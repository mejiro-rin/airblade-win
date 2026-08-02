use super::device::get_default_render_device;
use super::processing::{InputPcmFormat, SampleEncoding};
use crate::error::{CoreError, Result};
use ringbuf::HeapProd;
use ringbuf::traits::Producer;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use windows::Win32::Foundation::*;
use windows::Win32::Media::Audio::*;
use windows::Win32::Media::KernelStreaming::{KSDATAFORMAT_SUBTYPE_PCM, WAVE_FORMAT_EXTENSIBLE};
use windows::Win32::System::Com::*;
use windows::Win32::System::Threading::*;
use windows::core::GUID;

pub struct WasapiCapture {
    audio_client: IAudioClient,
    capture_client: IAudioCaptureClient,
    event_handle: HANDLE,
    pub format: InputPcmFormat,
}

unsafe impl Send for WasapiCapture {}

impl WasapiCapture {
    pub fn new() -> Result<Self> {
        unsafe {
            CoInitializeEx(None, COINIT_MULTITHREADED).ok()?;

            let device = get_default_render_device()?;
            let audio_client: IAudioClient = device.Activate(CLSCTX_ALL, None)?;

            let format_ptr = audio_client.GetMixFormat()?;
            let format = parse_mix_format(format_ptr)?;

            audio_client.Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                0,
                0,
                format_ptr,
                None,
            )?;
            CoTaskMemFree(Some(format_ptr.cast()));

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
        unsafe { self.audio_client.Start()? };
        Ok(())
    }

    pub fn stop(&self) -> Result<()> {
        unsafe { self.audio_client.Stop()? };
        Ok(())
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

                if capture
                    .capture_client
                    .GetBuffer(&mut data_ptr, &mut frames_available, &mut flags, None, None)
                    .is_err()
                {
                    break;
                }

                let channels = capture.format.channels;
                let sample_count = frames_available as usize * channels;

                if flags & AUDCLNT_BUFFERFLAGS_SILENT.0 as u32 != 0 {
                    for _ in 0..sample_count {
                        let _ = producer.try_push(0.0f32);
                    }
                } else {
                    let byte_count = frames_available as usize * capture.format.block_align;
                    let bytes = std::slice::from_raw_parts(data_ptr, byte_count);
                    if let Ok(samples) = capture.format.decode(bytes) {
                        for sample in samples {
                            let _ = producer.try_push(sample);
                        }
                    }
                }

                let _ = capture.capture_client.ReleaseBuffer(frames_available);
            }
        }
    }
}

unsafe fn parse_mix_format(format_ptr: *const WAVEFORMATEX) -> Result<InputPcmFormat> {
    let base = unsafe { *format_ptr };
    let (encoding, valid_bits) = match base.wFormatTag as u32 {
        WAVE_FORMAT_PCM => (SampleEncoding::SignedPcm, base.wBitsPerSample),
        3 => (SampleEncoding::Float, base.wBitsPerSample),
        WAVE_FORMAT_EXTENSIBLE => {
            let extensible =
                unsafe { std::ptr::read_unaligned(format_ptr.cast::<WAVEFORMATEXTENSIBLE>()) };
            let sub_format = extensible.SubFormat;
            let valid_bits = unsafe { extensible.Samples.wValidBitsPerSample };
            const IEEE_FLOAT: GUID = GUID::from_u128(0x00000003_0000_0010_8000_00aa00389b71);
            let encoding = if sub_format == KSDATAFORMAT_SUBTYPE_PCM {
                SampleEncoding::SignedPcm
            } else if sub_format == IEEE_FLOAT {
                SampleEncoding::Float
            } else {
                return Err(CoreError::Protocol("WASAPI 返回了未知的扩展采样格式"));
            };
            (encoding, valid_bits)
        }
        _ => return Err(CoreError::Protocol("WASAPI 返回了不支持的采样格式")),
    };
    InputPcmFormat {
        sample_rate: base.nSamplesPerSec,
        channels: base.nChannels as usize,
        block_align: base.nBlockAlign as usize,
        container_bits: base.wBitsPerSample,
        valid_bits,
        encoding,
    }
    .validate()
}
