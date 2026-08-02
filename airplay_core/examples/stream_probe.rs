use airplay_core::pairing_client::open_transient_control;
use std::{net::SocketAddr, str::FromStr};

fn main() {
    let address = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "192.168.3.24:7000".to_owned());
    let address =
        SocketAddr::from_str(&address).expect("地址格式应为 IP:端口，例如 192.168.3.24:7000");

    let result = open_transient_control(address).and_then(|mut channel| {
        channel.get_info()?;
        let mut session = channel.setup_session()?;
        session.open_event_channel()?;
        session.record()?;
        session.setup_stream()
    });
    match result {
        Ok(info) => {
            println!("HomePod stream SETUP 验证成功。");
            println!("RTP dataPort：{}", info.data_port);
            println!("接收端 controlPort：{}", info.control_port);
            println!("本地 controlPort：{}", info.local_control_port);
            println!(
                "协商延迟：{}～{} 帧（约 {}～{} ms）",
                info.latency_min_samples,
                info.latency_max_samples,
                info.latency_min_samples * 1000 / 44_100,
                info.latency_max_samples * 1000 / 44_100
            );
            println!(
                "RECORD Audio-Latency：{}",
                info.receiver_audio_latency_samples
                    .map(|value| value.to_string())
                    .unwrap_or_else(|| "未提供".into())
            );
        }
        Err(error) => {
            eprintln!("stream SETUP 失败：{error}");
            std::process::exit(1);
        }
    }
}
