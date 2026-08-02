use airplay_core::pairing_client::open_transient_control;
use std::{
    f32::consts::TAU,
    net::SocketAddr,
    str::FromStr,
    thread,
    time::{Duration, Instant},
};

fn main() {
    let mut arguments = std::env::args().skip(1);
    let address = arguments
        .next()
        .unwrap_or_else(|| "192.168.3.24:7000".to_owned());
    let receiver_volume_db = arguments
        .next()
        .and_then(|value| value.parse::<f32>().ok())
        .unwrap_or(-20.0);
    let tone_amplitude = arguments
        .next()
        .and_then(|value| value.parse::<f32>().ok())
        .unwrap_or(0.15);
    let seconds = arguments
        .next()
        .and_then(|value| value.parse::<u64>().ok())
        .unwrap_or(5);
    let address =
        SocketAddr::from_str(&address).expect("地址格式应为 IP:端口，例如 192.168.3.24:7000");
    if let Err(error) = run(address, receiver_volume_db, tone_amplitude, seconds) {
        eprintln!("播放探针失败：{error}");
        std::process::exit(1);
    }
}

fn run(
    address: SocketAddr,
    receiver_volume_db: f32,
    tone_amplitude: f32,
    seconds: u64,
) -> airplay_core::error::Result<()> {
    if !(-144.0..=0.0).contains(&receiver_volume_db)
        || !(0.0..=1.0).contains(&tone_amplitude)
        || seconds == 0
    {
        return Err(airplay_core::error::CoreError::InvalidArgument);
    }
    let mut channel = open_transient_control(address)?;
    channel.get_info()?;
    let mut session = channel.setup_session()?;
    session.open_event_channel()?;
    session.record()?;
    session.setup_stream()?;
    session.set_volume(receiver_volume_db)?;
    session.send_playback_sync(true)?;

    println!(
        "正在播放 440 Hz 测试音：接收端音量 {receiver_volume_db:.1} dB，PCM 振幅 {tone_amplitude:.2}，持续 {seconds} 秒..."
    );
    let packet_time = Duration::from_secs_f64(352.0 / 44_100.0);
    let start = Instant::now();
    let mut deadline = start;
    let mut frame_index = 0_u64;
    let mut next_sync = start + Duration::from_secs(1);
    let mut retransmitted = 0_usize;
    while start.elapsed() < Duration::from_secs(seconds) {
        let mut samples = [0_i16; 704];
        for frame in 0..352 {
            let phase = TAU * 440.0 * (frame_index + frame as u64) as f32 / 44_100.0;
            let sample = (phase.sin() * tone_amplitude * i16::MAX as f32) as i16;
            samples[frame * 2] = sample;
            samples[frame * 2 + 1] = sample;
        }
        session.send_audio_packet(&samples)?;
        retransmitted += session.poll_retransmit_requests()?;
        frame_index += 352;
        if Instant::now() >= next_sync {
            session.send_playback_sync(false)?;
            next_sync += Duration::from_secs(1);
        }
        deadline += packet_time;
        if let Some(wait) = deadline.checked_duration_since(Instant::now()) {
            thread::sleep(wait);
        }
    }
    if let Some(stats) = session.ptp_stats() {
        println!(
            "PTP 诊断：发送 Sync={}、Announce={}；接收319={}、320={}、消息类型掩码=0x{:X}、Delay_Req={}；RTP重传包={}。",
            stats.sync_sent,
            stats.announce_sent,
            stats.event_received,
            stats.general_received,
            stats.received_types,
            stats.delay_requests,
            retransmitted
        );
    }
    println!("测试音数据发送完成。关闭程序后 HomePod 会自动清理该会话。");
    Ok(())
}
