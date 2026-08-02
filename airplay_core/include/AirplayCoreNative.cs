using System;
using System.Runtime.InteropServices;

namespace AirBlade.Interop;

internal enum AirplayConnectionPolicy : int
{
    Manual = 0,
    Automatic = 1,
}

internal enum AirplayEventKind : int
{
    None = 0,
    State = 1,
    Volume = 2,
    Error = 3,
}

internal enum AirplaySessionState : int
{
    Idle = 0,
    Pairing = 1,
    Connecting = 2,
    Streaming = 3,
    Disconnected = 4,
    Failed = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct AirplayEvent
{
    public AirplayEventKind Kind;
    public AirplaySessionState State;
    public float VolumeDb;
    public int ErrorCode;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct AirplayTargetInfo
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] DeviceId;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
    public byte[] DisplayName;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
    public byte[] Address;

    public ushort Port;
}

/// <summary>airplay_core.dll 的稳定原生入口；配置持久化由 C# 服务层负责。</summary>
internal static class AirplayCoreNative
{
    private const string Library = "airplay_core";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airplay_core_init();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr airplay_core_version();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong airplay_session_create();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airplay_session_set_connection_policy(
        ulong handle,
        AirplayConnectionPolicy policy);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airplay_session_connect(
        ulong handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string address);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airplay_session_disconnect(ulong handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airplay_session_set_volume(ulong handle, float volumeDb);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airplay_session_poll_event(ulong handle, out AirplayEvent airplayEvent);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airplay_session_destroy(ulong handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airplay_discover_homepods(
        uint timeoutMs,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] AirplayTargetInfo[] targets,
        nuint capacity,
        out nuint count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int airplay_event_release(ref AirplayEvent airplayEvent);
}
