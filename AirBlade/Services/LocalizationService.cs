using AirBlade.Models;

namespace AirBlade.Services;

/// <summary>
/// 轻量级界面文本本地化服务：以键值对保存中英文文本，切换语言后触发事件通知界面刷新。
/// </summary>
public sealed class LocalizationService
{
    /// <summary>
    /// 全局唯一的本地化实例。
    /// </summary>
    public static LocalizationService Current { get; } = new();

    private static readonly Dictionary<string, (string Chinese, string English)> Texts = new(StringComparer.Ordinal)
    {
        // 通用
        ["Common.VersionFormat"] = ("版本 {0}", "Version {0}"),

        // 快捷窗
        ["MainWindow.MoreToolTip"] = ("更多设置", "More settings"),
        ["MainWindow.MinimizeToolTip"] = ("最小化", "Minimize"),
        ["Device.EmptyState"] = ("未搜索到设备", "No devices found"),

        // 设备状态与操作
        ["Device.Discovered"] = ("已发现", "Discovered"),
        ["Device.Unavailable"] = ("当前不可用", "Currently unavailable"),
        ["Device.State.Idle"] = ("未连接", "Not connected"),
        ["Device.State.Pairing"] = ("正在配对", "Pairing"),
        ["Device.State.Connecting"] = ("正在连接", "Connecting"),
        ["Device.State.Streaming"] = ("已连接", "Connected"),
        ["Device.State.Disconnected"] = ("已断开", "Disconnected"),
        ["Device.State.Failed"] = ("连接失败", "Connection failed"),
        ["Device.State.Unknown"] = ("未知状态", "Unknown state"),
        ["Device.Show"] = ("显示设备", "Show device"),
        ["Device.Hide"] = ("隐藏设备", "Hide device"),
        ["Device.Stop"] = ("停止播放", "Stop playing"),
        ["Device.PlayHere"] = ("播放到此设备", "Play to this device"),
        ["Device.Connect"] = ("连接", "Connect"),
        ["Device.Disconnect"] = ("断开", "Disconnect"),
        ["Device.AutoConnect"] = ("自动连接", "Auto connect"),
        ["Device.Forget"] = ("忘记设备", "Forget device"),

        // 设置窗口
        ["Settings.Title"] = ("AirBlade 设置", "AirBlade Settings"),
        ["Settings.Minimize"] = ("最小化", "Minimize"),
        ["Settings.Maximize"] = ("最大化", "Maximize"),
        ["Settings.Restore"] = ("还原", "Restore"),
        ["Settings.Close"] = ("关闭", "Close"),
        ["Settings.Nav.Devices"] = ("设备", "Devices"),
        ["Settings.Nav.General"] = ("通用", "General"),
        ["Settings.Nav.Appearance"] = ("外观", "Appearance"),
        ["Settings.Nav.About"] = ("关于", "About"),
        ["Settings.Devices.Title"] = ("AirPlay 设备", "AirPlay devices"),
        ["Settings.Devices.DiscoverToolTip"] = ("手动发现", "Discover manually"),
        ["Settings.General.Title"] = ("通用", "General"),
        ["Settings.General.Language"] = ("语言", "Language"),
        ["Settings.General.LanguageDescription"] = ("选择应用界面显示语言", "Choose the display language of the app"),
        ["Settings.General.TogglePlaceholder"] = ("开关设置", "Toggle setting"),
        ["Settings.General.TogglePlaceholderDescription"] = ("开关类型设置项（占位）", "Toggle-type setting (placeholder)"),
        ["Settings.General.ToggleOn"] = ("开", "On"),
        ["Settings.General.ToggleOff"] = ("关", "Off"),
        ["Settings.General.OptionPlaceholder"] = ("选择设置", "Selection setting"),
        ["Settings.General.OptionPlaceholderDescription"] = ("选择类型设置项（占位）", "Selection-type setting (placeholder)"),
        ["Settings.General.Option1"] = ("选项一", "Option 1"),
        ["Settings.General.Option2"] = ("选项二", "Option 2"),
        ["Settings.General.Option3"] = ("选项三", "Option 3"),
        ["Settings.Save"] = ("保存", "Save"),
        ["Settings.Appearance.Title"] = ("外观", "Appearance"),
        ["Settings.Appearance.Theme"] = ("常用程序主题", "App theme"),
        ["Settings.Appearance.ThemeDescription"] = ("跟随系统，或固定为深色、浅色", "Follow the system, or use dark or light mode"),
        ["Settings.Appearance.ThemeFollowSystem"] = ("跟随系统", "System"),
        ["Settings.Appearance.ThemeDark"] = ("深色", "Dark"),
        ["Settings.Appearance.ThemeLight"] = ("浅色", "Light"),
        ["Settings.About.Title"] = ("关于", "About"),
        ["Settings.About.Copyright"] = ("版权", "Copyright"),
        ["Settings.About.CopyrightDescription"] = ("© 2026 AirBlade 贡献者，保留所有权利。（占位）", "© 2026 AirBlade contributors. All rights reserved. (placeholder)"),
        ["Settings.About.Repository"] = ("代码仓库", "Repository"),

        // 系统托盘菜单
        ["Tray.OpenConsole"] = ("打开控制台", "Open console"),
        ["Tray.OpenSettings"] = ("打开设置", "Open settings"),
        ["Tray.Exit"] = ("退出", "Exit"),

        // 用户可见错误消息
        ["Error.NativeOperationFailed"] = ("AirPlay 原生操作失败：{0}（{1}）。", "AirPlay native operation failed: {0} ({1})."),
        ["Error.InvalidArgument"] = ("参数无效", "Invalid argument"),
        ["Error.InvalidState"] = ("当前会话状态不允许此操作", "The current session state does not allow this operation"),
        ["Error.InvalidProtocolData"] = ("协议数据无效", "Invalid protocol data"),
        ["Error.ReceiverAuthFailed"] = ("接收器认证失败", "Receiver authentication failed"),
        ["Error.NetworkFailed"] = ("网络连接失败", "Network connection failed"),
        ["Error.DiscoveryFailed"] = ("设备发现失败", "Device discovery failed"),
        ["Error.PairingRejected"] = ("接收器拒绝配对", "Receiver rejected pairing"),
        ["Error.InvalidPairingData"] = ("配对数据无效", "Invalid pairing data"),
        ["Error.RtspRejected"] = ("接收器拒绝 RTSP 请求", "Receiver rejected the RTSP request"),
        ["Error.InvalidResponse"] = ("接收器返回无效响应", "Receiver returned an invalid response"),
        ["Error.AudioCaptureFailed"] = ("Windows 音频采集失败", "Windows audio capture failed"),
        ["Error.NativeInternal"] = ("Rust 原生层发生内部异常", "Internal error in the Rust native layer"),
        ["Error.UnknownNative"] = ("未知原生错误", "Unknown native error"),
        ["Error.CreateSessionFailed"] = ("无法创建 AirPlay 原生会话。", "Failed to create the AirPlay native session."),
        ["Error.DeviceNotFound"] = ("找不到设备“{0}”。", "Device “{0}” was not found."),
        ["Error.InitializeFirst"] = ("请先调用 InitializeAsync。", "Call InitializeAsync first."),
        ["Error.CurrentDeviceOnly"] = ("只能操作当前连接的设备。", "Only the currently connected device can be operated."),
        ["Error.SessionBusy"] = ("AirPlay 会话已连接或正在连接。", "The AirPlay session is already connected or connecting."),
        ["Error.DiscoveryTimeoutRange"] = ("发现超时必须在 1 毫秒到 30 秒之间。", "Discovery timeout must be between 1 millisecond and 30 seconds."),
        ["Error.VolumeRange"] = ("音量必须在 -144 到 0 dB 之间。", "Volume must be between -144 and 0 dB."),
    };

    private AppLanguage _language = AppLanguage.Chinese;

    /// <summary>
    /// 语言切换后触发，订阅者应刷新各自持有的界面文本。
    /// </summary>
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// 当前界面语言。
    /// </summary>
    public AppLanguage Language
    {
        get => _language;
        private set
        {
            if (_language == value) return;
            _language = value;
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string this[string key] => Get(key);

    /// <summary>
    /// 按当前语言返回键对应的文本，找不到键时原样返回键名。
    /// </summary>
    public string Get(string key) =>
        Texts.TryGetValue(key, out var pair)
            ? Language == AppLanguage.English ? pair.English : pair.Chinese
            : key;

    /// <summary>
    /// 按当前语言格式化带占位符的模板文本。
    /// </summary>
    public string Format(string key, params object[] args) => string.Format(Get(key), args);

    /// <summary>
    /// 切换界面语言并通知所有订阅者。
    /// </summary>
    public void SetLanguage(AppLanguage language)
    {
        if (!Enum.IsDefined(language)) throw new ArgumentOutOfRangeException(nameof(language));
        Language = language;
    }
}
