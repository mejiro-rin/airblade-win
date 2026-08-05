//! 后台 AirPlay 发送引擎。
//! 网络、采集和节拍都在 Rust 线程中运行，FFI 只发送命令并轮询事件。

use std::{
    collections::VecDeque,
    net::SocketAddr,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, Sender},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

use crate::{
    audio::{
        AIRPLAY_CHANNELS, AIRPLAY_SAMPLE_RATE, ALAC_FRAMES_PER_PACKET, AudioSampleBuffer,
        InputPcmFormat, PcmConverter, WasapiCapture, processing::f32_to_i16, run_capture_loop,
    },
    error::{CoreError, Result, abi_error_code},
    event_channel::RemoteEvent,
    pairing_client::open_transient_control,
};
use windows::{
    Win32::System::Com::{COINIT_MULTITHREADED, CoInitializeEx},
    core::HRESULT,
};

pub enum EngineCommand {
    SetVolume(f32),
    Stop,
}

/// 连接策略由调用方按设备保存；核心不会持久化任何 UI 偏好。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum ConnectionPolicy {
    /// 默认策略。连接失败或断开后等待用户下一次 connect。
    Manual = 0,
    /// 仅对明确的暂时性网络故障进行有限次数重试。
    Automatic = 1,
}

impl TryFrom<i32> for ConnectionPolicy {
    type Error = CoreError;

    fn try_from(value: i32) -> Result<Self> {
        match value {
            0 => Ok(Self::Manual),
            1 => Ok(Self::Automatic),
            _ => Err(CoreError::InvalidArgument),
        }
    }
}

#[derive(Debug, Clone, PartialEq)]
pub enum EngineEvent {
    Paired,
    Streaming,
    Volume(f32),
    Reconnecting,
    Stopped,
    Error(i32),
}

/// 环回采集采样缓冲的时长（秒），按采集格式（如 48 kHz 双声道）计算容量。
/// 3 秒对实时推流来说过大：建流慢时只会积压更多旧音频，1 秒已足够
/// 吸收调度抖动，配合“满时保留最新采样”策略可以把时差控制在上限内。
const CAPTURE_RING_SECONDS: u32 = 1;

/// 发送前允许积压的 PCM 时长上限（毫秒）。超过后丢弃最旧采样，
/// 避免设备端与电脑播放的时差随网络卡顿不断拉大。
const MAX_PENDING_AUDIO_MS: usize = 200;

/// 积压超过上限后回落到这个目标时长（毫秒），一次性跳过多余的旧音频。
const TARGET_PENDING_AUDIO_MS: usize = 100;

/// 积压上限对应的 44.1 kHz 双声道交错采样数。
const MAX_PENDING_SAMPLES: usize =
    AIRPLAY_SAMPLE_RATE as usize * AIRPLAY_CHANNELS * MAX_PENDING_AUDIO_MS / 1000;

/// 积压回落后保留的 44.1 kHz 双声道交错采样数。
const TARGET_PENDING_SAMPLES: usize =
    AIRPLAY_SAMPLE_RATE as usize * AIRPLAY_CHANNELS * TARGET_PENDING_AUDIO_MS / 1000;

pub struct SenderEngine {
    commands: Sender<EngineCommand>,
    events: Receiver<EngineEvent>,
    running: Arc<AtomicBool>,
    protocol_worker: Option<JoinHandle<()>>,
    capture_worker: Option<JoinHandle<()>>,
}

impl SenderEngine {
    pub fn start(address: SocketAddr, policy: ConnectionPolicy) -> Result<Self> {
        // 环回捕获必须在 MTA 上创建和使用：WinUI 的 UI 线程是 STA，
        // 在 STA 上调用 CoInitializeEx(MTA) 会返回 RPC_E_CHANGED_MODE 导致连接直接失败。
        // 因此在采集线程内完成 COM 初始化和捕获器创建，再把 PCM 格式交给协议线程。
        let (format_tx, format_rx) = mpsc::channel::<Result<(InputPcmFormat, AudioSampleBuffer)>>();
        let running = Arc::new(AtomicBool::new(true));
        let capture_running = running.clone();
        let capture_worker = thread::Builder::new()
            .name("airblade-capture".into())
            .spawn(move || {
                unsafe {
                    let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
                }
                let capture = match WasapiCapture::new() {
                    Ok(capture) => capture,
                    Err(error) => {
                        let _ = format_tx.send(Err(error));
                        return;
                    }
                };
                let input_format = capture.format;
                let capacity = input_format.sample_rate as usize
                    * input_format.channels
                    * CAPTURE_RING_SECONDS as usize;
                let buffer = AudioSampleBuffer::new(capacity);
                if format_tx.send(Ok((input_format, buffer.clone()))).is_err() {
                    return;
                }
                if capture.start().is_ok() {
                    run_capture_loop(&capture, buffer, capture_running.clone());
                    let _ = capture.stop();
                } else {
                    capture_running.store(false, Ordering::Release);
                }
            })?;
        let (input_format, consumer) = format_rx.recv().map_err(|_| {
            CoreError::Windows(windows::core::Error::from_hresult(HRESULT(
                0x8000_4005u32 as i32,
            )))
        })??;

        let (command_tx, command_rx) = mpsc::channel();
        let (event_tx, event_rx) = mpsc::channel();
        let protocol_running = running.clone();
        let protocol_worker = thread::Builder::new()
            .name("airblade-sender".into())
            .spawn(move || {
                run_with_reconnect(
                    address,
                    policy,
                    input_format,
                    &consumer,
                    command_rx,
                    &event_tx,
                    protocol_running.clone(),
                );
                protocol_running.store(false, Ordering::Release);
                let _ = event_tx.send(EngineEvent::Stopped);
            })?;

        Ok(Self {
            commands: command_tx,
            events: event_rx,
            running,
            protocol_worker: Some(protocol_worker),
            capture_worker: Some(capture_worker),
        })
    }

