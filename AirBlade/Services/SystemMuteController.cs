using AirBlade.Interop;

namespace AirBlade.Services;

/// <summary>
/// 系统默认输出设备的静音控制，供“连接设备后静音电脑”过渡功能使用。
/// </summary>
public interface ISystemMuteController
{
    /// <summary>读取当前默认输出设备是否静音；读取失败返回 null。</summary>
    bool? GetMuted();

    /// <summary>设置当前默认输出设备是否静音；失败静默忽略。</summary>
    void SetMuted(bool muted);
}

/// <summary>
/// 通过 airplay_core 的 FFI 控制系统静音，失败时静默，不打断播放流程。
/// </summary>
public sealed class SystemMuteController : ISystemMuteController
{
    public bool? GetMuted()
    {
        try
        {
            return AirplayCoreNative.airplay_system_get_mute() switch
            {
                0 => false,
                1 => true,
                _ => null,
            };
        }
        catch (Exception)
        {
            // 原生组件不可用或读取失败时视为未知，不触发静音接管。
            return null;
        }
    }

    public void SetMuted(bool muted)
    {
        try
        {
            AirplayCoreNative.airplay_system_set_mute(muted ? 1 : 0);
        }
        catch (Exception)
        {
            // 静音失败静默忽略，不打断播放。
        }
    }
}
