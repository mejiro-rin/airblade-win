using System.Runtime.InteropServices;
using AirBlade.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace AirBlade;

/// <summary>
/// 位于工作区右下角的快捷设备控制窗口。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _autoRefreshTimer;
    private DateTime _lastShownAtUtc = DateTime.MinValue;
    private bool _positioned;
    private bool _autoHideEnabled;

    public MainWindow(DeviceManagerViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _autoRefreshTimer.Tick += OnAutoRefreshTimerTick;
        Activated += OnActivated;
        Closed += OnClosed;
        // 首次显示前完成尺寸、位置和无边框配置，避免在激活回调里改布局干扰首帧合成。
        ConfigureWindowPlacement();
    }

    public DeviceManagerViewModel ViewModel { get; }
    public event EventHandler? MoreRequested;

    /// <summary>
    /// 初始化完成后每八秒发现一次设备，平衡状态及时性与界面稳定性。
    /// </summary>
    public void StartAutoRefresh() => _autoRefreshTimer.Start();

    private void MoreButton_Click(object sender, RoutedEventArgs args) => MoreRequested?.Invoke(this, EventArgs.Empty);

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

    private async void VolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Slider { DataContext: DeviceItemViewModel device }) await ViewModel.SetVolumeAsync(device);
    }

    private async void OnAutoRefreshTimerTick(object? sender, object args) => await ViewModel.DiscoverAsync();

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs args) => _autoHideEnabled = true;

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _autoRefreshTimer.Stop();
        _autoRefreshTimer.Tick -= OnAutoRefreshTimerTick;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // 窗口刚被托盘唤起时可能先收到一次失焦事件（激活竞争失败），
            // 短暂时间内不自动隐藏，避免“刚显示就消失”的竞态。
            if (_autoHideEnabled && (DateTime.UtcNow - _lastShownAtUtc).TotalMilliseconds > 1500) HideToTray();
            return;
        }
        ConfigureWindowPlacement();
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
        // 固定窗口尺寸；高度比旧值下调几个像素，让窗口整体更贴近任务栏。
        AppWindow.Resize(new Windows.Graphics.SizeInt32(540, 585));
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
