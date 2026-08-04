//! 连接链路干跑探针：完成发现、配对、SETUP、RECORD、SETUP_STREAM 后立即 TEARDOWN。
//! 不发送音频包、不调用播放同步，因此不会发声，也不会接管正在播放的设备。

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
    let Some(target) = targets.first() else {
        println!("未发现任何 HomePod。");
        return;
    };
    println!("目标：{}（{}）", target.display_name, target.device_id);
    let Some(endpoint) = pick_endpoint(target) else {
        eprintln!("目标没有可用 IPv4 地址。");
        std::process::exit(2);
    };

    println!("[1/5] transient 配对...");
    let mut channel = open_transient_control(endpoint).expect("配对失败");
    println!("      配对成功，控制通道已建立。");

    println!("[2/5] 读取 /info...");
    let info = channel.get_info().expect("读取设备信息失败");
    println!("      名称={:?} 型号={:?} statusFlags={:?}", info.name, info.model, info.status_flags);

    println!("[3/5] SETUP 会话...");
    let mut session = channel.setup_session().expect("SETUP 失败");
    println!("      SETUP 成功。");

    println!("[4/5] 打开事件通道并 RECORD...");
    session.open_event_channel().expect("打开事件通道失败");
    session.record().expect("RECORD 失败");

    println!("[5/5] SETUP_STREAM...");
    let stream = session.setup_stream().expect("SETUP_STREAM 失败");
    println!(
        "      data_port={} control_port={} 本机控制端口={} 延迟={} 采样",
        stream.data_port, stream.control_port, stream.local_control_port, stream.latency_min_samples
    );

    // 到此为止：不发送任何音频包，立即拆除会话，恢复设备原状。
    println!("干跑完成，立即 TEARDOWN...");
    session.teardown().expect("TEARDOWN 失败");
    println!("TEARDOWN 完成，设备已释放。");
}

/// 优先取 IPv4 地址，与发送引擎行为一致。
fn pick_endpoint(target: &AirPlayTarget) -> Option<SocketAddr> {
    target
        .addresses
        .iter()
        .copied()
        .find(|address| matches!(address, IpAddr::V4(_)))
        .or_else(|| target.addresses.first().copied())
        .map(|address| SocketAddr::new(address, target.port))
}
