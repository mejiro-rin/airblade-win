//! 系统默认输出设备的静音控制（过渡功能）。
//! 连接 AirPlay 设备后静音电脑，避免电脑与 HomePod 同时出声；断开或退出时恢复。

use super::device::get_default_render_device;
use crate::error::Result;
use windows::{
    Win32::Foundation::BOOL, Win32::Media::Audio::Endpoints::IAudioEndpointVolume,
    Win32::System::Com::*, core::HRESULT,
};

/// 当前线程已有公寓（STA 或 MTA）时 CoInitializeEx 返回的错误码，可直接复用现有公寓。
const RPC_E_CHANGED_MODE: HRESULT = HRESULT(0x8001_0106u32 as i32);

/// 确保 COM 可用：首次初始化 MTA，线程已有公寓则直接继续。
fn ensure_com_initialized() -> Result<()> {
    unsafe {
        let result = CoInitializeEx(None, COINIT_MULTITHREADED);
        if result.is_ok() || result == RPC_E_CHANGED_MODE {
            Ok(())
        } else {
            Err(windows::core::Error::from_hresult(result).into())
        }
    }
}

/// 读取当前默认输出设备是否静音。
pub fn get_default_render_mute() -> Result<bool> {
    unsafe {
        ensure_com_initialized()?;
        let device = get_default_render_device()?;
        let volume: IAudioEndpointVolume = device.Activate(CLSCTX_ALL, None)?;
        Ok(volume.GetMute()?.as_bool())
    }
}

/// 设置当前默认输出设备是否静音。
pub fn set_default_render_mute(muted: bool) -> Result<()> {
    unsafe {
        ensure_com_initialized()?;
        let device = get_default_render_device()?;
        let volume: IAudioEndpointVolume = device.Activate(CLSCTX_ALL, None)?;
        volume.SetMute(BOOL(muted as i32), std::ptr::null())?;
        Ok(())
    }
}
