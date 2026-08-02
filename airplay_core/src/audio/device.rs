use windows::Win32::Media::Audio::*;
use windows::Win32::System::Com::*;
use windows::core::*;

pub fn get_default_render_device() -> Result<IMMDevice> {
    unsafe {
        let enumerator: IMMDeviceEnumerator =
            CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)?;
        enumerator.GetDefaultAudioEndpoint(eRender, eConsole)
    }
}
