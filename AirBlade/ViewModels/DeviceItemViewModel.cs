using AirBlade.Models;
using AirBlade.Interop;
using AirBlade.Services;

namespace AirBlade.ViewModels;

/// <summary>
/// 面向单行设备卡片的可绑定状态。
/// </summary>
public sealed class DeviceItemViewModel : ObservableObject
{
    /// <summary>
    /// 界面默认音量 50%，对应有效音量范围中点 -18 dB。
    /// </summary>
    public const double DefaultVolumePercent = 50;

    /// <summary>
    /// AirPlay 静音哨兵值（dB），苹果用 -144 表示静音。
    /// </summary>
    public const float MuteVolumeDb = -144f;

    /// <summary>
    /// HomePod 有效音量范围下限（dB），等于新映射中滑块 16 对应的分贝值（-36 + 16 × 0.36），
    /// 即听感实测下限，之后可继续按实测调整。
    /// </summary>
    public const float EffectiveMinVolumeDb = -30.24f;

    /// <summary>
    /// 有效音量范围每 1% 对应的分贝步长（-36 到 0 分成 100 等份）。
    /// </summary>
    private const double EffectiveVolumeStepDb = (0f - EffectiveMinVolumeDb) / 100.0;

    private AirplayDeviceSnapshot _snapshot;
    private double _editableVolume;
    private bool _autoConnect;
    private bool _isAdjustingVolume;
    private string? _operationErrorMessage;
    private bool _canAdjustVolume;
    private bool _isCurrentDevice;

    public DeviceItemViewModel(AirplayDeviceSnapshot snapshot)
    {
        _snapshot = snapshot;
        _editableVolume = snapshot.VolumeDb is float volume ? ToPercent(volume) : DefaultVolumePercent;
        _autoConnect = snapshot.AutoConnect;
    }

    public string DeviceId => _snapshot.DeviceId;
    public string DisplayName => _snapshot.DisplayName;
    public string Endpoint => $"{_snapshot.Address}:{_snapshot.Port}";
    public bool IsDiscovered => _snapshot.IsDiscovered;
    public bool IsHidden => _snapshot.IsHidden;
    public AirplaySessionState ConnectionState => _snapshot.ConnectionState;
    public string DiscoveryStatusText => IsDiscovered ? T["Device.Discovered"] : T["Device.Unavailable"];
    public string ConnectionStatusText => ConnectionState switch
    {
        AirplaySessionState.Idle => T["Device.State.Idle"],
        AirplaySessionState.Pairing => T["Device.State.Pairing"],
        AirplaySessionState.Connecting => T["Device.State.Connecting"],
        AirplaySessionState.Streaming => T["Device.State.Streaming"],
        AirplaySessionState.Disconnected => T["Device.State.Disconnected"],
        AirplaySessionState.Failed => T["Device.State.Failed"],
        _ => T["Device.State.Unknown"],
    };
    public string HiddenActionText => IsHidden ? T["Device.Show"] : T["Device.Hide"];
    public string? ErrorMessage => _snapshot.LastError?.Message ?? _operationErrorMessage;
    public bool CanAdjustVolume { get => _canAdjustVolume; private set => SetProperty(ref _canAdjustVolume, value); }
    public bool IsCurrentDevice { get => _isCurrentDevice; private set => SetProperty(ref _isCurrentDevice, value); }
    // 未连接时显示播放图标（点击连接），已连接时显示暂停图标（点击断开）。
    public string ActionGlyph => IsCurrentDevice ? "\uE769" : "\uE768";
    public string ActionToolTip => IsCurrentDevice ? T["Device.Stop"] : T["Device.PlayHere"];
    public string ConnectionActionText => IsCurrentDevice ? T["Device.Disconnect"] : T["Device.Connect"];
    public string AutoConnectLabel => T["Device.AutoConnect"];
    public string ForgetActionText => T["Device.Forget"];
    public bool AutoConnect { get => _autoConnect; private set => SetProperty(ref _autoConnect, value); }

    /// <summary>
    /// 用户正在拖动音量滑块时为 true：快照刷新不回写滑块，避免拖动过程中被旧值拉回。
    /// </summary>
    public bool IsAdjustingVolume { get => _isAdjustingVolume; set => SetProperty(ref _isAdjustingVolume, value); }

    public double EditableVolume
    {
        get => _editableVolume;
        set => SetProperty(ref _editableVolume, Math.Clamp(value, 0, 100));
    }

    /// <summary>
    /// 把 dB 映射回滑块值：-144（静音哨兵）显示为 0，有效范围（EffectiveMinVolumeDb 到 0）线性映射为 1 到 100。
    /// </summary>
    public static double ToPercent(float volumeDb)
    {
        if (volumeDb <= MuteVolumeDb) return 0;
        return Math.Clamp((volumeDb - EffectiveMinVolumeDb) / EffectiveVolumeStepDb, 1, 100);
    }

    /// <summary>
    /// 把滑块值映射为 dB：0 专门留给静音（-144），1 到 100 线性映射有效音量范围（EffectiveMinVolumeDb 到 0）。
    /// </summary>
    public static float ToVolumeDb(double percent)
    {
        if (percent < 0.5) return MuteVolumeDb;
        return (float)Math.Clamp(EffectiveMinVolumeDb + EffectiveVolumeStepDb * percent, EffectiveMinVolumeDb, 0);
    }

    /// <summary>
    /// 语言切换后重新触发文本属性变更通知。
    /// </summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DiscoveryStatusText));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(HiddenActionText));
        OnPropertyChanged(nameof(ActionToolTip));
        OnPropertyChanged(nameof(ConnectionActionText));
        OnPropertyChanged(nameof(AutoConnectLabel));
        OnPropertyChanged(nameof(ForgetActionText));
        OnPropertyChanged(nameof(ErrorMessage));
    }

    public void Update(AirplayDeviceSnapshot snapshot, bool isCurrentDevice)
    {
        _snapshot = snapshot;
        IsCurrentDevice = isCurrentDevice;
        // 未连接时也可以预设音量，连接时再应用到设备。
        CanAdjustVolume = snapshot.IsDiscovered;
        if (snapshot.VolumeDb is float volume && !IsAdjustingVolume) EditableVolume = ToPercent(volume);
        AutoConnect = snapshot.AutoConnect;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(IsDiscovered));
        OnPropertyChanged(nameof(IsHidden));
        OnPropertyChanged(nameof(ConnectionState));
        OnPropertyChanged(nameof(DiscoveryStatusText));
        OnPropertyChanged(nameof(ConnectionStatusText));
        OnPropertyChanged(nameof(HiddenActionText));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(ActionGlyph));
        OnPropertyChanged(nameof(ActionToolTip));
        OnPropertyChanged(nameof(ConnectionActionText));
    }

    public void UpdateConnectionAvailability(bool isCurrentDevice)
    {
        IsCurrentDevice = isCurrentDevice;
        CanAdjustVolume = IsDiscovered;
        OnPropertyChanged(nameof(ActionGlyph));
        OnPropertyChanged(nameof(ActionToolTip));
        OnPropertyChanged(nameof(ConnectionActionText));
    }

    public void SetOperationError(Exception exception)
    {
        _operationErrorMessage = exception.Message;
        OnPropertyChanged(nameof(ErrorMessage));
    }

    public void ClearOperationError()
    {
        if (_operationErrorMessage is null) return;
        _operationErrorMessage = null;
        OnPropertyChanged(nameof(ErrorMessage));
    }

    private static LocalizationService T => LocalizationService.Current;
}
