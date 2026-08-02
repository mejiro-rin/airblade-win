//! HomePod 回调发送端时使用的最小 DACP 服务。
//! 本模块只接收音量属性，不实现播放列表、媒体库等 iTunes 遥控功能。

use mdns_sd::{ServiceDaemon, ServiceInfo};
use std::{
    io::{Read, Write},
    net::{IpAddr, TcpListener, TcpStream},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver},
    },
    thread::{self, JoinHandle},
    time::Duration,
};

use crate::error::{CoreError, Result};

pub struct DacpServer {
    daemon: ServiceDaemon,
    fullname: String,
    events: Receiver<f32>,
    running: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}

impl DacpServer {
    pub fn start(local_ip: IpAddr, dacp_id: &str, active_remote: u32) -> Result<Self> {
        let listener = TcpListener::bind((local_ip, 0))?;
        listener.set_nonblocking(true)?;
        let port = listener.local_addr()?.port();
        let daemon = ServiceDaemon::new().map_err(|e| CoreError::Discovery(e.to_string()))?;
        let instance = format!("iTunes_Ctrl_{dacp_id}");
        let properties = [
            ("txtvers", "1"),
            ("Ver", "131077"),
            ("DbId", "1"),
            ("OSsi", "0x2012E"),
        ];
        let service = ServiceInfo::new(
            "_dacp._tcp.local.",
            &instance,
            "AirBlade.local.",
            local_ip.to_string(),
            port,
            &properties[..],
        )
        .map_err(|e| CoreError::Discovery(e.to_string()))?;
        let fullname = service.get_fullname().to_owned();
        daemon
            .register(service)
            .map_err(|e| CoreError::Discovery(e.to_string()))?;

        let running = Arc::new(AtomicBool::new(true));
        let worker_running = running.clone();
        let (event_tx, event_rx) = mpsc::channel();
        let worker = thread::Builder::new()
            .name("airblade-dacp".into())
            .spawn(move || {
                while worker_running.load(Ordering::Acquire) {
                    match listener.accept() {
                        Ok((mut stream, _)) => {
                            if let Ok(Some(volume)) = handle_connection(&mut stream, active_remote)
                            {
                                event_tx.send(volume).ok();
                            }
                        }
                        Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                            thread::sleep(Duration::from_millis(10));
                        }
                        Err(_) => break,
                    }
                }
            })?;
        Ok(Self {
            daemon,
            fullname,
            events: event_rx,
            running,
            worker: Some(worker),
        })
    }

    pub fn drain_volumes(&self) -> Vec<f32> {
        self.events.try_iter().collect()
    }
}

impl Drop for DacpServer {
    fn drop(&mut self) {
        self.running.store(false, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
        let _ = self.daemon.unregister(&self.fullname);
        let _ = self.daemon.shutdown();
    }
}

fn handle_connection(stream: &mut TcpStream, active_remote: u32) -> Result<Option<f32>> {
    stream.set_read_timeout(Some(Duration::from_secs(2)))?;
    stream.set_write_timeout(Some(Duration::from_secs(2)))?;
    let mut request = [0_u8; 8192];
    let read = stream.read(&mut request)?;
    let text = std::str::from_utf8(&request[..read])
        .map_err(|_| CoreError::Protocol("DACP 请求不是 UTF-8"))?;
    let authorized = text.lines().any(|line| {
        line.split_once(':').is_some_and(|(name, value)| {
            name.eq_ignore_ascii_case("Active-Remote") && value.trim() == active_remote.to_string()
        })
    });
    if !authorized {
        stream.write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n")?;
        return Ok(None);
    }
    let target = text
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .ok_or(CoreError::Protocol("DACP 请求行错误"))?;
    let volume = parse_device_volume(target)?;
    stream.write_all(b"HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n")?;
    Ok(volume)
}

fn parse_device_volume(target: &str) -> Result<Option<f32>> {
    let Some((_, query)) = target.split_once('?') else {
        return Ok(None);
    };
    for item in query.split('&') {
        let Some((name, value)) = item.split_once('=') else {
            continue;
        };
        if name != "dmcp.device-volume" {
            continue;
        }
        let volume = value
            .parse::<f32>()
            .map_err(|_| CoreError::Protocol("DACP 音量数值错误"))?;
        if !volume.is_finite() || !(-144.0..=0.0).contains(&volume) {
            return Err(CoreError::Protocol("DACP 音量超出 AirPlay 范围"));
        }
        return Ok(Some(volume));
    }
    Ok(None)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_device_volume_from_dacp_query() {
        assert_eq!(
            parse_device_volume("/ctrl-int/1/setproperty?dmcp.device-volume=-23.5").unwrap(),
            Some(-23.5)
        );
        assert!(parse_device_volume("/ctrl-int/1/setproperty?dmcp.device-volume=2").is_err());
    }
}
