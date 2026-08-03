using AirBlade.Models;
using AirBlade.Services;
using AirBlade.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace AirBlade;

/// <summary>
/// 提供设备管理和应用信息的完整设置窗口。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsDraftViewModel _appliedSettings = new();
    private readonly DeviceSettingsService _settings;
    private readonly Action<GlobalSettings>? _appearanceApplied;
    private readonly UISettings _uiSettings = new();
    private ThemeMode _currentTheme = ThemeMode.FollowSystem;
    private string _themeDictionary = "Dark";
    private bool _titleBarActive = true;

    public SettingsWindow(DeviceManagerViewModel viewModel, DeviceSettingsService settings, Action<GlobalSettings>? appearanceApplied = null)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _appearanceApplied = appearanceApplied;
        InitializeComponent();
        var global = _settings.GetGlobalSettings();
        _appliedSettings.AppearanceTheme = global.Theme switch
        {
            ThemeMode.Dark => 1,
            ThemeMode.Light => 2,
            _ => 0,
        };
        Draft.CopyFrom(_appliedSettings);
        ApplyTitleBarTheme(global.Theme);
        var titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        UpdateTitleBarDragRectangles(titleBar);
        AppWindow.Changed += OnAppWindowChanged;
        Activated += OnWindowActivated;
        _uiSettings.ColorValuesChanged += OnSystemThemeChanged;
        Closed += OnClosed;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.PreferredMinimumWidth = 1111;
        SettingsNav.SelectedItem = SettingsNav.MenuItems[0];
    }

    public DeviceManagerViewModel ViewModel { get; }

    /// <summary>
    /// 通用/外观页设置项的草稿值，保存前只修改这里。
    /// </summary>
    public SettingsDraftViewModel Draft { get; } = new();

    /// <summary>
    /// 滑块松手即应用音量，与快捷窗行为一致。
    /// </summary>
    private async void VolumeSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Slider { DataContext: DeviceItemViewModel device }) await ViewModel.SetVolumeAsync(device);
    }

    /// <summary>
    /// 保存草稿：外观设置持久化到全局配置，并回调应用外观。
    /// </summary>
    private async void SaveButton_Click(object sender, RoutedEventArgs args)
    {
        var previous = _settings.GetGlobalSettings();
        _appliedSettings.CopyFrom(Draft);
        var updated = previous with
        {
            Theme = _appliedSettings.AppearanceTheme switch
            {
                1 => ThemeMode.Dark,
                2 => ThemeMode.Light,
                _ => ThemeMode.FollowSystem,
            },
        };
        try
        {
            await _settings.UpdateGlobalSettingsAsync(_ => updated);
            _appearanceApplied?.Invoke(updated);
        }
        catch (Exception)
        {
            // 保存失败时保持草稿不变，避免界面状态与配置不一致。
        }
    }

    /// <summary>
    /// 按主题模式设置标题栏：RequestedTheme 只影响窗口内容，标题栏由系统绘制，
    /// 因此需要通过 DWM 属性显式切换深色模式，并从对应主题字典取颜色。
    /// </summary>
    public void ApplyTitleBarTheme(ThemeMode theme)
    {
        _currentTheme = theme;
        ApplyTitleBarThemeCore(theme);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
        => UpdateTitleBarState(args.WindowActivationState != WindowActivationState.Deactivated);

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange) UpdateTitleBarDragRectangles(AppWindow.TitleBar);
        if (args.DidPresenterChange) UpdateMaximizeGlyph();
    }

    /// <summary>
    /// 系统深浅色切换时重新按当前主题计算标题栏配色。
    /// </summary>
    private void OnSystemThemeChanged(UISettings sender, object args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (_currentTheme != ThemeMode.Dark) ApplyTitleBarThemeCore(_currentTheme);
        });
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _uiSettings.ColorValuesChanged -= OnSystemThemeChanged;
    }

    private void ApplyTitleBarThemeCore(ThemeMode theme)
    {
        var actualDark = theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => Content is FrameworkElement root && root.ActualTheme == ElementTheme.Dark,
        };
        _themeDictionary = actualDark ? "Dark" : "Light";
        UpdateTitleBarState(_titleBarActive);
    }

    /// <summary>
    /// 按激活状态应用标题栏背景与前景（聚焦/失焦）。
    /// </summary>
    private void UpdateTitleBarState(bool isActive)
    {
        _titleBarActive = isActive;
        var backgroundKey = isActive ? "SettingsTitleBarBackgroundBrush" : "SettingsTitleBarInactiveBackgroundBrush";
        var foregroundKey = isActive ? "SettingsTitleBarForegroundBrush" : "SettingsTitleBarInactiveForegroundBrush";
        TitleBarArea.Background = ThemeBrush(_themeDictionary, backgroundKey)
            ?? new SolidColorBrush(_themeDictionary == "Dark" ? Windows.UI.Color.FromArgb(255, 40, 40, 40) : Windows.UI.Color.FromArgb(255, 245, 245, 245));
        // 资源查找失败时用与主题一致的硬编码兜底，避免 Foreground 变成 null 让图标退回默认黑色。
        var foreground = ThemeBrush(_themeDictionary, foregroundKey)
            ?? new SolidColorBrush(_themeDictionary == "Dark" ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(255, 26, 26, 26));
        TitleBarTitle.Foreground = foreground;
        MinimizeButton.Foreground = foreground;
        MaximizeButton.Foreground = foreground;
        CloseButton.Foreground = foreground;
        MinimizePath.Stroke = foreground;
        MaximizePath.Stroke = foreground;
        RestorePath.Stroke = foreground;
        ClosePath.Stroke = foreground;
    }

    /// <summary>
    /// 更新标题栏可拖动区域（右侧预留三个窗口按钮）。
    /// </summary>
    private void UpdateTitleBarDragRectangles(Microsoft.UI.Windowing.AppWindowTitleBar titleBar)
    {
        var width = Math.Max(0, AppWindow.Size.Width - 140);
        titleBar.SetDragRectangles([new Windows.Graphics.RectInt32(0, 0, width, 32)]);
    }

    private void UpdateMaximizeGlyph()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        MaximizePath.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestorePath.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(MaximizeButton, maximized ? "还原" : "最大化");
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs args)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs args)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore();
        else presenter.Maximize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs args) => Close();

    /// <summary>
    /// 从指定主题字典读取画刷。
    /// </summary>
    private static SolidColorBrush? ThemeBrush(string dictionary, string key)
    {
        if (Application.Current.Resources.ThemeDictionaries.TryGetValue(dictionary, out var themeValue)
            && themeValue is ResourceDictionary theme
            && theme.TryGetValue(key, out var resource)
            && resource is SolidColorBrush brush)
        {
            return brush;
        }
        return null;
    }

    /// <summary>
    /// 根据左侧导航选中项切换右侧页面。
    /// </summary>
    private void SettingsNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // 切换标签页即丢弃未应用的修改，开关与选项回到已应用状态。
        Draft.CopyFrom(_appliedSettings);
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag?.ToString();
        DevicesPage.Visibility = tag == "Devices" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = tag == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "About" ? Visibility.Visible : Visibility.Collapsed;
    }

}
