namespace AirBlade.ViewModels;

/// <summary>
/// 通用/外观页设置项的草稿状态：切换标签页或点“放弃”时从已应用值还原，
/// 点“应用”时把草稿提交为已应用值。
/// </summary>
public sealed class SettingsDraftViewModel : ObservableObject
{
    private bool _generalToggle;
    private int _generalOption;
    private bool _appearanceToggle;
    private int _appearanceOption;
    private int _appearanceTheme;
    private int _language;
    private bool _quickWindowAcrylic;

    public bool GeneralToggle { get => _generalToggle; set => SetProperty(ref _generalToggle, value); }
    public int GeneralOption { get => _generalOption; set => SetProperty(ref _generalOption, value); }
    public bool AppearanceToggle { get => _appearanceToggle; set => SetProperty(ref _appearanceToggle, value); }
    public int AppearanceOption { get => _appearanceOption; set => SetProperty(ref _appearanceOption, value); }
    public int AppearanceTheme { get => _appearanceTheme; set => SetProperty(ref _appearanceTheme, value); }
    public int Language { get => _language; set => SetProperty(ref _language, value); }
    public bool QuickWindowAcrylic { get => _quickWindowAcrylic; set => SetProperty(ref _quickWindowAcrylic, value); }

    /// <summary>
    /// 用另一份草稿覆盖当前值，用于应用与还原。
    /// </summary>
    public void CopyFrom(SettingsDraftViewModel source)
    {
        GeneralToggle = source.GeneralToggle;
        GeneralOption = source.GeneralOption;
        AppearanceToggle = source.AppearanceToggle;
        AppearanceOption = source.AppearanceOption;
        AppearanceTheme = source.AppearanceTheme;
        Language = source.Language;
        QuickWindowAcrylic = source.QuickWindowAcrylic;
    }
}
