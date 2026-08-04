using AirBlade.Interop;

namespace AirBlade.Models;

/// <summary>
/// 表示用户对单个设备的持久化偏好。
/// </summary>
public sealed record DeviceSettings
{
    /// <summary>设备名称，连接成功后记忆，供重启时恢复设备列表。</summary>
    public string? DisplayName { get; init; }

    /// <summary>设备地址，连接成功后记忆。</summary>
    public string? Address { get; init; }

    /// <summary>设备端口，连接成功后记忆。</summary>
    public ushort? Port { get; init; }

    public bool AutoConnect { get; init; } = false;
    public AirplayConnectionPolicy ConnectionPolicy { get; init; } = AirplayConnectionPolicy.Manual;
    public bool RememberVolume { get; init; } = true;
    public float VolumeDb { get; init; } = -15.12f;
    public bool Hidden { get; init; } = false;
    public DateTimeOffset? LastConnectedAt { get; init; }
}
