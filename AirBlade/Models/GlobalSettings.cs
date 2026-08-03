using AirBlade.Interop;

namespace AirBlade.Models;

/// <summary>
/// 应用界面语言。
/// </summary>
public enum AppLanguage
{
    /// <summary>中文（默认）。</summary>
    Chinese = 0,

    /// <summary>英语。</summary>
    English = 1,
}

/// <summary>
/// 应用窗口配色模式。
/// </summary>
public enum ThemeMode
{
    FollowSystem = 0,
    Dark = 1,
    Light = 2,
}

/// <summary>
/// 表示应用级别的持久化偏好。
/// </summary>
public sealed record GlobalSettings
{
    public bool StartupEnabled { get; init; } = false;
    public bool LoggingEnabled { get; init; } = false;
    public AirplayConnectionPolicy DefaultConnectionPolicy { get; init; } = AirplayConnectionPolicy.Manual;
    public float DefaultVolumeDb { get; init; } = -72;
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public ThemeMode Theme { get; init; } = ThemeMode.FollowSystem;
    public AppLanguage Language { get; init; } = AppLanguage.Chinese;
}
