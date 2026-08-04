using AirBlade.Models;
using AirBlade.Services;
using AirBlade.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AirBlade;

/// <summary>
/// 管理应用窗口与设备服务的共同生命周期。
/// </summary>
public partial class App : Application
{
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private DeviceSettingsService? _settings;
    private AirplayDeviceManager? _manager;
    private DeviceManagerViewModel? _quickViewModel;
    private DeviceManagerViewModel? _settingsViewModel;
    private TrayIconHost? _trayIcon;
    private int _isShuttingDown;

    public App() => InitializeComponent();

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _settings = new DeviceSettingsService();
        try
        {
            await _settings.InitializeAsync();
        }
        catch (Exception)
        {
            // 配置读取失败时使用默认配置继续启动。
        }
        _manager = new AirplayDeviceManager(_settings);
        _quickViewModel = new DeviceManagerViewModel(_manager);
        _mainWindow = new MainWindow(_quickViewModel);
        var global = _settings.GetGlobalSettings();
        LocalizationService.Current.SetLanguage(global.Language);
        ApplyGlobalAppearance(global);
        SetApplicationIcon(_mainWindow);
        _mainWindow.MoreRequested += OnMoreRequested;
        _mainWindow.Closed += OnMainWindowClosed;
        _trayIcon = new TrayIconHost(_mainWindow, ShowQuickWindow, ShowSettingsFromTray, ExitApplication);
        _mainWindow.Activate();
        _ = InitializeAndDiscoverAsync(_manager, _quickViewModel, _mainWindow);
    }

    private async Task InitializeAndDiscoverAsync(AirplayDeviceManager manager, DeviceManagerViewModel viewModel, MainWindow window)
    {
        try
        {
            await manager.InitializeAsync();
            await viewModel.DiscoverAsync();
            window.StartAutoRefresh();
            // 首次发现完成后，自动连接配置了自动连接的设备。
            await manager.AutoConnectAsync();
        }
        catch (Exception exception)
        {
            viewModel.ShowError(exception);
        }
    }

    private void OnMoreRequested(object? sender, EventArgs args) => OpenSettings();

    private void OpenSettings()
    {
        if (_manager is null || _settings is null) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsViewModel = new DeviceManagerViewModel(_manager, includeHidden: true);
        _settingsWindow = new SettingsWindow(_settingsViewModel, _settings, OnAppearanceApplied);
        ApplyGlobalAppearance(_settings.GetGlobalSettings());
        SetApplicationIcon(_settingsWindow);
        _settingsWindow.Closed += OnSettingsWindowClosed;
        _settingsWindow.Activate();
    }

    // 托盘回调本身就运行在 UI 线程，直接同步调用可以保留前台权限，
    // 让 ShowFromTray 里的激活在 Windows 允许前台切换的窗口内完成。
    private void ShowQuickWindow() => _mainWindow?.ShowFromTray();

    /// <summary>
    /// 为所有窗口指定应用图标，保证任务栏、标题栏和 Alt+Tab 显示一致。
    /// </summary>
    private static void SetApplicationIcon(Window window) =>
        window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

    private void ShowSettingsFromTray() => OpenSettings();

    private void ExitApplication() => _mainWindow?.Close();

    private void OnSettingsWindowClosed(object sender, WindowEventArgs args)
    {
        if (_settingsWindow is not null) _settingsWindow.Closed -= OnSettingsWindowClosed;
        _settingsWindow = null;
        _settingsViewModel?.Dispose();
        _settingsViewModel = null;
    }

    /// <summary>
    /// 把配色模式应用到已打开窗口的根元素（ElementTheme 支持恢复跟随系统）。
    /// </summary>
    private void ApplyGlobalAppearance(GlobalSettings settings)
    {
        if (_mainWindow is not null)
        {
            ApplyWindowTheme(_mainWindow, settings.Theme);
            _mainWindow.ApplyAcrylic(settings.QuickWindowAcrylic);
        }
        if (_settingsWindow is not null) ApplyWindowTheme(_settingsWindow, settings.Theme);
    }

    private static void ApplyWindowTheme(Microsoft.UI.Xaml.Window window, ThemeMode theme)
    {
        if (window.Content is not FrameworkElement root) return;
        root.RequestedTheme = theme switch
        {
            ThemeMode.Dark => ElementTheme.Dark,
            ThemeMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };
    }

    /// <summary>
    /// 设置保存后实时应用：窗口主题、设置窗口标题栏与界面语言。
    /// </summary>
    private void OnAppearanceApplied(GlobalSettings settings)
    {
        ApplyGlobalAppearance(settings);
        _settingsWindow?.ApplyTitleBarTheme(settings.Theme);
        ApplyGlobalLanguage(settings);
    }

    /// <summary>
    /// 把语言设置应用到本地化服务、所有窗口与托盘菜单。
    /// </summary>
    private void ApplyGlobalLanguage(GlobalSettings settings)
    {
        LocalizationService.Current.SetLanguage(settings.Language);
        _mainWindow?.ApplyLocalization();
        _settingsWindow?.ApplyLocalization();
        _trayIcon?.UpdateMenuStrings();
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (Interlocked.Exchange(ref _isShuttingDown, 1) != 0) return;
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_settingsWindow is not null) _settingsWindow.Close();
        _quickViewModel?.Dispose();
        _quickViewModel = null;
        if (_manager is not null) await _manager.DisposeAsync();
        if (_settings is not null) await _settings.DisposeAsync();
        await AirplaySessionRegistry.DisposeAllAsync();
    }
}
