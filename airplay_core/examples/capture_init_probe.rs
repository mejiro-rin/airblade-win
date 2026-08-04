//! 采集初始化探针：只初始化 WASAPI 环回捕获，不启动采集、不发声。
//! 连接引擎启动的第一步就是创建这个捕获器，这一步失败会直接导致连接失败。

use airplay_core::audio::WasapiCapture;

fn main() {
    println!("尝试初始化 WASAPI 环回捕获（默认输出设备）...");
    match WasapiCapture::new() {
        Ok(capture) => {
            println!("初始化成功：");
            println!("  采样率：{} Hz", capture.format.sample_rate);
            println!("  声道数：{}", capture.format.channels);
            println!("  block_align：{}", capture.format.block_align);
            println!("  container_bits：{}", capture.format.container_bits);
            println!("  valid_bits：{}", capture.format.valid_bits);
            println!("  编码：{:?}", capture.format.encoding);
        }
        Err(error) => {
            println!("初始化失败：{error}");
            std::process::exit(1);
        }
    }
}
