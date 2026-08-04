using AirBlade.Models;
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
/// 提供设备管理和应用信息的完整设置窗口。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly SettingsDraftViewModel _appliedSettings = new();
    private readonly DeviceSettingsService _settings;
    private readonly Action<GlobalSettings>? _appearanceApplied;
    private readonly UISettings _uiSettings = new();
    private readonly DispatcherTimer _volumeThrottleTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private DeviceItemViewModel? _throttledVolumeDevice;
    private DeviceItemViewModel? _lastThrottledDevice;
    private double _lastThrottledValue;
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
        _appliedSettings.Language = (int)global.Language;
        Draft.CopyFrom(_appliedSettings);
        ApplyLocalization();
        ApplyTitleBarTheme(global.Theme);
        var titleBar = AppWindow.TitleBar;
        titleBar.ExtendsContentIntoTitleBar = true;
        UpdateTitleBarDragRectangles(titleBar);
        AppWindow.Changed += OnAppWindowChanged;
        Activated += OnWindowActivated;
        _uiSettings.ColorValuesChanged += OnSystemThemeChanged;
        _volumeThrottleTimer.Tick += OnVolumeThrottleTick;
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

    /// <summary>
    /// 松手即应用最终音量，与快捷窗行为一致。
    /// </summary>
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

    /// <summary>
    /// 设备卡片隐藏按钮：与连接按钮同理，改用事件处理器。
    /// </summary>
    private async void ToggleHiddenButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: DeviceItemViewModel device })
            await ViewModel.ToggleHiddenCommand.ExecuteAsync(device);
    }

    /// <summary>
    /// 设备卡片的自动连接开关：状态变更后立即持久化，与隐藏按钮行为一致。
    /// 程序刷新快照导致开关回写时会自动跳过，避免重复保存。
    /// </summary>
    private async void AutoConnectToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleSwitch { DataContext: DeviceItemViewModel device } toggle) return;
        if (device.AutoConnect == toggle.IsOn) return;
        await ViewModel.SetAutoConnectAsync(device, toggle.IsOn);
    }

    /// <summary>
    /// 忘记设备：断开连接并删除该设备的全部记忆配置。
    /// </summary>
    private async void ForgetDeviceButton_Click(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: DeviceItemViewModel device }) await ViewModel.RemoveDeviceAsync(device);
    }

    /// <summary>
    /// 保存草稿：通用与外观设置持久化到全局配置，并回调应用外观与语言。
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
            Language = (AppLanguage)_appliedSettings.Language,
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
    /// 按当前语言刷新设置窗口内的静态文本。
    /// </summary>
    public void ApplyLocalization()
    {
        var t = LocalizationService.Current;
        Title = t["Settings.Title"];
        TitleBarTitle.Text = t["Settings.Title"];
        ToolTipService.SetToolTip(MinimizeButton, t["Settings.Minimize"]);
        ToolTipService.SetToolTip(MaximizeButton, t["Settings.Maximize"]);
        ToolTipService.SetToolTip(CloseButton, t["Settings.Close"]);
        NavDevices.Content = t["Settings.Nav.Devices"];
        NavGeneral.Content = t["Settings.Nav.General"];
        NavAppearance.Content = t["Settings.Nav.Appearance"];
        NavAbout.Content = t["Settings.Nav.About"];
        DevicesPageTitle.Text = t["Settings.Devices.Title"];
        ToolTipService.SetToolTip(DiscoverButton, t["Settings.Devices.DiscoverToolTip"]);
        GeneralPageTitle.Text = t["Settings.General.Title"];
        LanguageCard.Header = t["Settings.General.Language"];
        LanguageCard.Description = t["Settings.General.LanguageDescription"];
        TogglePlaceholderCard.Header = t["Settings.General.TogglePlaceholder"];
        TogglePlaceholderCard.Description = t["Settings.General.TogglePlaceholderDescription"];
        TogglePlaceholderSwitch.OnContent = t["Settings.General.ToggleOn"];
        TogglePlaceholderSwitch.OffContent = t["Settings.General.ToggleOff"];
        OptionPlaceholderCard.Header = t["Settings.General.OptionPlaceholder"];
        OptionPlaceholderCard.Description = t["Settings.General.OptionPlaceholderDescription"];
        OptionPlaceholder1.Content = t["Settings.General.Option1"];
        OptionPlaceholder2.Content = t["Settings.General.Option2"];
        OptionPlaceholder3.Content = t["Settings.General.Option3"];
        GeneralSaveButton.Content = t["Settings.Save"];
        AppearanceSaveButton.Content = t["Settings.Save"];
        AppearancePageTitle.Text = t["Settings.Appearance.Title"];
        ThemeCard.Header = t["Settings.Appearance.Theme"];
        ThemeCard.Description = t["Settings.Appearance.ThemeDescription"];
        ThemeFollowSystemOption.Content = t["Settings.Appearance.ThemeFollowSystem"];
        ThemeDarkOption.Content = t["Settings.Appearance.ThemeDark"];
        ThemeLightOption.Content = t["Settings.Appearance.ThemeLight"];
        AboutPageTitle.Text = t["Settings.About.Title"];
        CopyrightCard.Header = t["Settings.About.Copyright"];
        CopyrightCard.Description = t["Settings.About.CopyrightDescription"];
        RepositoryCard.Header = t["Settings.About.Repository"];
        UpdateMaximizeGlyph();
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
        _volumeThrottleTimer.Stop();
        _volumeThrottleTimer.Tick -= OnVolumeThrottleTick;
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
        var t = LocalizationService.Current;
        ToolTipService.SetToolTip(MaximizeButton, maximized ? t["Settings.Restore"] : t["Settings.Maximize"]);
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
