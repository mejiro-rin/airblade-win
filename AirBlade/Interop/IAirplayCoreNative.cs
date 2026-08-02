namespace AirBlade.Interop;

internal interface IAirplayCoreNative
{
    void EnsureLoaded();
    ulong CreateSession();
    int SetConnectionPolicy(ulong handle, AirplayConnectionPolicy policy);
    int Connect(ulong handle, string endpoint);
    int Disconnect(ulong handle);
    int SetVolume(ulong handle, float volumeDb);
    int PollEvent(ulong handle, out AirplayEvent airplayEvent);
    int Destroy(ulong handle);
    int Discover(uint timeoutMs, AirplayTargetInfo[] targets, out nuint count);
}

internal sealed class PInvokeAirplayCoreNative : IAirplayCoreNative
{
    private const string ExpectedVersion = "0.4.0";
    private static readonly object InitializationLock = new();
    private static bool _isLoaded;

    public void EnsureLoaded()
    {
        lock (InitializationLock)
        {
            if (_isLoaded) return;
            if (!Environment.Is64BitProcess) throw new AirplayCoreException("AirPlay 原生组件仅支持 x64 进程。", null);
            try
            {
                ThrowIfFailed(AirplayCoreNative.airplay_core_init());
                var version = System.Runtime.InteropServices.Marshal.PtrToStringUTF8(AirplayCoreNative.airplay_core_version());
                if (version != ExpectedVersion) throw new AirplayCoreException($"airplay_core.dll 版本不匹配：需要 {ExpectedVersion}，实际为 {version ?? "未知"}。", null);
                ThrowIfFailed(AirplayCoreNative.airplay_core_get_abi_layout(out var layout));
                AirplayAbiVerifier.Verify(layout);
                _isLoaded = true;
            }
            catch (DllNotFoundException ex) { throw new AirplayCoreException("找不到 airplay_core.dll。请先执行 cargo build。", null, ex); }
            catch (BadImageFormatException ex) { throw new AirplayCoreException("airplay_core.dll 位数不匹配，应用和 DLL 必须均为 x64。", null, ex); }
            catch (EntryPointNotFoundException ex) { throw new AirplayCoreException("airplay_core.dll 缺少所需导出入口，请重新编译 Rust 核心。", null, ex); }
        }
    }
    public ulong CreateSession() => AirplayCoreNative.airplay_session_create();
    public int SetConnectionPolicy(ulong h, AirplayConnectionPolicy p) => AirplayCoreNative.airplay_session_set_connection_policy(h, p);
    public int Connect(ulong h, string e) => AirplayCoreNative.airplay_session_connect(h, e);
    public int Disconnect(ulong h) => AirplayCoreNative.airplay_session_disconnect(h);
    public int SetVolume(ulong h, float v) => AirplayCoreNative.airplay_session_set_volume(h, v);
    public int PollEvent(ulong h, out AirplayEvent e) => AirplayCoreNative.airplay_session_poll_event(h, out e);
    public int Destroy(ulong h) => AirplayCoreNative.airplay_session_destroy(h);
    public int Discover(uint t, AirplayTargetInfo[] a, out nuint c) => AirplayCoreNative.airplay_discover_homepods(t, a, (nuint)a.Length, out c);
    internal static void ThrowIfFailed(int result) { if (result != 0) throw AirplayCoreException.FromCode(result); }
}
