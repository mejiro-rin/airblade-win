use airplay_core::discovery::{discover_airplay_targets, is_homepod};
use std::time::Duration;

fn main() {
    println!("正在搜索局域网中的 AirPlay 音频接收端（8 秒）...");
    match discover_airplay_targets(Duration::from_secs(8)) {
        Ok(targets) if targets.is_empty() => {
            println!("未发现 HomePod。请确认电脑与 HomePod 在同一局域网，且防火墙允许 UDP 5353。")
        }
        Ok(targets) => {
            for target in targets {
                println!(
                    "名称: {}\n设备 ID: {}\n地址: {:?}:{}\nAirPlay 控制端口: {:?}\n型号: {:?}\n功能: {:?}\n状态标志: {:?}\n",
                    target.display_name,
                    target.device_id,
                    target.addresses,
                    target.port,
                    target.airplay_port,
                    target.model,
                    target.features,
                    target.status_flags
                );
                println!(
                    "识别结果: {}\n",
                    if is_homepod(&target) {
                        "HomePod"
                    } else {
                        "其他 AirPlay 接收端（缺少或未知 model 字段）"
                    }
                );
            }
        }
        Err(error) => {
            eprintln!("发现失败: {error}");
            std::process::exit(1);
        }
    }
}
