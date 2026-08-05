use airplay_core::audio::{AudioSampleBuffer, WasapiCapture, run_capture_loop, write_to_wav};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::Duration;

fn main() -> airplay_core::error::Result<()> {
    let capture = WasapiCapture::new()?;
    let sample_rate = capture.format.sample_rate;
    let channels = capture.format.channels as u16;

    // 0.5秒缓冲量
    let capacity = (sample_rate as usize) * (channels as usize) / 2;
    let buffer = AudioSampleBuffer::new(capacity);
    let producer = buffer.clone();
    let consumer = buffer;

    capture.start()?;
    let running = Arc::new(AtomicBool::new(true));

    let running1 = running.clone();
    let capture_thread = thread::spawn(move || {
        run_capture_loop(&capture, producer, running1);
    });

    let running2 = running.clone();
    let wav_thread = thread::spawn(move || {
        write_to_wav(consumer, sample_rate, channels, running2, "test_output.wav");
    });

    println!("录音10秒,请播放一段音乐...");
    thread::sleep(Duration::from_secs(10));
    running.store(false, Ordering::Relaxed);

    capture_thread.join().unwrap();
    wav_thread.join().unwrap();

    println!("完成,输出文件: test_output.wav");
    Ok(())
}
