using System.Collections.ObjectModel;
using AirBlade.Interop;
using AirBlade.Models;
using AirBlade.Services;
using Microsoft.UI.Xaml;

namespace AirBlade.ViewModels;

/// <summary>
/// 将设备管理器的快照和操作转换为界面可绑定状态。
/// </summary>
public sealed class DeviceManagerViewModel : ObservableObject, IDisposable
{
    private readonly AirplayDeviceManager _manager;
    private readonly bool _includeHidden;
    private readonly SynchronizationContext? _synchronizationContext;
    private readonly object _operationChainGate = new();
    private Task _pendingOperation = Task.CompletedTask;
    private bool _isBusy;
    private string? _errorMessage;
    private int _disposed;

    public DeviceManagerViewModel(AirplayDeviceManager manager, bool includeHidden = false)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _includeHidden = includeHidden;
        _synchronizationContext = SynchronizationContext.Current;
        DiscoverCommand = new AsyncCommand(_ => DiscoverAsync(), _ => !IsBusy);
        ConnectCommand = new AsyncCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncCommand(DisconnectAsync, CanDisconnect);
        ToggleConnectionCommand = new AsyncCommand(ToggleConnectionAsync, CanToggleConnection);
        SetVolumeCommand = new AsyncCommand(SetVolumeAsync, CanSetVolume);
        ToggleHiddenCommand = new AsyncCommand(ToggleHiddenAsync, parameter => !IsBusy && parameter is DeviceItemViewModel);
        _manager.DevicesChanged += OnDevicesChanged;
        LocalizationService.Current.LanguageChanged += OnLanguageChanged;
        RefreshDevices();
    }

    public ObservableCollection<DeviceItemViewModel> Devices { get; } = [];
    public AsyncCommand DiscoverCommand { get; }
    public AsyncCommand ConnectCommand { get; }
    public AsyncCommand DisconnectCommand { get; }
    public AsyncCommand ToggleConnectionCommand { get; }
    public AsyncCommand SetVolumeCommand { get; }
    public AsyncCommand ToggleHiddenCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RefreshCommands(); } }
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(ErrorVisibility));
            }
        }
    }
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public Visibility ErrorVisibility => HasError ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyStateText => Devices.Count == 0 ? LocalizationService.Current["Device.EmptyState"] : string.Empty;
    public string AppVersion => LocalizationService.Current.Format("Common.VersionFormat", typeof(DeviceManagerViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    public void ShowError(Exception exception) => ErrorMessage = exception?.Message ?? throw new ArgumentNullException(nameof(exception));

    public async Task DiscoverAsync() => await ExecuteOperationAsync(null, () => _manager.DiscoverAsync());
    public async Task ConnectAsync(object? parameter) => await ExecuteOperationAsync(AsDevice(parameter), device => _manager.ConnectAsync(device.DeviceId));
    public async Task DisconnectAsync(object? parameter) => await ExecuteOperationAsync(AsDevice(parameter), device => _manager.DisconnectAsync(device.DeviceId));
    public async Task ToggleConnectionAsync(object? parameter) => await ExecuteOperationAsync(AsDevice(parameter), async device =>
    {
        if (device.ConnectionState is AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming)
            await _manager.DisconnectAsync(device.DeviceId);
        else await _manager.ConnectAsync(device.DeviceId);
    });
    public async Task SetVolumeAsync(object? parameter)
    {
        var device = AsDevice(parameter);
        if (device is null) return;
        // 调用时立即捕获滑块值，避免排队等待期间快照刷新覆盖滑块后读不到用户的最新选择。
        var volumeDb = DeviceItemViewModel.ToVolumeDb(device.EditableVolume);
        await ExecuteOperationAsync(device, () => _manager.SetVolumeAsync(device.DeviceId, volumeDb));
    }
    public async Task SetAutoConnectAsync(DeviceItemViewModel device, bool autoConnect)
    {
        if (device is null) return;
        await ExecuteOperationAsync(device, () => _manager.SetAutoConnectAsync(device.DeviceId, autoConnect));
    }
    public async Task RemoveDeviceAsync(DeviceItemViewModel device)
    {
        if (device is null) return;
        await ExecuteOperationAsync(device, () => _manager.RemoveDeviceAsync(device.DeviceId));
    }
    public async Task ToggleHiddenAsync(object? parameter) => await ExecuteOperationAsync(AsDevice(parameter), device => _manager.SetDeviceHiddenAsync(device.DeviceId, !device.IsHidden));

    public void RefreshDevices()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var existing = Devices.ToDictionary(item => item.DeviceId, StringComparer.Ordinal);
        var snapshots = _manager.GetDevices(includeHidden: true)
            .Where(snapshot => _includeHidden ||
                (!snapshot.IsHidden &&
                 (snapshot.IsDiscovered ||
                  snapshot.ConnectionState is AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming)))
            .OrderBy(snapshot => snapshot.DisplayName, StringComparer.CurrentCulture)
            .ThenBy(snapshot => snapshot.DeviceId, StringComparer.Ordinal)
            .ToArray();
        var desiredDevices = new List<DeviceItemViewModel>(snapshots.Length);
        foreach (var snapshot in snapshots)
        {
            // 按钮形态由连接状态驱动：活跃连接中显示“断开/暂停”，
            // 断联或失败后自动回到“连接/播放”，即使会话仍关联该设备（等待自动重连恢复）。
            var isCurrentDevice = snapshot.ConnectionState is AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming;
            if (existing.TryGetValue(snapshot.DeviceId, out var item)) item.Update(snapshot, isCurrentDevice);
            else
            {
                item = new DeviceItemViewModel(snapshot);
                item.UpdateConnectionAvailability(isCurrentDevice);
            }
            desiredDevices.Add(item);
        }

        for (var index = 0; index < desiredDevices.Count; index++)
        {
            var currentIndex = Devices.IndexOf(desiredDevices[index]);
            if (currentIndex < 0) Devices.Insert(index, desiredDevices[index]);
            else if (currentIndex != index) Devices.Move(currentIndex, index);
        }
        for (var index = Devices.Count - 1; index >= desiredDevices.Count; index--)
        {
            Devices.RemoveAt(index);
        }
        OnPropertyChanged(nameof(EmptyStateText));
        RefreshCommands();
    }

    private async Task ExecuteOperationAsync(DeviceItemViewModel? device, Func<Task> action)
    {
        var operation = QueueOperationAsync(device, action);
        await operation;
    }

    private Task QueueOperationAsync(DeviceItemViewModel? device, Func<Task> action)
    {
        lock (_operationChainGate)
        {
            var predecessor = _pendingOperation;
            var operation = RunOperationAsync(device, action, predecessor);
            _pendingOperation = operation;
            return operation;
        }
    }

    private Task ExecuteOperationAsync(DeviceItemViewModel? device, Func<DeviceItemViewModel, Task> action)
    {
        if (device is null) return Task.CompletedTask;
        return ExecuteOperationAsync(device, () => action(device));
    }

    /// <summary>
    /// 串行执行界面操作：等待前一个操作结束后再开始本次操作。
    /// 之前直接在忙碌时丢弃请求，导致滑块在发现扫描期间调音量没反应，
    /// 扫描结束刷新快照时又把滑块回弹到旧值。
    /// </summary>
    private async Task RunOperationAsync(DeviceItemViewModel? device, Func<Task> action, Task predecessor)
    {
        try { await predecessor; }
        catch { /* 前置操作已自行处理异常，本次操作继续执行 */ }
        IsBusy = true;
        ErrorMessage = null;
        device?.ClearOperationError();
        try
        {
            await action();
            RefreshDevices();
        }
        catch (Exception exception)
        {
            ErrorMessage = exception.Message;
            device?.SetOperationError(exception);
            RefreshCommands();
        }
        finally { IsBusy = false; }
    }

    private bool CanConnect(object? parameter)
    {
        if (IsBusy || parameter is not DeviceItemViewModel device || !device.IsDiscovered) return false;
        return device.ConnectionState is not (AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming);
    }

    private bool CanDisconnect(object? parameter) => !IsBusy && parameter is DeviceItemViewModel device
        && device.ConnectionState is AirplaySessionState.Pairing or AirplaySessionState.Connecting or AirplaySessionState.Streaming;
    private bool CanSetVolume(object? parameter) => !IsBusy && parameter is DeviceItemViewModel device && device.IsDiscovered;
    private bool CanToggleConnection(object? parameter) => CanConnect(parameter) || CanDisconnect(parameter);
    private static DeviceItemViewModel? AsDevice(object? parameter) => parameter as DeviceItemViewModel;

    private void OnDevicesChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_synchronizationContext is null) RefreshDevices();
        else _synchronizationContext.Post(_ => RefreshDevices(), null);
    }

    /// <summary>
    /// 语言切换后刷新全部文本状态，并确保回到 UI 线程。
    /// </summary>
    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (_synchronizationContext is null) RefreshLocalizedText();
        else _synchronizationContext.Post(_ => RefreshLocalizedText(), null);
    }

    private void RefreshLocalizedText()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        OnPropertyChanged(nameof(EmptyStateText));
        OnPropertyChanged(nameof(AppVersion));
        foreach (var device in Devices) device.RefreshLocalization();
    }

    private void RefreshCommands()
    {
        DiscoverCommand.RaiseCanExecuteChanged();
        ConnectCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        ToggleConnectionCommand.RaiseCanExecuteChanged();
        SetVolumeCommand.RaiseCanExecuteChanged();
        ToggleHiddenCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _manager.DevicesChanged -= OnDevicesChanged;
        LocalizationService.Current.LanguageChanged -= OnLanguageChanged;
    }
}
