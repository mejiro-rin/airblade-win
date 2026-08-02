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
        let info = session.setup_info()?;
        Ok(info)
    });
    match result {
        Ok(info) => println!(
            "event channel 和 RECORD 验证成功：eventPort={}，时间同步={}。",
            info.event_port, info.timing_protocol
        ),
        Err(error) => {
            eprintln!("event channel 或 RECORD 失败：{error}");
            std::process::exit(1);
        }
    }
}
