use airplay_core::{
    audio::{PcmConverter, WasapiCapture, create_ring, processing::f32_to_i16, run_capture_loop},
    event_channel::RemoteEvent,
    pairing_client::open_transient_control,
};
use ringbuf::traits::Consumer;
use std::{
    collections::VecDeque,
    net::SocketAddr,
    str::FromStr,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
    thread,
    time::{Duration, Instant},
};

fn main() {
    let mut arguments = std::env::args().skip(1);
    let address = arguments
        .next()
        .unwrap_or_else(|| "192.168.3.24:7000".to_owned());
    let seconds = arguments
        .next()
        .and_then(|value| value.parse::<u64>().ok())
        .unwrap_or(10);
    let receiver_volume_db = arguments
        .next()
        .and_then(|value| value.parse::<f32>().ok())
        .unwrap_or(-20.0);
    let address =
        SocketAddr::from_str(&address).expect("地址格式应为 IP:端口，例如 192.168.3.24:7000");
    if let Err(error) = run(address, seconds, receiver_volume_db) {
        eprintln!("系统声音推流失败：{error}");
        std::process::exit(1);
    }
}

fn run(
    address: SocketAddr,
    seconds: u64,
    receiver_volume_db: f32,
) -> airplay_core::error::Result<()> {
    if seconds == 0 || !(-144.0..=0.0).contains(&receiver_volume_db) {
        return Err(airplay_core::error::CoreError::InvalidArgument);
    }
    let capture = WasapiCapture::new()?;
    let input_format = capture.format;
    let (producer, mut consumer) =
        create_ring(input_format.sample_rate as usize * input_format.channels * 2);
    let running = Arc::new(AtomicBool::new(true));
    let capture_running = running.clone();
    let capture_thread = thread::spawn(move || {
        if capture.start().is_ok() {
            run_capture_loop(&capture, producer, capture_running.clone());
            let _ = capture.stop();
        }
    });

    let result = (|| {
        let mut channel = open_transient_control(address)?;
        channel.get_info()?;
        let mut session = channel.setup_session()?;
        session.open_event_channel()?;
        session.record()?;
        session.setup_stream()?;
        session.set_volume(receiver_volume_db)?;
        session.send_playback_sync(true)?;

        println!(
            "正在推送 Windows 默认输出设备的系统声音（{} Hz，{} 声道，{} 位），接收端音量 {:.1} dB，持续 {} 秒...",
            input_format.sample_rate,
            input_format.channels,
            input_format.container_bits,
            receiver_volume_db,
            seconds
        );
        let mut converter = PcmConverter::new(input_format.sample_rate, input_format.channels)?;
        let mut converted = VecDeque::<i16>::new();
        let mut raw = Vec::new();
        let packet_time = Duration::from_secs_f64(352.0 / 44_100.0);
        let start = Instant::now();
        let mut deadline = start;
        let mut next_sync = start + Duration::from_secs(1);
        while start.elapsed() < Duration::from_secs(seconds) {
            raw.clear();
            while let Some(sample) = consumer.try_pop() {
                raw.push(sample);
            }
            if !raw.is_empty() {
                converted.extend(f32_to_i16(&converter.convert(&raw)));
            }
            let mut packet = [0_i16; 704];
            for sample in &mut packet {
                if let Some(value) = converted.pop_front() {
                    *sample = value;
                }
            }
            session.send_audio_packet(&packet)?;
            for event in session.poll_remote_events()? {
                if let RemoteEvent::Volume(value) = event {
                    println!("收到 HomePod 音量变化：{value:.3} dB");
                }
            }
            if Instant::now() >= next_sync {
                session.send_playback_sync(false)?;
                next_sync += Duration::from_secs(1);
            }
            deadline += packet_time;
            if let Some(wait) = deadline.checked_duration_since(Instant::now()) {
                thread::sleep(wait);
            }
        }
        Ok(())
    })();

    running.store(false, Ordering::Release);
    let _ = capture_thread.join();
    result
}