    pub fn set_volume(&self, value: f32) -> Result<()> {
        self.commands
            .send(EngineCommand::SetVolume(value))
            .map_err(|_| CoreError::InvalidState("发送引擎已经停止"))
    }

    pub fn drain_events(&self) -> Vec<EngineEvent> {
        self.events.try_iter().collect()
    }

    pub fn stop(&mut self) {
        let _ = self.commands.send(EngineCommand::Stop);
        self.running.store(false, Ordering::Release);
        self.join();
    }

    fn join(&mut self) {
        if let Some(worker) = self.protocol_worker.take() {
            let _ = worker.join();
        }
        if let Some(worker) = self.capture_worker.take() {
            let _ = worker.join();
        }
    }
}

impl Drop for SenderEngine {
    fn drop(&mut self) {
        self.running.store(false, Ordering::Release);
        let _ = self.commands.send(EngineCommand::Stop);
        self.join();
    }
}

fn run_with_reconnect(
    address: SocketAddr,
    policy: ConnectionPolicy,
    input_format: InputPcmFormat,
    consumer: &AudioSampleBuffer,
    commands: Receiver<EngineCommand>,
    events: &Sender<EngineEvent>,
    running: Arc<AtomicBool>,
) {
    let mut retry = 0_u32;
    while running.load(Ordering::Acquire) {
        match run_protocol(
            address,
            input_format,
            consumer,
            &commands,
            events,
            running.clone(),
        ) {
            Ok(()) => return,
            Err(error)
                if running.load(Ordering::Acquire) && should_retry(policy, &error, retry) =>
            {
                // 只有暂时性网络故障才允许自动重试，避免与其他发送端争抢播放权。
                events.send(EngineEvent::Reconnecting).ok();
                retry = retry.saturating_add(1);
                let delay_ms = 500_u64.saturating_mul(1_u64 << retry.min(4));
                wait_for_retry(&commands, &running, Duration::from_millis(delay_ms));
            }
            Err(error) if running.load(Ordering::Acquire) => {
                events.send(EngineEvent::Error(abi_error_code(&error))).ok();
                return;
            }
            Err(_) => return,
        }
    }
}

/// 自动模式最多尝试三次，并只接受不会暗示接收端拒绝或已被接管的网络错误。
fn should_retry(policy: ConnectionPolicy, error: &CoreError, retry: u32) -> bool {
    if policy != ConnectionPolicy::Automatic || retry >= 3 {
        return false;
    }
    matches!(
        error,
        CoreError::Network(io)
            if matches!(
                io.kind(),
                std::io::ErrorKind::TimedOut
                    | std::io::ErrorKind::AddrNotAvailable
                    | std::io::ErrorKind::NetworkUnreachable
                    | std::io::ErrorKind::HostUnreachable
            )
    )
}

