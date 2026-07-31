use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::thread;
use std::time::Duration;

pub fn run_verification_test() -> windows::core::Result<()> {
    let capture = WasapiCapture::new()?;
    let sample_rate = capture.format.nSamplesPerSec;
    let channels = capture.format.nChannels;

    // 0.5秒缓冲量
    let capacity = (sample_rate as usize) * (channels as usize) / 2;
    let mut ring = AudioRingBuffer::new(capacity);

    capture.start()?;
    let running = Arc::new(AtomicBool::new(true));

    thread::sleep(Duration::from_secs(10)); // 录10秒
    running.store(false, Ordering::Relaxed);

    capture.stop()?;
    Ok(())
}