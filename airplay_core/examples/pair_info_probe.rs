//! 连接状态探针：只做发现、transient 配对和读取设备信息。
//! 不建立音频会话、不发音频包，用于夜间安静诊断连接链路。

use std::{
    net::{IpAddr, SocketAddr},
    time::Duration,
};

use airplay_core::{
    discovery::{AirPlayTarget, discover_homepods},
    pairing_client::open_transient_control,
};

fn main() {
    println!("正在发现 HomePod（5 秒）...");
    let targets = match discover_homepods(Duration::from_secs(5)) {
        Ok(list) => list,
        Err(error) => {
            eprintln!("发现失败：{error}");
            std::process::exit(1);
        }
    };
    if targets.is_empty() {
        println!("未发现任何 HomePod。");
        return;
    }
    for (index, target) in targets.iter().enumerate() {
        println!(
            "[{index}] {} id={} 地址={:?} 端口={} model={:?} status_flags={:?}",
            target.display_name,
            target.device_id,
            target.addresses,
            target.port,
            target.model,
            target.status_flags,
        );
    }

    let target = &targets[0];
    let Some(endpoint) = pick_endpoint(target) else {
        eprintln!("目标没有可用的 IPv4 地址。");
        std::process::exit(2);
    };
    println!("对 {endpoint} 发起 transient 配对（不建立音频会话）...");
    let mut channel = match open_transient_control(endpoint) {
        Ok(channel) => {
            println!("配对成功：M1-M4 完成，加密控制通道已建立。");
            channel
        }
        Err(error) => {
            eprintln!("配对失败：{error}");
            std::process::exit(3);
        }
    };
    match channel.get_info() {
        Ok(info) => {
            println!("设备信息读取成功：");
            println!("  名称：{:?}", info.name);
            println!("  型号：{:?}", info.model);
            println!("  deviceId：{:?}", info.device_id);
            println!("  features：{:?}", info.features);
            println!("  statusFlags：{:?}", info.status_flags);
            println!("  latencyMin：{:?}", info.latency_min);
            if let Some(flags) = info.status_flags {
                println!("  状态解读：{}", describe_status_flags(flags));
            }
        }
        Err(error) => {
            eprintln!("读取设备信息失败：{error}");
            std::process::exit(4);
        }
    }
    // channel 在此处 drop：断开控制连接，不执行 SETUP/RECORD，不发送任何音频。
    println!("探针结束，控制连接已断开。");
}

/// 优先取 IPv4 地址，与 AirPlay 发送引擎的定向单播行为一致。
fn pick_endpoint(target: &AirPlayTarget) -> Option<SocketAddr> {
    target
        .addresses
        .iter()
        .copied()
        .find(|address| matches!(address, IpAddr::V4(_)))
        .or_else(|| target.addresses.first().copied())
        .map(|address| SocketAddr::new(address, target.port))
}

/// 按 AirPlay 惯例解读状态标志位（bit0=播放中，bit1=已暂停，bit2=待机）。
fn describe_status_flags(flags: u64) -> String {
    let mut parts = Vec::new();
    if flags & 0x01 != 0 {
        parts.push("正在播放");
    }
    if flags & 0x02 != 0 {
        parts.push("已暂停");
    }
    if flags & 0x04 != 0 {
        parts.push("待机");
    }
    if parts.is_empty() {
        "空闲，未播放".to_owned()
    } else {
        parts.join("、")
    }
}
