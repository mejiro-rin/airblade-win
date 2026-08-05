using System.Runtime.InteropServices;

namespace AirBlade.Interop;

public enum AirplayConnectionPolicy : int { Manual = 0, Automatic = 1 }
public enum AirplayEventKind : int { None = 0, State = 1, Volume = 2, Error = 3 }
public enum AirplaySessionState : int { Idle = 0, Pairing = 1, Connecting = 2, Streaming = 3, Disconnected = 4, Failed = 5 }
public enum AirplayErrorCode : int
{
    InvalidArgument = -1, InvalidState = -2, Protocol = -3, Authentication = -4,
    Network = -5, Discovery = -6, PairingStatus = -7, PairingTlv = -8,
    RtspStatus = -9, RtspBody = -10, WindowsAudio = -11, NativePanic = -127,
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct AirplayEvent
{
    public AirplayEventKind Kind;
    public AirplaySessionState State;
    public float VolumeDb;
    public int ErrorCode;
}

[StructLayout(LayoutKind.Sequential, Pack = 2, CharSet = CharSet.Ansi)]
internal struct AirplayTargetInfo
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] DeviceId;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] DisplayName;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)] public byte[] Address;
    public ushort Port;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct AirplayAbiLayout
{
    public uint EventSize, EventAlign, EventKindOffset, EventStateOffset, EventVolumeDbOffset, EventErrorCodeOffset;
    public uint TargetSize, TargetAlign, TargetDeviceIdOffset, TargetDisplayNameOffset, TargetAddressOffset, TargetPortOffset;
}

internal static class AirplayCoreNative
{
    private const string Library = "airplay_core";
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_core_init();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern IntPtr airplay_core_version();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_core_get_abi_layout(out AirplayAbiLayout layout);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern ulong airplay_session_create();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_session_set_connection_policy(ulong handle, AirplayConnectionPolicy policy);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_session_connect(ulong handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string address);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_session_disconnect(ulong handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_session_set_volume(ulong handle, float volumeDb);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_session_poll_event(ulong handle, out AirplayEvent airplayEvent);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_session_destroy(ulong handle);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_discover_homepods(uint timeoutMs, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] AirplayTargetInfo[] targets, nuint capacity, out nuint count);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_system_get_mute();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int airplay_system_set_mute(int muted);
}
