use super::AudioSampleBuffer;
use hound::{SampleFormat, WavSpec, WavWriter};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

pub fn write_to_wav(
    consumer: AudioSampleBuffer,
    sample_rate: u32,
    channels: u16,
    running: Arc<AtomicBool>,
    output_path: &str,
) {
    let spec = WavSpec {
        channels,
        sample_rate,
        bits_per_sample: 32,
        sample_format: SampleFormat::Float,
    };

    let mut writer = WavWriter::create(output_path, spec).expect("创建WAV文件失败");

    while running.load(Ordering::Relaxed) {
        while let Some(sample) = consumer.try_pop() {
            let _ = writer.write_sample(sample);
        }
        std::thread::sleep(std::time::Duration::from_millis(10));
    }

    // 循环结束后再排空一次剩余数据,避免最后一批丢失
    while let Some(sample) = consumer.try_pop() {
        let _ = writer.write_sample(sample);
    }

    let _ = writer.finalize();
}
