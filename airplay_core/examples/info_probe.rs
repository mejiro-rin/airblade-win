use airplay_core::pairing_client::open_transient_control;
use std::{net::SocketAddr, str::FromStr};

fn main() {
    let address = std::env::args()
        .nth(1)
        .unwrap_or_else(|| "192.168.3.24:7000".to_owned());
    let address =
        SocketAddr::from_str(&address).expect("地址格式应为 IP:端口，例如 192.168.3.24:7000");

    // 连续请求两次，用第二次成功证明 M4 后的连接和加密计数器确实被保留下来。
    match open_transient_control(address).and_then(|mut channel| {
        let info = channel.get_info()?;
        channel.get_info()?;
        Ok(info)
    }) {
        Ok(info) => {
            println!("HomePod 常驻加密控制连接和连续两次 GET /info 验证成功。");
            println!("名称：{}", info.name.as_deref().unwrap_or("未提供"));
            println!("型号：{}", info.model.as_deref().unwrap_or("未提供"));
            println!("设备 ID：{}", info.device_id.as_deref().unwrap_or("未提供"));
            println!(
                "功能位：{}",
                info.features
                    .map(|value| format!("0x{value:X}"))
                    .unwrap_or_else(|| "未提供".to_owned())
            );
            println!(
                "状态位：{}",
                info.status_flags
                    .map(|value| format!("0x{value:X}"))
                    .unwrap_or_else(|| "未提供".to_owned())
            );
            println!(
                "latencyMin：{}",
                info.latency_min
                    .map(|value| value.to_string())
                    .unwrap_or_else(|| "未提供".to_owned())
            );
            println!("binary plist：{} 字节", info.body_bytes);
        }
        Err(error) => {
            eprintln!("加密 GET /info 失败：{error}");
            std::process::exit(1);
        }
    }
}
