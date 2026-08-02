using AirBlade.Interop;

namespace AirBlade.Models;

/// <summary>
/// 表示应用级别的持久化偏好。
/// </summary>
public sealed record GlobalSettings
{
    public bool StartupEnabled { get; init; } = false;
    public bool LoggingEnabled { get; init; } = false;
    public AirplayConnectionPolicy DefaultConnectionPolicy { get; init; } = AirplayConnectionPolicy.Manual;
    public float DefaultVolumeDb { get; init; } = -18;
    public TimeSpan DiscoveryTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
