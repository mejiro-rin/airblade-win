using AirBlade.Interop;

namespace AirBlade.Models;

/// <summary>
/// 表示单个 AirPlay 设备的运行时状态，不负责配置文件读写。
/// </summary>
public sealed class AirplayDevice
{
    public AirplayDevice(string deviceId, string displayName, string address, ushort port)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        Address = address;
        Port = port;
    }

    public string DeviceId { get; }
    public string DisplayName { get; private set; }
    public string Address { get; private set; }
    public ushort Port { get; private set; }
    public AirplaySessionState ConnectionState { get; internal set; } = AirplaySessionState.Idle;
    public float? VolumeDb { get; internal set; }
    public AirplayCoreException? LastError { get; internal set; }
    public bool IsDiscovered { get; internal set; }

    internal void UpdateDiscovery(string displayName, string address, ushort port)
    {
        DisplayName = displayName;
        Address = address;
        Port = port;
        IsDiscovered = true;
    }

    internal void MarkUndiscovered() => IsDiscovered = false;
}

/// <summary>
/// 面向界面的设备只读状态快照。
/// </summary>
public sealed record AirplayDeviceSnapshot(
    string DeviceId,
    string DisplayName,
    string Address,
    ushort Port,
    AirplaySessionState ConnectionState,
    float? VolumeDb,
    AirplayCoreException? LastError,
    bool IsDiscovered,
    bool IsHidden,
    bool AutoConnect);
