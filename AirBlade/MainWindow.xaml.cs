using System.Runtime.InteropServices;
using AirBlade.Services;
using AirBlade.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace AirBlade;

/// <summary>
/// 位于工作区右下角的快捷设备控制窗口。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _autoRefreshTimer;
    private readonly DispatcherTimer _autoHideTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private DateTime _lastShownAtUtc = DateTime.MinValue;
    private bool _positioned;
    private bool _isActive;
    private readonly DispatcherTimer _volumeThrottleTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private DeviceItemViewModel? _throttledVolumeDevice;
    private DeviceItemViewModel? _lastThrottledDevice;
    private double _lastThrottledValue;
    private bool _acrylicEnabled;
    private readonly UISettings _uiSettings = new();

    public MainWindow(DeviceManagerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _autoRefreshTimer.Tick += OnAutoRefreshTimerTick;
        _volumeThrottleTimer.Tick += OnVolumeThrottleTick;
        _autoHideTimer.Tick += OnAutoHideTimerTick;
        _autoHideTimer.Start();
        _uiSettings.ColorValuesChanged += OnSystemThemeChanged;
        Activated += OnActivated;
        Closed += OnClosed;
        // 首次显示前完成尺寸、位置和无边框配置，避免在激活回调里改布局干扰首帧合成。
        ConfigureWindowPlacement();
        ApplyLocalization();
    }

    public DeviceManagerViewModel ViewModel { get; }
    public event EventHandler? MoreRequested;

    /// <summary>
    /// 切换控制窗口的亚克力效果：开启时根背景变透明以露出亚克力材质，关闭时按当前主题恢复纯色背景。
    /// 仅在设置值真正变化时才重建 SystemBackdrop；打开设置页会重复调用此方法，
    /// 若每次都新建 DesktopAcrylicBackdrop，可见窗口的亚克力控制器会被重建并闪黑。
    /// </summary>
    public void ApplyAcrylic(bool enabled)
    {
        if (_acrylicEnabled == enabled)
        {
            // 值未变化时只刷新背景画刷，不重建 SystemBackdrop，避免可见窗口闪黑。
            RefreshWindowBackground();
            return;
        }
        _acrylicEnabled = enabled;
        SystemBackdrop = enabled ? new DesktopAcrylicBackdrop() : null;
        RefreshWindowBackground();
    }

    /// <summary>
    /// 按当前实际主题刷新窗口背景：亚克力开启时透明，关闭时从主题字典取 WindowBackgroundBrush。
    /// 不能缓存旧画刷实例，否则主题切换后纯色背景会停留在旧主题颜色。
    /// </summary>
    public void RefreshWindowBackground()
    {
        if (_acrylicEnabled)
        {
            RootLayout.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            return;
        }
        var theme = RootLayout.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(theme, out var value)
            && value is ResourceDictionary dictionary
            && dictionary.TryGetValue("WindowBackgroundBrush", out var resource)
            && resource is Brush background)
        {
            RootLayout.Background = background;
        }
    }

    /// <summary>
    /// 跟随系统主题模式下，系统深浅色切换时重新解析纯色背景。
    /// </summary>
    private void OnSystemThemeChanged(UISettings sender, object args)
    {
        _ = DispatcherQueue.TryEnqueue(() => RefreshWindowBackground());
    }

    /// <summary>
    /// 按当前语言刷新快捷窗内的静态文本。
    /// </summary>
    public void ApplyLocalization()
    {
        ToolTipService.SetToolTip(MoreButton, LocalizationService.Current["MainWindow.MoreToolTip"]);
        ToolTipService.SetToolTip(MinimizeButton, LocalizationService.Current["MainWindow.MinimizeToolTip"]);
    }

    /// <summary>
    /// 初始化完成后每八秒发现一次设备，平衡状态及时性与界面稳定性。
    /// </summary>
    public void StartAutoRefresh() => _autoRefreshTimer.Start();

    private void MoreButton_Click(object sender, RoutedEventArgs args) => MoreRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 手动最小化到托盘，不依赖失焦自动隐藏判定。
    /// </summary>
    private void MinimizeButton_Click(object sender, RoutedEventArgs args) => HideToTray();

    /// <summary>
    /// 隐藏快捷窗，但保留系统托盘入口。
    /// </summary>
    public void HideToTray() => AppWindow.Hide();

    /// <summary>
    /// 从系统托盘重新显示并激活快捷窗。
    /// </summary>
    public void ShowFromTray()
    {
        _lastShownAtUtc = DateTime.UtcNow;
        AppWindow.Show();
        Activate();
        // Activate 在异步投递场景可能因前台权限失效，用 Win32 API 强制前置并激活。
        var windowHandle = WindowNative.GetWindowHandle(this);
        _ = NativeMethods.ShowWindow(windowHandle, SwShow);
        _ = NativeMethods.SetForegroundWindow(windowHandle);
    }

    private const int SwShow = 5;

    /// <summary>
    /// 拖动中值变化时启动节流定时器，未松手也会按固定间隔把当前音量发送出去。
    /// </summary>
    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (sender is Slider { DataContext: DeviceItemViewModel device })
        {
            _throttledVolumeDevice = device;
            if (!_volumeThrottleTimer.IsEnabled) _volumeThrottleTimer.Start();
        }
    }

    /// <summary>
    /// 拖动开始时挂起快照回写，避免发送过程中的状态刷新把滑块拉回旧值。
    /// </summary>
    private void VolumeSlider_PointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Slider { DataContext: DeviceItemViewModel device }) device.IsAdjustingVolume = true;
    }

    private async void VolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Slider { DataContext: DeviceItemViewModel device })
        {
            device.IsAdjustingVolume = false;
            await ViewModel.SetVolumeAsync(device);
        }
    }

    /// <summary>
    /// 节流发送：拖动中每 150 毫秒发送一次当前值；值不再变化时停止定时器。
    /// </summary>
    private void OnVolumeThrottleTick(object? sender, object args)
    {
        var device = _throttledVolumeDevice;
        if (device is null || (_lastThrottledDevice == device && _lastThrottledValue == device.EditableVolume))
        {
            _volumeThrottleTimer.Stop();
            return;
        }
        _lastThrottledDevice = device;
        _lastThrottledValue = device.EditableVolume;
        _ = ViewModel.SetVolumeAsync(device);
    }

    /// <summary>
    /// 设备卡片连接按钮：DataTemplate 里无法用 ElementName 绑定窗口命令，改为事件处理器直接调用。
    /// </summary>
    private async void ToggleConnectionButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: DeviceItemViewModel device })
            await ViewModel.ToggleConnectionCommand.ExecuteAsync(device);
    }

    private async void OnAutoRefreshTimerTick(object? sender, object args) => await ViewModel.DiscoverAsync();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _autoRefreshTimer.Stop();
        _autoRefreshTimer.Tick -= OnAutoRefreshTimerTick;
        _volumeThrottleTimer.Stop();
        _volumeThrottleTimer.Tick -= OnVolumeThrottleTick;
        _autoHideTimer.Stop();
        _autoHideTimer.Tick -= OnAutoHideTimerTick;
        _uiSettings.ColorValuesChanged -= OnSystemThemeChanged;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        _isActive = args.WindowActivationState != WindowActivationState.Deactivated;
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // 窗口刚被托盘唤起时可能先收到一次失焦事件（激活竞争失败），
            // 短暂时间内不自动隐藏，避免“刚显示就消失”的竞态。
            if ((DateTime.UtcNow - _lastShownAtUtc).TotalMilliseconds > 1500) HideToTray();
            return;
        }
        ConfigureWindowPlacement();
    }

    /// <summary>
    /// 失焦自动隐藏的兜底：个别全屏应用切换时 Deactivated 事件可能不触发，
    /// 定期检查窗口可见但未激活的情况，超过防抖时间后隐藏到托盘。
    /// </summary>
    private void OnAutoHideTimerTick(object? sender, object args)
    {
        if (!_isActive && AppWindow.IsVisible && (DateTime.UtcNow - _lastShownAtUtc).TotalMilliseconds > 1500)
            HideToTray();
    }

    /// <summary>
    /// 配置快捷窗的样式、尺寸和右下角位置，保证首次显示前布局就绪。
    /// </summary>
    private void ConfigureWindowPlacement()
    {
        if (_positioned) return;
        _positioned = true;
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(true, false);
        }
        // 固定窗口尺寸；高度按需求缩减为原来的三分之一，窗口整体更紧凑。
        AppWindow.Resize(new Windows.Graphics.SizeInt32(540, 400));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        // 底部保留 8 像素空隙，靠近任务栏但不直接接触。
        AppWindow.Move(new Windows.Graphics.PointInt32(area.X + area.Width - size.Width - 16, area.Y + area.Height - size.Height));
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr windowHandle, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr windowHandle);
    }
}
