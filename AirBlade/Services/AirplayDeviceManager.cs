using AirBlade.Interop;
using AirBlade.Models;

namespace AirBlade.Services;

/// <summary>
/// 协调设备发现、用户配置和唯一的 AirPlay 会话。
/// </summary>
public sealed class AirplayDeviceManager : IAsyncDisposable
{
    private readonly DeviceSettingsService _settings;
    private readonly AirplaySessionService _session;
    private readonly ISystemMuteController _systemMute;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _stateGate = new();
    private readonly Dictionary<string, AirplayDevice> _devices = new(StringComparer.Ordinal);
    private string? _currentConnectedDeviceId;
    // 标记当前系统静音是否由本功能接管（连接成功后自动静音），断开或退出时恢复。
    private bool _systemMuteOwned;
    private bool _initialized;
    private int _disposed;

    public AirplayDeviceManager(DeviceSettingsService settings) : this(settings, new AirplaySessionService(), new SystemMuteController()) { }

    internal AirplayDeviceManager(DeviceSettingsService settings, AirplaySessionService session, ISystemMuteController? systemMute = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _systemMute = systemMute ?? new SystemMuteController();
    }

    public event EventHandler? DevicesChanged;

    public string? CurrentConnectedDeviceId
    {
        get { lock (_stateGate) return _currentConnectedDeviceId; }
    }

