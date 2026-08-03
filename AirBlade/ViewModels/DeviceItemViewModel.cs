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
    /// 界面默认音量 50%，对应底层 -72 dB（-144 到 0 的中点）。
    /// </summary>
    public const double DefaultVolumePercent = 50;

    private AirplayDeviceSnapshot _snapshot;
    private double _editableVolume;
    private string? _operationErrorMessage;
    private bool _canAdjustVolume;
    private bool _isCurrentDevice;

    public DeviceItemViewModel(AirplayDeviceSnapshot snapshot)
    {
        _snapshot = snapshot;
        _editableVolume = snapshot.VolumeDb is float volume ? ToPercent(volume) : DefaultVolumePercent;
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
    public string ActionGlyph => IsCurrentDevice ? "\uE71A" : "\uE768";
    public string ActionToolTip => IsCurrentDevice ? T["Device.Stop"] : T["Device.PlayHere"];
    public string ConnectionActionText => IsCurrentDevice ? T["Device.Disconnect"] : T["Device.Connect"];

    public double EditableVolume
    {
        get => _editableVolume;
        set => SetProperty(ref _editableVolume, Math.Clamp(value, 0, 100));
    }

    /// <summary>
    /// 把 -144 到 0 dB 线性映射为 0 到 100 百分比。
    /// </summary>
    public static double ToPercent(float volumeDb) => Math.Clamp((volumeDb + 144) / 1.44, 0, 100);

    /// <summary>
    /// 把 0 到 100 百分比线性映射回 -144 到 0 dB。
    /// </summary>
    public static float ToVolumeDb(double percent) => (float)Math.Clamp(-144 + 1.44 * percent, -144, 0);

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
        OnPropertyChanged(nameof(ErrorMessage));
    }

    public void Update(AirplayDeviceSnapshot snapshot, bool isCurrentDevice)
    {
        _snapshot = snapshot;
        IsCurrentDevice = isCurrentDevice;
        // 未连接时也可以预设音量，连接时再应用到设备。
        CanAdjustVolume = snapshot.IsDiscovered;
        if (snapshot.VolumeDb is float volume) EditableVolume = ToPercent(volume);
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
