use airplay_core::ffi::{
    AirplayEvent, AirplayTargetInfo, airplay_discover_homepods, airplay_session_create,
    airplay_session_destroy, airplay_session_poll_event, airplay_session_set_volume,
    airplay_session_start, airplay_session_stop,
};
use std::{
    ffi::CStr,
    os::raw::c_char,
    thread,
    time::{Duration, Instant},
};

fn main() {
    let test_seconds = std::env::args()
        .nth(1)
        .and_then(|value| value.parse::<u64>().ok())
        .filter(|value| *value > 0)
        .unwrap_or(15);
    println!("FFI 测试时长：{test_seconds} 秒。测试期间可在 HomePod 上调节音量。 ");

    let mut targets: Vec<AirplayTargetInfo> = (0..8)
        .map(|_| AirplayTargetInfo {
            device_id: [0; 32],
            display_name: [0; 128],
            address: [0; 64],
            port: 0,
        })
        .collect();
    let mut count = 0usize;
    let result = unsafe {
        airplay_discover_homepods(5_000, targets.as_mut_ptr(), targets.len(), &mut count)
    };
    if result != 0 || count == 0 {
        eprintln!("FFI 发现失败：错误码={result}，设备数={count}");
        std::process::exit(1);
    }
    let target = &targets[0];
    let name = text(&target.display_name);
    let address = text(&target.address);
    let endpoint = format!("{address}:{}", target.port);
    println!("FFI 发现成功：{name}，{endpoint}");

    let handle = airplay_session_create();
    let endpoint = std::ffi::CString::new(endpoint).unwrap();
    let result = unsafe { airplay_session_start(handle, endpoint.as_ptr()) };
    if result != 0 {
        eprintln!("FFI 启动失败：{result}");
        let _ = airplay_session_destroy(handle);
        std::process::exit(1);
    }

    let start = Instant::now();
    let mut next_progress = Duration::from_secs(60);
    let mut volume_sent = false;
    while start.elapsed() < Duration::from_secs(test_seconds) {
        let mut event = AirplayEvent {
            kind: 0,
            state: 0,
            volume_db: 0.0,
            error_code: 0,
        };
        let result = unsafe { airplay_session_poll_event(handle, &mut event) };
        if result != 0 {
            eprintln!("FFI 轮询失败：{result}");
            break;
        }
        match event.kind {
            1 => {
                println!("FFI 状态事件：{}", event.state);
                if event.state == 3 && !volume_sent {
                    let volume_result = airplay_session_set_volume(handle, -18.0);
                    println!("FFI 设置音量结果：{volume_result}");
                    volume_sent = true;
                }
            }
            2 => println!("FFI 收到远端音量：{:.3} dB", event.volume_db),
            3 => {
                eprintln!("FFI 错误事件：{}", event.error_code);
                break;
            }
            _ => thread::sleep(Duration::from_millis(20)),
        }
        if start.elapsed() >= next_progress {
            println!("FFI 已稳定运行 {} 秒。", start.elapsed().as_secs());
            next_progress += Duration::from_secs(60);
        }
    }
    let stop = airplay_session_stop(handle);
    let destroy = airplay_session_destroy(handle);
    println!("FFI 停止={stop}，释放={destroy}。");
}

fn text<const N: usize>(value: &[c_char; N]) -> String {
    unsafe { CStr::from_ptr(value.as_ptr()) }
        .to_string_lossy()
        .into_owned()
}
