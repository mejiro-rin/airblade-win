//! 真机手动重连探针：第一次网络错误后保持运行，输入 r 才再次连接。

use airplay_core::ffi::{
    AirplayEvent, AirplayTargetInfo, airplay_discover_homepods, airplay_session_connect,
    airplay_session_create, airplay_session_destroy, airplay_session_disconnect,
    airplay_session_poll_event, airplay_session_set_connection_policy,
};
use std::{
    ffi::{CStr, CString},
    io,
    os::raw::c_char,
    sync::mpsc,
    thread,
    time::Duration,
};

fn main() {
    let endpoint = std::env::args()
        .nth(1)
        .or_else(discover_endpoint)
        .unwrap_or_else(|| {
            eprintln!("未发现可用 HomePod；也可以传入 IP:端口，例如 192.168.3.24:7000。");
            std::process::exit(2);
        });
    let handle = airplay_session_create();
    assert_ne!(handle, 0, "创建会话失败");
    assert_eq!(airplay_session_set_connection_policy(handle, 0), 0);
    connect(handle, &endpoint);

    let (command_tx, command_rx) = mpsc::channel();
    thread::spawn(move || {
        let stdin = io::stdin();
        loop {
            let mut line = String::new();
            if stdin.read_line(&mut line).is_err() {
                break;
            }
            if command_tx.send(line.trim().to_owned()).is_err() {
                break;
            }
        }
    });

    println!("手动连接探针已启动。输入 r 重连，输入 d 断开，输入 q 退出。");
    loop {
        while let Ok(command) = command_rx.try_recv() {
            match command.as_str() {
                "r" => connect(handle, &endpoint),
                "d" => println!("主动断开：{}", airplay_session_disconnect(handle)),
                "q" => {
                    let _ = airplay_session_disconnect(handle);
                    let _ = airplay_session_destroy(handle);
                    return;
                }
                _ => println!("未知命令；可用命令：r、d、q"),
            }
        }

        let mut event = AirplayEvent {
            kind: 0,
            state: 0,
            volume_db: 0.0,
            error_code: 0,
        };
        let result = unsafe { airplay_session_poll_event(handle, &mut event) };
        if result != 0 {
            eprintln!("轮询失败：{result}");
            break;
        }
        match event.kind {
            1 => println!("状态：{}", event.state),
            2 => println!("远端音量：{:.3} dB", event.volume_db),
            3 => eprintln!(
                "连接错误：{}；不会自动重连，输入 r 可再次连接。",
                event.error_code
            ),
            _ => {}
        }
        thread::sleep(Duration::from_millis(20));
    }
    let _ = airplay_session_disconnect(handle);
    let _ = airplay_session_destroy(handle);
}

fn connect(handle: u64, endpoint: &str) {
    let endpoint = CString::new(endpoint).expect("地址不能包含 NUL");
    let result = unsafe { airplay_session_connect(handle, endpoint.as_ptr()) };
    println!("请求连接：{result}");
}

fn discover_endpoint() -> Option<String> {
    for attempt in 1..=3 {
        if let Some(endpoint) = discover_endpoint_once() {
            return Some(endpoint);
        }
        eprintln!("第 {attempt} 次发现没有结果，稍后重试。");
        thread::sleep(Duration::from_millis(500));
    }
    None
}

fn discover_endpoint_once() -> Option<String> {
    let mut targets: Vec<AirplayTargetInfo> = (0..8)
        .map(|_| AirplayTargetInfo {
            device_id: [0; 32],
            display_name: [0; 128],
            address: [0; 64],
            port: 0,
        })
        .collect();
    let mut count = 0;
    let result = unsafe {
        airplay_discover_homepods(5_000, targets.as_mut_ptr(), targets.len(), &mut count)
    };
    if result != 0 || count == 0 {
        return None;
    }
    let target = &targets[0];
    let address =
        unsafe { CStr::from_ptr(target.address.as_ptr() as *const c_char) }.to_string_lossy();
    Some(format!("{address}:{}", target.port))
}