    public IReadOnlyList<AirplayDeviceSnapshot> GetDevices(bool includeHidden = false)
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            return _devices.Values
                .Where(device => includeHidden || !_settings.GetDeviceSettings(device.DeviceId).Hidden)
                .Select(ToSnapshot)
                .ToArray();
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        await EnterOperationAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await _settings.InitializeAsync(operationCancellation.Token).ConfigureAwait(false);
            _session.StateChanged += OnSessionStateChanged;
            _session.VolumeChanged += OnSessionVolumeChanged;
            _session.ErrorOccurred += OnSessionErrorOccurred;
            try
            {
                await _session.InitializeAsync(operationCancellation.Token).ConfigureAwait(false);
                RestoreRememberedDevices();
                _initialized = true;
            }
            catch
            {
                UnsubscribeSessionEvents();
                throw;
            }
        }
        finally { _operationGate.Release(); }
    }

    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        await EnterOperationAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            var timeout = _settings.GetGlobalSettings().DiscoveryTimeout;
            var discovered = await _session.DiscoverAsync(timeout, operationCancellation.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                // 本轮发现到的设备：更新信息，ApplyDiscoveredDevice 内部会重置消失计数。
                foreach (var result in discovered)
                {
                    var device = _settings.ApplyDiscoveredDevice(result);
                    _devices[device.DeviceId] = device;
                }

                // 未发现的设备：活跃连接中的绝不标记（活跃会话就是在线证据）；
                // 其余设备（未连接或已断联）本轮扫描不到即可标记未发现，及时反映消失，不延后。
                var discoveredIds = discovered.Select(result => result.DeviceId).ToHashSet(StringComparer.Ordinal);
                foreach (var device in _devices.Values)
                {
                    if (discoveredIds.Contains(device.DeviceId)) continue;

                    var isCurrentDevice = _currentConnectedDeviceId is not null &&
                        StringComparer.Ordinal.Equals(device.DeviceId, _currentConnectedDeviceId);
                    var activelyConnected = isCurrentDevice &&
                        device.ConnectionState is AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming;
                    if (!activelyConnected)
                    {
                        device.MarkUndiscovered();
                    }
                }
            }
            RaiseDevicesChanged();
        }
        finally { _operationGate.Release(); }
    }

    public async Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        await EnterOperationAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            AirplayDevice device;
            string? previousDeviceId;
            lock (_stateGate)
            {
                if (!_devices.TryGetValue(deviceId, out device!)) throw new KeyNotFoundException(LocalizationService.Current.Format("Error.DeviceNotFound", deviceId));
                previousDeviceId = _currentConnectedDeviceId;
            }

            if (previousDeviceId is not null && !StringComparer.Ordinal.Equals(previousDeviceId, deviceId))
                await _session.DisconnectAsync(operationCancellation.Token).ConfigureAwait(false);

            var settings = _settings.GetDeviceSettings(deviceId);
            await _session.ConnectAsync($"{device.Address}:{device.Port}", settings.ConnectionPolicy, operationCancellation.Token).ConfigureAwait(false);
            await _settings.UpdateDeviceSettingsAsync(
                deviceId,
                value => value with
                {
                    // 连接成功后记忆设备信息，作为长期记忆供重启恢复。
                    DisplayName = device.DisplayName,
                    Address = device.Address,
                    Port = device.Port,
                    LastConnectedAt = DateTimeOffset.UtcNow,
                },
                operationCancellation.Token).ConfigureAwait(false);

            lock (_stateGate)
            {
                _currentConnectedDeviceId = deviceId;
                // 轮询事件尚未到达前，先乐观显示“正在连接”，让按钮立即进入断开/暂停态；
                // 后续 Pairing/Streaming 事件会覆盖为真实状态。
                device.ConnectionState = _session.Current.State is AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming
                    ? _session.Current.State
                    : AirplaySessionState.Connecting;
                // 连接成功即视为恢复正常，清除此前残留的错误，避免红色警告一直挂在卡片上。
                device.LastError = null;
            }

            if (settings.RememberVolume)
            {
                await _session.SetVolumeAsync(settings.VolumeDb, operationCancellation.Token).ConfigureAwait(false);
                lock (_stateGate) device.VolumeDb = settings.VolumeDb;
            }
            RaiseDevicesChanged();
        }
        finally { _operationGate.Release(); }
    }

    public async Task DisconnectAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        await EnterOperationAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            EnsureCurrentDevice(deviceId);
            await _session.DisconnectAsync(operationCancellation.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                _currentConnectedDeviceId = null;
                if (_devices.TryGetValue(deviceId, out var device)) device.ConnectionState = AirplaySessionState.Disconnected;
                ApplySystemMutePolicyCore(active: false);
            }
            RaiseDevicesChanged();
        }
        finally { _operationGate.Release(); }
    }

    public async Task SetVolumeAsync(string deviceId, float volumeDb, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        await EnterOperationAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            lock (_stateGate)
            {
                if (!_devices.ContainsKey(deviceId)) throw new KeyNotFoundException(LocalizationService.Current.Format("Error.DeviceNotFound", deviceId));
            }

            // 无论是否已连接都持久化音量，作为连接前预设；已连接时同时实时下发到设备。
            await _settings.UpdateDeviceSettingsAsync(
                deviceId,
                value => value with { VolumeDb = volumeDb, RememberVolume = true },
                operationCancellation.Token).ConfigureAwait(false);
            if (StringComparer.Ordinal.Equals(CurrentConnectedDeviceId, deviceId))
                await _session.SetVolumeAsync(volumeDb, operationCancellation.Token).ConfigureAwait(false);
            lock (_stateGate) _devices[deviceId].VolumeDb = volumeDb;
            RaiseDevicesChanged();
        }
        finally { _operationGate.Release(); }
    }

    /// <summary>
    /// 启动时把配置文件里记忆过的设备恢复到运行时列表（离线状态），
    /// 这样即使设备当前不在线，设置页也能看到并保留其配置。
    /// </summary>
    private void RestoreRememberedDevices()
    {
        lock (_stateGate)
        {
            foreach (var (deviceId, settings) in _settings.GetRememberedDevices())
            {
                if (settings.Address is null || settings.Port is not > 0) continue;
                if (_devices.ContainsKey(deviceId)) continue;
                var remembered = new AirplayDevice(
                    deviceId,
                    string.IsNullOrWhiteSpace(settings.DisplayName) ? deviceId : settings.DisplayName,
                    settings.Address,
                    settings.Port.Value);
                // 渲染时就带上记忆音量，避免连接前滑块一直显示默认值。
                if (settings.RememberVolume) remembered.VolumeDb = settings.VolumeDb;
                _devices.Add(deviceId, remembered);
            }
        }
    }

    /// <summary>
    /// 更新设备在快捷列表中的可见性，并立即通知界面刷新。
    /// </summary>
    public async Task SetDeviceHiddenAsync(string deviceId, bool hidden, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        await EnterOperationAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            lock (_stateGate)
            {
                if (!_devices.ContainsKey(deviceId)) throw new KeyNotFoundException(LocalizationService.Current.Format("Error.DeviceNotFound", deviceId));
            }
            await _settings.UpdateDeviceSettingsAsync(
                deviceId,
                value => value with { Hidden = hidden },
                operationCancellation.Token).ConfigureAwait(false);
            RaiseDevicesChanged();
        }
        finally { _operationGate.Release(); }
    }

    /// <summary>
    /// 更新设备是否自动连接，设置页的开关直接持久化到这里。
    /// </summary>
    public async Task SetAutoConnectAsync(string deviceId, bool autoConnect, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        await EnterOperationAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            lock (_stateGate)
            {
                if (!_devices.ContainsKey(deviceId)) throw new KeyNotFoundException(LocalizationService.Current.Format("Error.DeviceNotFound", deviceId));
            }
            await _settings.UpdateDeviceSettingsAsync(
                deviceId,
                value => value with { AutoConnect = autoConnect },
                operationCancellation.Token).ConfigureAwait(false);
            RaiseDevicesChanged();
        }
        finally { _operationGate.Release(); }
    }

    /// <summary>
    /// 启动阶段调用：自动连接最近连接过且当前已发现的自动连接设备。
    /// 没有符合条件的设备时返回 false，不做任何事。
    /// </summary>
    public async Task<bool> AutoConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureInitialized();
        string? target;
        lock (_stateGate)
        {
            target = _devices.Values
                .Where(device => device.IsDiscovered)
                .Select(device => (Device: device, Settings: _settings.GetDeviceSettings(device.DeviceId)))
                .Where(item => item.Settings.AutoConnect)
                .OrderByDescending(item => item.Settings.LastConnectedAt)
                .Select(item => item.Device.DeviceId)
                .FirstOrDefault();
        }
        if (target is null) return false;
        await ConnectAsync(target, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 设置保存后立即应用“连接后静音电脑”策略：
    /// 当前正在播放且开关开启则静音，否则恢复本功能造成的静音。
    /// </summary>
    public void RefreshSystemMutePolicy()
    {
        ThrowIfDisposed();
        lock (_stateGate)
        {
            var isStreaming = _currentConnectedDeviceId is not null
                && _devices.TryGetValue(_currentConnectedDeviceId, out var device)
                && device.ConnectionState == AirplaySessionState.Streaming;
            ApplySystemMutePolicyCore(isStreaming);
        }
    }

    /// <summary>
    /// 忘记设备：断开连接（如正在播放），从运行时列表移除并删除全部记忆配置。
    /// 删除后设备重新被发现时会作为新设备出现。
    /// </summary>
    public async Task RemoveDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ThrowIfDisposed();
        using var operationCancellation = CreateOperationCancellation(cancellationToken);
        await EnterOperationAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            lock (_stateGate)
            {
                if (!_devices.ContainsKey(deviceId)) throw new KeyNotFoundException(LocalizationService.Current.Format("Error.DeviceNotFound", deviceId));
            }

            // 正在播放该设备时先断开，再移除记忆，避免留下无法操作的连接状态。
            if (StringComparer.Ordinal.Equals(CurrentConnectedDeviceId, deviceId))
            {
                await _session.DisconnectAsync(operationCancellation.Token).ConfigureAwait(false);
                lock (_stateGate) _currentConnectedDeviceId = null;
            }

            lock (_stateGate) _devices.Remove(deviceId);
            await _settings.RemoveDeviceAsync(deviceId, operationCancellation.Token).ConfigureAwait(false);
            RaiseDevicesChanged();
        }
        finally { _operationGate.Release(); }
    }

    private async Task EnterOperationAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
    }

    private CancellationTokenSource CreateOperationCancellation(CancellationToken cancellationToken) =>
        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);

    private void EnsureInitialized()
    {
        if (!_initialized) throw new InvalidOperationException(LocalizationService.Current["Error.InitializeFirst"]);
    }

    private void EnsureCurrentDevice(string deviceId)
    {
        lock (_stateGate)
        {
            if (!StringComparer.Ordinal.Equals(_currentConnectedDeviceId, deviceId))
                throw new InvalidOperationException(LocalizationService.Current["Error.CurrentDeviceOnly"]);
        }
    }

    private void OnSessionStateChanged(object? sender, AirplayStateChangedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_stateGate)
        {
            if (_currentConnectedDeviceId is not null && _devices.TryGetValue(_currentConnectedDeviceId, out var device))
            {
                device.ConnectionState = args.Current.State;
                device.VolumeDb = args.Current.VolumeDb;
                device.LastError = args.Current.LastError;
            }
            ApplySystemMutePolicyCore(active: args.Current.State == AirplaySessionState.Streaming);
        }
        RaiseDevicesChanged();
    }

    /// <summary>
    /// 按连接状态与全局开关应用静音策略：
    /// 连接成功时若电脑当前未静音则由本功能静音；断开时只恢复本功能造成的静音，
    /// 用户原本的静音状态不受影响。
    /// </summary>
    private void ApplySystemMutePolicyCore(bool active)
    {
        if (active)
        {
            if (_systemMuteOwned || !_settings.GetGlobalSettings().MuteComputerWhenConnected) return;
            if (_systemMute.GetMuted() == false)
            {
                _systemMute.SetMuted(true);
                _systemMuteOwned = true;
            }
            return;
        }
        RestoreSystemMuteCore();
    }

    private void RestoreSystemMuteCore()
    {
        if (!_systemMuteOwned) return;
        _systemMute.SetMuted(false);
        _systemMuteOwned = false;
    }

    private void OnSessionVolumeChanged(object? sender, AirplayVolumeChangedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        string? deviceId;
        lock (_stateGate)
        {
            deviceId = _currentConnectedDeviceId;
            if (deviceId is not null && _devices.TryGetValue(deviceId, out var device)) device.VolumeDb = args.VolumeDb;
        }
        // 远端（HomePod/iOS）调节音量后同步持久化，让下次启动能恢复真实音量。
        if (deviceId is not null) _ = PersistRemoteVolumeAsync(deviceId, args.VolumeDb);
        RaiseDevicesChanged();
    }

    private async Task PersistRemoteVolumeAsync(string deviceId, float volumeDb)
    {
        try
        {
            await _settings.UpdateDeviceSettingsAsync(deviceId, value => value with { VolumeDb = volumeDb, RememberVolume = true }).ConfigureAwait(false);
        }
        catch
        {
            // 音量回写失败不影响播放，避免轮询线程抛出未处理异常。
        }
    }

    private void OnSessionErrorOccurred(object? sender, AirplayCoreException error)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_stateGate)
        {
            if (_currentConnectedDeviceId is not null && _devices.TryGetValue(_currentConnectedDeviceId, out var device)) device.LastError = error;
        }
        RaiseDevicesChanged();
    }

    private void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
    private AirplayDeviceSnapshot ToSnapshot(AirplayDevice device) => new(
        device.DeviceId,
        device.DisplayName,
        device.Address,
        device.Port,
        device.ConnectionState,
        device.VolumeDb,
        device.LastError,
        device.IsDiscovered,
        _settings.GetDeviceSettings(device.DeviceId).Hidden,
        _settings.GetDeviceSettings(device.DeviceId).AutoConnect);
    private void UnsubscribeSessionEvents()
    {
        _session.StateChanged -= OnSessionStateChanged;
        _session.VolumeChanged -= OnSessionVolumeChanged;
        _session.ErrorOccurred -= OnSessionErrorOccurred;
    }
    private void ThrowIfDisposed() { if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(AirplayDeviceManager)); }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetimeCancellation.Cancel();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try { UnsubscribeSessionEvents(); }
        finally
        {
            lock (_stateGate) RestoreSystemMuteCore();
            _operationGate.Release();
            await _session.DisposeAsync().ConfigureAwait(false);
            _lifetimeCancellation.Dispose();
            _operationGate.Dispose();
        }
    }
}
