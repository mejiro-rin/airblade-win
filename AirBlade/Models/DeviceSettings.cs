using AirBlade.Interop;

namespace AirBlade.Models;

/// <summary>
/// 表示用户对单个设备的持久化偏好。
/// </summary>
public sealed record DeviceSettings
{
    public bool AutoConnect { get; init; } = false;
    public AirplayConnectionPolicy ConnectionPolicy { get; init; } = AirplayConnectionPolicy.Manual;
    public bool RememberVolume { get; init; } = true;
    public float VolumeDb { get; init; } = -18;
    public bool Hidden { get; init; } = false;
    public DateTimeOffset? LastConnectedAt { get; init; }
}
