use airplay_core::pairing_client::open_transient_control;
use std::{net::SocketAddr, str::FromStr};

fn main() {
    let address = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "192.168.3.24:7000".to_owned());
    let address =
        SocketAddr::from_str(&address).expect("地址格式应为 IP:端口，例如 192.168.3.24:7000");

    let result = open_transient_control(address).and_then(|mut channel| {
        let info = channel.get_info()?;
        let session = channel.setup_session()?;
        Ok((info, session.setup_info()?))
    });
    match result {
        Ok((device, session)) => {
            println!("HomePod 会话 SETUP 验证成功。");
            println!("设备：{}", device.name.as_deref().unwrap_or("未提供"));
            println!("会话 ID：{}", session.session_id);
            println!("会话 URI：{}", session.session_uri);
            println!("本地 timingPort：{}", session.timing_port);
            println!("时间同步协议：{}", session.timing_protocol);
            println!("HomePod eventPort：{}", session.event_port);
            println!(
                "latencyMin：{}",
                session
                    .latency_min
                    .map(|value| value.to_string())
                    .unwrap_or_else(|| "未提供".to_owned())
            );
            println!(
                "latencyMax：{}",
                session
                    .latency_max
                    .map(|value| value.to_string())
                    .unwrap_or_else(|| "未提供".to_owned())
            );
        }
        Err(error) => {
            eprintln!("会话 SETUP 失败：{error}");
            std::process::exit(1);
        }
    }
}
