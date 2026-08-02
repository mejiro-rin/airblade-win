//! 后台 AirPlay 发送引擎。
//! 网络、采集和节拍都在 Rust 线程中运行，FFI 只发送命令并轮询事件。

use ringbuf::{HeapCons, traits::Consumer};
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
        InputPcmFormat, PcmConverter, WasapiCapture, create_ring, processing::f32_to_i16,
        run_capture_loop,
    },
    error::{CoreError, Result},
    event_channel::RemoteEvent,
    pairing_client::open_transient_control,
};

pub enum EngineCommand {
    SetVolume(f32),
    Stop,
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

pub struct SenderEngine {
    commands: Sender<EngineCommand>,
    events: Receiver<EngineEvent>,
    running: Arc<AtomicBool>,
    protocol_worker: Option<JoinHandle<()>>,
    capture_worker: Option<JoinHandle<()>>,
}

impl SenderEngine {
    pub fn start(address: SocketAddr) -> Result<Self> {
        let capture = WasapiCapture::new()?;
        let input_format = capture.format;
        let capacity = input_format.sample_rate as usize * input_format.channels * 3;
        let (producer, consumer) = create_ring(capacity);
        let running = Arc::new(AtomicBool::new(true));
        let capture_running = running.clone();
        let capture_worker = thread::Builder::new()
            .name("airblade-capture".into())
            .spawn(move || {
                if capture.start().is_ok() {
                    run_capture_loop(&capture, producer, capture_running.clone());
                    let _ = capture.stop();
                } else {
                    capture_running.store(false, Ordering::Release);
                }
            })?;

        let (command_tx, command_rx) = mpsc::channel();
        let (event_tx, event_rx) = mpsc::channel();
        let protocol_running = running.clone();
        let protocol_worker = thread::Builder::new()
            .name("airblade-sender".into())
            .spawn(move || {
                let mut consumer = consumer;
                run_with_reconnect(
                    address,
                    input_format,
                    &mut consumer,
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
    input_format: InputPcmFormat,
    consumer: &mut HeapCons<f32>,
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
            Err(error) if running.load(Ordering::Acquire) => {
                events.send(EngineEvent::Error(error_code(&error))).ok();
                events.send(EngineEvent::Reconnecting).ok();
                retry = retry.saturating_add(1);
                let delay_ms = 500_u64.saturating_mul(1_u64 << retry.min(4));
                wait_for_retry(&commands, &running, Duration::from_millis(delay_ms));
            }
            Err(_) => return,
        }
    }
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
    consumer: &mut HeapCons<f32>,
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

    let mut converter = PcmConverter::new(input_format.sample_rate, input_format.channels)?;
    let mut converted = VecDeque::<i16>::new();
    let mut raw = Vec::new();
    let packet_time = Duration::from_secs_f64(352.0 / 44_100.0);
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
        raw.clear();
        while let Some(sample) = consumer.try_pop() {
            raw.push(sample);
        }
        if !raw.is_empty() {
            converted.extend(f32_to_i16(&converter.convert(&raw)));
        }
        let mut packet = [0_i16; 704];
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

fn error_code(error: &CoreError) -> i32 {
    match error {
        CoreError::InvalidArgument => -1,
        CoreError::InvalidState(_) => -2,
        CoreError::Protocol(_) | CoreError::RtspBody(_) => -3,
        CoreError::Authentication => -4,
        CoreError::Network(_) | CoreError::Windows(_) => -5,
        CoreError::Discovery(_) => -6,
        CoreError::PairingStatus(_) => -7,
        CoreError::PairingTlv(_) => -8,
        CoreError::RtspStatus(_, _) => -9,
    }
}
