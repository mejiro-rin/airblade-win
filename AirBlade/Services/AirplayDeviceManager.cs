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
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _stateGate = new();
    private readonly Dictionary<string, AirplayDevice> _devices = new(StringComparer.Ordinal);
    private string? _currentConnectedDeviceId;
    private bool _initialized;
    private int _disposed;

    public AirplayDeviceManager(DeviceSettingsService settings) : this(settings, new AirplaySessionService()) { }

    internal AirplayDeviceManager(DeviceSettingsService settings, AirplaySessionService session)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _session = session ?? throw new ArgumentNullException(nameof(session));
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
            lock (_stateGate)
            {
                foreach (var device in _devices.Values) device.MarkUndiscovered();
            }
            RaiseDevicesChanged();

            var timeout = _settings.GetGlobalSettings().DiscoveryTimeout;
            var discovered = await _session.DiscoverAsync(timeout, operationCancellation.Token).ConfigureAwait(false);
            lock (_stateGate)
            {
                foreach (var result in discovered)
                {
                    var device = _settings.ApplyDiscoveredDevice(result);
                    _devices[device.DeviceId] = device;
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
                if (!_devices.TryGetValue(deviceId, out device!)) throw new KeyNotFoundException($"找不到设备“{deviceId}”。");
                previousDeviceId = _currentConnectedDeviceId;
            }

            if (previousDeviceId is not null && !StringComparer.Ordinal.Equals(previousDeviceId, deviceId))
                await _session.DisconnectAsync(operationCancellation.Token).ConfigureAwait(false);

            var settings = _settings.GetDeviceSettings(deviceId);
            await _session.ConnectAsync($"{device.Address}:{device.Port}", settings.ConnectionPolicy, operationCancellation.Token).ConfigureAwait(false);
            await _settings.UpdateDeviceSettingsAsync(
                deviceId,
                value => value with { LastConnectedAt = DateTimeOffset.UtcNow },
                operationCancellation.Token).ConfigureAwait(false);

            lock (_stateGate)
            {
                _currentConnectedDeviceId = deviceId;
                device.ConnectionState = _session.Current.State;
                device.LastError = _session.Current.LastError;
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
            EnsureCurrentDevice(deviceId);
            await _session.SetVolumeAsync(volumeDb, operationCancellation.Token).ConfigureAwait(false);
            var settings = _settings.GetDeviceSettings(deviceId);
            if (settings.RememberVolume)
                await _settings.UpdateDeviceSettingsAsync(deviceId, value => value with { VolumeDb = volumeDb }, operationCancellation.Token).ConfigureAwait(false);
            lock (_stateGate) _devices[deviceId].VolumeDb = volumeDb;
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
        if (!_initialized) throw new InvalidOperationException("请先调用 InitializeAsync。");
    }

    private void EnsureCurrentDevice(string deviceId)
    {
        lock (_stateGate)
        {
            if (!StringComparer.Ordinal.Equals(_currentConnectedDeviceId, deviceId))
                throw new InvalidOperationException("只能操作当前连接的设备。");
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
        }
        RaiseDevicesChanged();
    }

    private void OnSessionVolumeChanged(object? sender, AirplayVolumeChangedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        lock (_stateGate)
        {
            if (_currentConnectedDeviceId is not null && _devices.TryGetValue(_currentConnectedDeviceId, out var device)) device.VolumeDb = args.VolumeDb;
        }
        RaiseDevicesChanged();
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
    private static AirplayDeviceSnapshot ToSnapshot(AirplayDevice device) => new(device.DeviceId, device.DisplayName, device.Address, device.Port, device.ConnectionState, device.VolumeDb, device.LastError, device.IsDiscovered);
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
            _operationGate.Release();
            await _session.DisposeAsync().ConfigureAwait(false);
            _lifetimeCancellation.Dispose();
            _operationGate.Dispose();
        }
    }
}