/// 重连等待期间仍然响应停止命令，避免关闭应用时最多卡住八秒。
fn wait_for_retry(commands: &Receiver<EngineCommand>, running: &AtomicBool, duration: Duration) {
    let deadline = Instant::now() + duration;
    while running.load(Ordering::Acquire) && Instant::now() < deadline {
        match commands.recv_timeout(Duration::from_millis(100)) {
            Ok(EngineCommand::Stop) => {
                running.store(false, Ordering::Release);
                return;
            }
            Ok(EngineCommand::SetVolume(_)) => {
                // 连接恢复前没有可发送音量的控制通道，恢复后由 UI 再同步当前值。
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                running.store(false, Ordering::Release);
                return;
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {}
        }
    }
}

fn run_protocol(
    address: SocketAddr,
    input_format: InputPcmFormat,
    consumer: &AudioSampleBuffer,
    commands: &Receiver<EngineCommand>,
    events: &Sender<EngineEvent>,
    running: Arc<AtomicBool>,
) -> Result<()> {
    let mut channel = open_transient_control(address)?;
    events.send(EngineEvent::Paired).ok();
    channel.get_info()?;
    let mut session = channel.setup_session()?;
    session.open_event_channel()?;
    session.record()?;
    session.setup_stream()?;
    session.send_playback_sync(true)?;
    events.send(EngineEvent::Streaming).ok();

    // 建流（配对、SETUP、首次同步）期间采集线程已积压了旧音频；直接发送
    // 会让设备端从一开始就落后一个建流耗时。发送首包前清空积压，保证
    // 首包接近实时。重连时也走这里，会一并清掉上次失败会话留下的旧音频。
    consumer.clear();

    let mut converter = PcmConverter::new(input_format.sample_rate, input_format.channels)?;
    let mut converted = VecDeque::<i16>::new();
    let packet_time =
        Duration::from_secs_f64(ALAC_FRAMES_PER_PACKET as f64 / AIRPLAY_SAMPLE_RATE as f64);
    let mut deadline = Instant::now();
    let mut next_sync = deadline + Duration::from_secs(1);
    let mut next_feedback = deadline + Duration::from_secs(25);
    while running.load(Ordering::Acquire) {
        for command in commands.try_iter() {
            match command {
                EngineCommand::SetVolume(value) => session.set_volume(value)?,
                EngineCommand::Stop => {
                    running.store(false, Ordering::Release);
                    break;
                }
            }
        }
        let raw = consumer.drain();
        if !raw.is_empty() {
            converted.extend(f32_to_i16(&converter.convert(&raw)));
        }
        // 积压超过上限说明网络暂时跟不上，丢弃最旧采样把时差压回目标值，
        // 并把发送节奏重新锚定到当前时间，避免用爆发追赶的方式把积压
        // 一股脑塞给接收端。
        if trim_stale_pending(&mut converted) > 0 {
            deadline = Instant::now();
        }
        let mut packet = [0_i16; ALAC_FRAMES_PER_PACKET * AIRPLAY_CHANNELS];
        for sample in &mut packet {
            if let Some(value) = converted.pop_front() {
                *sample = value;
            }
        }
        session.send_audio_packet(&packet)?;
        session.poll_retransmit_requests()?;
        for event in session.poll_remote_events()? {
            if let RemoteEvent::Volume(value) = event {
                events.send(EngineEvent::Volume(value)).ok();
            }
        }
        let now = Instant::now();
        if now >= next_sync {
            session.send_playback_sync(false)?;
            next_sync += Duration::from_secs(1);
        }
        if now >= next_feedback {
            session.feedback()?;
            next_feedback += Duration::from_secs(25);
        }
        deadline += packet_time;
        if let Some(wait) = deadline.checked_duration_since(Instant::now()) {
            thread::sleep(wait);
        }
    }
    let _ = session.teardown();
    Ok(())
}

/// 丢弃积压中最旧的采样，把队列长度压回目标值；返回丢弃的采样数。
fn trim_stale_pending(converted: &mut VecDeque<i16>) -> usize {
    if converted.len() > MAX_PENDING_SAMPLES {
        let drop = converted.len() - TARGET_PENDING_SAMPLES;
        converted.drain(..drop);
        drop
    } else {
        0
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn manual_policy_never_retries() {
        let error = CoreError::Network(std::io::Error::from(std::io::ErrorKind::TimedOut));
        assert!(!should_retry(ConnectionPolicy::Manual, &error, 0));
    }

    #[test]
    fn automatic_policy_only_retries_transient_network_errors() {
        let timeout = CoreError::Network(std::io::Error::from(std::io::ErrorKind::TimedOut));
        let reset = CoreError::Network(std::io::Error::from(std::io::ErrorKind::ConnectionReset));
        assert!(should_retry(ConnectionPolicy::Automatic, &timeout, 0));
        assert!(!should_retry(ConnectionPolicy::Automatic, &reset, 0));
        assert!(!should_retry(
            ConnectionPolicy::Automatic,
            &CoreError::Authentication,
            0
        ));
        assert!(!should_retry(
            ConnectionPolicy::Automatic,
            &CoreError::Protocol("bad"),
            0
        ));
        assert!(!should_retry(ConnectionPolicy::Automatic, &timeout, 3));
    }

    #[test]
    fn stop_during_retry_cancels_wait() {
        let (sender, receiver) = mpsc::channel();
        let running = AtomicBool::new(true);
        sender.send(EngineCommand::Stop).unwrap();
        wait_for_retry(&receiver, &running, Duration::from_secs(1));
        assert!(!running.load(Ordering::Acquire));
    }

    #[test]
    fn stale_pending_is_trimmed_to_target() {
        let total = MAX_PENDING_SAMPLES + 5000;
        let mut pending: VecDeque<i16> = (0..total as i16).collect();
        let dropped = trim_stale_pending(&mut pending);
        assert_eq!(dropped, total - TARGET_PENDING_SAMPLES);
        assert_eq!(pending.len(), TARGET_PENDING_SAMPLES);
        assert_eq!(*pending.front().unwrap(), dropped as i16);
        assert_eq!(*pending.back().unwrap(), (total - 1) as i16);
    }

    #[test]
    fn pending_below_limit_is_not_trimmed() {
        let mut pending: VecDeque<i16> = (0..MAX_PENDING_SAMPLES as i16).collect();
        assert_eq!(trim_stale_pending(&mut pending), 0);
        assert_eq!(pending.len(), MAX_PENDING_SAMPLES);
    }
}
