//! AirPlay 2 使用的最小 PTP 主时钟。
//! 它广播时钟声明和双阶段同步，并响应接收端的延迟测量请求。

use std::{
    io::ErrorKind,
    net::{IpAddr, Ipv4Addr, SocketAddr, UdpSocket},
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

use crate::error::{CoreError, Result};

const PTP_MULTICAST: Ipv4Addr = Ipv4Addr::new(224, 0, 1, 129);
const EVENT_PORT: u16 = 319;
const GENERAL_PORT: u16 = 320;

pub struct PtpMaster {
    running: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
    clock_id: u64,
    stats: Arc<PtpStatsAtomic>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct PtpStats {
    pub sync_sent: u64,
    pub announce_sent: u64,
    pub delay_requests: u64,
    pub event_received: u64,
    pub general_received: u64,
    pub received_types: u64,
}

#[derive(Default)]
struct PtpStatsAtomic {
    sync_sent: AtomicU64,
    announce_sent: AtomicU64,
    delay_requests: AtomicU64,
    event_received: AtomicU64,
    general_received: AtomicU64,
    received_types: AtomicU64,
}

impl PtpMaster {
    pub fn start(local_ip: IpAddr, peer_ip: IpAddr, clock_id: u64) -> Result<Self> {
        let IpAddr::V4(local_ip) = local_ip else {
            return Err(CoreError::Protocol("PTP 当前只支持 IPv4"));
        };
        let IpAddr::V4(peer_ip) = peer_ip else {
            return Err(CoreError::Protocol("PTP 接收端当前只支持 IPv4"));
        };
        let event = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, EVENT_PORT))?;
        let general = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, GENERAL_PORT))?;
        event.join_multicast_v4(&PTP_MULTICAST, &local_ip)?;
        general.join_multicast_v4(&PTP_MULTICAST, &local_ip)?;
        // Windows 可能同时存在 Wi-Fi、网线、VPN 和虚拟网卡；只加入组播组并不能
        // 保证发包走连接 HomePod 的那张网卡，因此必须显式指定出接口。
        socket2::SockRef::from(&event).set_multicast_if_v4(&local_ip)?;
        socket2::SockRef::from(&general).set_multicast_if_v4(&local_ip)?;
        event.set_multicast_ttl_v4(1)?;
        general.set_multicast_ttl_v4(1)?;
        // PTP 的 Sync 每 125 毫秒发送一次。使用非阻塞读取，避免两个套接字的
        // 读取超时叠加后拖慢发包节奏。
        event.set_nonblocking(true)?;
        general.set_nonblocking(true)?;
        event.set_multicast_loop_v4(false)?;
        general.set_multicast_loop_v4(false)?;

        let running = Arc::new(AtomicBool::new(true));
        let stats = Arc::new(PtpStatsAtomic::default());
        let worker_running = running.clone();
        let worker_stats = stats.clone();
        let worker = thread::Builder::new()
            .name("airblade-ptp".into())
            .spawn(move || {
                run_ptp(
                    event,
                    general,
                    peer_ip,
                    clock_id,
                    worker_running,
                    worker_stats,
                )
            })
            .map_err(CoreError::Network)?;
        Ok(Self {
            running,
            worker: Some(worker),
            clock_id,
            stats,
        })
    }

    pub fn clock_id(&self) -> u64 {
        self.clock_id
    }

    pub fn stats(&self) -> PtpStats {
        PtpStats {
            sync_sent: self.stats.sync_sent.load(Ordering::Relaxed),
            announce_sent: self.stats.announce_sent.load(Ordering::Relaxed),
            delay_requests: self.stats.delay_requests.load(Ordering::Relaxed),
            event_received: self.stats.event_received.load(Ordering::Relaxed),
            general_received: self.stats.general_received.load(Ordering::Relaxed),
            received_types: self.stats.received_types.load(Ordering::Relaxed),
        }
    }
}

