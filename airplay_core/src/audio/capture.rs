use windows::Win32::Media::Audio::*;
use windows::Win32::System::Com::*;
use windows::Win32::System::Threading::*;
use windows::Win32::Foundation::*;
use windows::core::*;
use super::device::get_default_render_device;
use ringbuf::HeapProd;
use ringbuf::traits::Producer;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

pub struct WasapiCapture {
    audio_client: IAudioClient,
    capture_client: IAudioCaptureClient,
    event_handle: HANDLE,
    pub format: WAVEFORMATEX,
}

unsafe impl Send for WasapiCapture {}

impl WasapiCapture {
    pub fn new() -> Result<Self> {
        unsafe {
            CoInitializeEx(None, COINIT_MULTITHREADED).ok()?;

            let device = get_default_render_device()?;
            let audio_client: IAudioClient = device.Activate(CLSCTX_ALL, None)?;

            let format_ptr = audio_client.GetMixFormat()?;
            let format = *format_ptr;

            audio_client.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                0,
                0,
                format_ptr,
                None,
            )?;

            let event_handle = CreateEventW(None, false, false, None)?;

            audio_client.SetEventHandle(event_handle)?;

            let capture_client: IAudioCaptureClient = audio_client.GetService()?;

            Ok(Self {
                audio_client,
                capture_client,
                event_handle,
                format,
            })
        }
    }

    pub fn start(&self) -> Result<()> {
        unsafe { self.audio_client.Start() }
    }

    pub fn stop(&self) -> Result<()> {
        unsafe { self.audio_client.Stop() }
    }
}

/// 采集主循环,跑在独立线程里。producer 的所有权被move进来。
pub fn run_capture_loop(
    capture: &WasapiCapture,
    mut producer: HeapProd<f32>,
    running: Arc<AtomicBool>,
) {
    while running.load(Ordering::Relaxed) {
        unsafe {
            let wait_result = WaitForSingleObject(capture.event_handle, 2000);
            if wait_result != WAIT_OBJECT_0 {
                continue;
            }

            loop {
                let packet_length = capture.capture_client.GetNextPacketSize().unwrap_or(0);
                if packet_length == 0 {
                    break;
                }

                let mut data_ptr: *mut u8 = std::ptr::null_mut();
                let mut frames_available = 0u32;
                let mut flags = 0u32;

                if capture.capture_client.GetBuffer(
                    &mut data_ptr,
                    &mut frames_available,
                    &mut flags,
                    None,
                    None,
                ).is_err() {
                    break;
                }

                let channels = capture.format.nChannels as usize;
                let sample_count = frames_available as usize * channels;

                if flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0 {
                    for _ in 0..sample_count {
                        let _ = producer.try_push(0.0f32);
                    }
                } else {
                    let samples = std::slice::from_raw_parts(
                        data_ptr as *const f32,
                        sample_count,
                    );
                    for &s in samples {
                        let _ = producer.try_push(s);
                    }
                }

                let _ = capture.capture_client.ReleaseBuffer(frames_available);
            }
        }
    }
}