impl Drop for PtpMaster {
    fn drop(&mut self) {
        self.running.store(false, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}

fn run_ptp(
    event: UdpSocket,
    general: UdpSocket,
    peer_ip: Ipv4Addr,
    clock_id: u64,
    running: Arc<AtomicBool>,
    stats: Arc<PtpStatsAtomic>,
) {
    // SETUP 中声明的是单播 PTP。报文必须直接发到接收端，不能一边设置
    // UNICAST 标志、一边发往组播地址，否则 HomePod 不会回应 Delay_Req。
    let event_target = SocketAddr::from((peer_ip, EVENT_PORT));
    let general_target = SocketAddr::from((peer_ip, GENERAL_PORT));
    let mut sync_sequence = 1_u16;
    let mut announce_sequence = 1_u16;
    let mut next_sync = Instant::now();
    let mut next_announce = Instant::now();
    let mut input = [0_u8; 256];

    while running.load(Ordering::Acquire) {
        let now = Instant::now();
        if now >= next_sync {
            let timestamp = ptp_timestamp();
            let _ = event.send_to(&sync_packet(clock_id, sync_sequence), event_target);
            let _ = general.send_to(
                &follow_up_packet(clock_id, sync_sequence, timestamp),
                general_target,
            );
            stats.sync_sent.fetch_add(1, Ordering::Relaxed);
            sync_sequence = sync_sequence.wrapping_add(1);
            next_sync = now + Duration::from_millis(125);
        }
        if now >= next_announce {
            let _ = general.send_to(
                &announce_packet(clock_id, announce_sequence),
                general_target,
            );
            stats.announce_sent.fetch_add(1, Ordering::Relaxed);
            announce_sequence = announce_sequence.wrapping_add(1);
            next_announce = now + Duration::from_secs(1);
        }

        match event.recv_from(&mut input) {
            Ok((length, sender)) => {
                stats.event_received.fetch_add(1, Ordering::Relaxed);
                if length > 0 {
                    let message_type = input[0] & 0x0f;
                    stats
                        .received_types
                        .fetch_or(1_u64 << message_type, Ordering::Relaxed);
                }
                if length < 34 || input[0] & 0x0f != 1 {
                    continue;
                }
                let sequence = u16::from_be_bytes([input[30], input[31]]);
                let mut requester = [0_u8; 10];
                requester.copy_from_slice(&input[20..30]);
                let response = delay_response(clock_id, sequence, ptp_timestamp(), requester);
                let _ = general.send_to(&response, SocketAddr::new(sender.ip(), GENERAL_PORT));
                stats.delay_requests.fetch_add(1, Ordering::Relaxed);
            }
            Err(error) if matches!(error.kind(), ErrorKind::WouldBlock | ErrorKind::TimedOut) => {
                thread::sleep(Duration::from_millis(2));
            }
            Err(_) => break,
        }
        loop {
            match general.recv_from(&mut input) {
                Ok((length, _)) => {
                    stats.general_received.fetch_add(1, Ordering::Relaxed);
                    if length > 0 {
                        let message_type = input[0] & 0x0f;
                        stats
                            .received_types
                            .fetch_or(1_u64 << message_type, Ordering::Relaxed);
                    }
                }
                Err(error)
                    if matches!(error.kind(), ErrorKind::WouldBlock | ErrorKind::TimedOut) =>
                {
                    break;
                }
                Err(_) => return,
            }
        }
    }
}

fn header(
    message_type: u8,
    length: u16,
    flags: u16,
    clock_id: u64,
    sequence: u16,
    control: u8,
    interval: i8,
) -> Vec<u8> {
    let mut packet = vec![0_u8; length as usize];
    // Apple 的 PTP 配置使用 transportSpecific=1；缺少这个高半字节时，
    // HomePod 能在网络上看到报文，但不会把它当作可用的 AirPlay 时钟。
    packet[0] = 0x10 | (message_type & 0x0f);
    packet[1] = 0x02;
    packet[2..4].copy_from_slice(&length.to_be_bytes());
    packet[6..8].copy_from_slice(&flags.to_be_bytes());
    packet[20..28].copy_from_slice(&clock_id.to_be_bytes());
    // 与 iOS/OwnTone 一致的 PTP 端口号 0x8005。
    packet[28..30].copy_from_slice(&0x8005_u16.to_be_bytes());
    packet[30..32].copy_from_slice(&sequence.to_be_bytes());
    packet[32] = control;
    packet[33] = interval as u8;
    packet
}

fn sync_packet(clock_id: u64, sequence: u16) -> Vec<u8> {
    // 双阶段同步的 Sync 时间戳必须为零，精确时间放在随后发送的 Follow_Up。
    header(0, 44, 0x0608, clock_id, sequence, 0, -3)
}

fn follow_up_packet(clock_id: u64, sequence: u16, timestamp: [u8; 10]) -> Vec<u8> {
    let mut packet = header(8, 96, 0x0408, clock_id, sequence, 0, -3);
    packet[34..44].copy_from_slice(&timestamp);

    // IEEE 802.1 Follow_Up 信息 TLV。负载先写组织代码和子类型，
    // 后面的时钟速率修正字段保持为零。
    packet[44..46].copy_from_slice(&0x0003_u16.to_be_bytes());
    packet[46..48].copy_from_slice(&28_u16.to_be_bytes());
    packet[48..51].copy_from_slice(&[0x00, 0x80, 0xc2]);
    packet[51..54].copy_from_slice(&[0x00, 0x00, 0x01]);

    // Apple Clock ID TLV，HomePod 用它确认 Follow_Up 属于哪个主时钟。
    packet[76..78].copy_from_slice(&0x0003_u16.to_be_bytes());
    packet[78..80].copy_from_slice(&16_u16.to_be_bytes());
    packet[80..83].copy_from_slice(&[0x00, 0x0d, 0x93]);
    packet[83..86].copy_from_slice(&[0x00, 0x00, 0x04]);
    packet[86..94].copy_from_slice(&clock_id.to_be_bytes());
    packet
}

fn announce_packet(clock_id: u64, sequence: u16) -> Vec<u8> {
    let mut packet = header(11, 76, 0x0408, clock_id, sequence, 0, 0);
    // iOS 的 Announce originTimestamp 为零。
    packet[47] = 128;
    // Apple 使用的优质 GPS 时钟参数。较差的 248/unknown 参数会让
    // HomePod 在最佳主时钟算法中选择局域网内的其他 Apple 时钟。
    packet[48..52].copy_from_slice(&0x0621_436a_u32.to_be_bytes());
    packet[52] = 128;
    packet[53..61].copy_from_slice(&clock_id.to_be_bytes());
    packet[63] = 0x20;
    packet[64..66].copy_from_slice(&0x0008_u16.to_be_bytes());
    packet[66..68].copy_from_slice(&8_u16.to_be_bytes());
    packet[68..76].copy_from_slice(&clock_id.to_be_bytes());
    packet
}

fn delay_response(
    clock_id: u64,
    sequence: u16,
    timestamp: [u8; 10],
    requester: [u8; 10],
) -> Vec<u8> {
    let mut packet = header(9, 54, 0x0608, clock_id, sequence, 0, -3);
    packet[34..44].copy_from_slice(&timestamp);
    packet[44..54].copy_from_slice(&requester);
    packet
}

pub fn ptp_timestamp() -> [u8; 10] {
    let duration = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    let seconds = duration.as_secs();
    let mut timestamp = [0_u8; 10];
    timestamp[..6].copy_from_slice(&seconds.to_be_bytes()[2..]);
    timestamp[6..].copy_from_slice(&duration.subsec_nanos().to_be_bytes());
    timestamp
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ptp_packets_have_expected_types_and_lengths() {
        let sync = sync_packet(7, 3);
        let follow = follow_up_packet(7, 3, [1; 10]);
        let announce = announce_packet(7, 4);
        assert_eq!((sync[0] & 0x0f, sync.len()), (0, 44));
        assert_eq!((follow[0] & 0x0f, follow.len()), (8, 96));
        assert_eq!((announce[0] & 0x0f, announce.len()), (11, 76));
        assert_eq!(sync[0] >> 4, 1);
        assert_eq!(&sync[6..8], &0x0608_u16.to_be_bytes());
        assert_eq!(&sync[28..30], &0x8005_u16.to_be_bytes());
        assert_eq!(&follow[76..80], &[0x00, 0x03, 0x00, 0x10]);
        assert_eq!(&announce[64..68], &[0x00, 0x08, 0x00, 0x08]);
        assert_eq!(&sync[20..28], &7_u64.to_be_bytes());
    }
}
