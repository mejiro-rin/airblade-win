//! HomePod HAP transient 配对客户端。
//! M1-M4 必须在同一条 TCP 连接完成；任何 SRP 证明不匹配都会立即终止。
use std::{
    collections::VecDeque,
    io::{Read, Write},
    net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr, TcpStream, UdpSocket},
    time::Duration,
};

use crate::hap_tlv::TYPE_PROOF;
use crate::{
    dacp::DacpServer,
    error::{CoreError, Result},
    event_channel::{EventChannel, RemoteEvent, parse_volume},
    hap_srp::HapSrpClient,
    hap_tlv::{TYPE_ERROR, TYPE_FLAGS, TYPE_METHOD, TYPE_PUBLIC_KEY, TYPE_SALT, TYPE_STATE, Tlv8},
    protocol::{
        EncryptedFramer, MAX_CONTROL_FRAME, RtspMessage, hkdf_sha512, parse_rtsp, rtsp_message_len,
    },
    ptp::PtpMaster,
    rtp::{AudioEncryptor, RtpClock, ptp_sync_packet},
};

const TRANSIENT_PAIRING_FLAG: u8 = 0x10;

#[derive(Debug, Clone)]
pub struct PairSetupM2 {
    pub salt: Vec<u8>,
    pub server_public_key: Vec<u8>,
}

#[derive(Debug, Clone)]
pub struct TransientPairingKeys {
    /// SRP 会话密钥为 64 字节；音频加密只使用前 32 字节。
    pub shared_secret: Vec<u8>,
    pub control_write_key: Vec<u8>,
    pub control_read_key: Vec<u8>,
}

/// 配对完成后仍保持在线的 AirPlay 加密控制连接。
/// 发送和接收各自使用独立密钥、独立计数器，不能混用。
pub struct PairedControlChannel {
    stream: TcpStream,
    keys: TransientPairingKeys,
    writer: EncryptedFramer,
    reader: EncryptedFramer,
    encrypted_input: Vec<u8>,
    plaintext_input: Vec<u8>,
    next_cseq: u32,
    info_received: bool,
    receiver_features: Option<u64>,
    remote_events: VecDeque<RemoteEvent>,
    dacp_id: String,
    active_remote: u32,
    dacp_server: DacpServer,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AirPlayInfo {
    pub name: Option<String>,
    pub model: Option<String>,
    pub device_id: Option<String>,
    pub features: Option<u64>,
    pub status_flags: Option<u64>,
    pub latency_min: Option<u64>,
    pub body_bytes: usize,
}

/// 会话级 SETUP 成功后的资源和接收端参数。
/// timing UDP 端口和加密控制连接都由该对象持有，防止建立后被提前关闭。
pub struct AirPlayControlSession {
    channel: PairedControlChannel,
    timing_socket: Option<UdpSocket>,
    timing_protocol: &'static str,
    ptp_master: Option<PtpMaster>,
    session_id: u32,
    session_uri: String,
    event_port: u16,
    latency_min: Option<u64>,
    latency_max: Option<u64>,
    event_channel: Option<EventChannel>,
    control_socket: Option<UdpSocket>,
    audio_socket: Option<UdpSocket>,
    data_port: Option<u16>,
    receiver_control_port: Option<u16>,
    recorded: bool,
    audio_latency_samples: Option<u64>,
    rtp_clock: Option<RtpClock>,
    audio_encryptor: Option<AudioEncryptor>,
    first_audio_packet: bool,
    retransmit_backlog: VecDeque<(u16, Vec<u8>)>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SessionSetupInfo {
    pub session_id: u32,
    pub session_uri: String,
    pub timing_port: u16,
    pub timing_protocol: &'static str,
    pub event_port: u16,
    pub latency_min: Option<u64>,
    pub latency_max: Option<u64>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StreamSetupInfo {
    pub data_port: u16,
    pub control_port: u16,
    pub local_control_port: u16,
    pub latency_min_samples: u64,
    pub latency_max_samples: u64,
    pub receiver_audio_latency_samples: Option<u64>,
}

/// 请求 HomePod 开始无 PIN 的 transient 配对，并验证其 M2 响应结构。
pub fn begin_transient_pairing(address: SocketAddr) -> Result<PairSetupM2> {
    let mut stream = connect(address)?;
    begin_transient_pairing_on(&mut stream)
}

/// 完成 HomePod transient 配对并返回控制通道密钥。
pub fn complete_transient_pairing(address: SocketAddr) -> Result<TransientPairingKeys> {
    Ok(open_transient_control(address)?.keys.clone())
}

/// 完成 transient 配对，并把完成 M1-M4 的同一条 TCP 连接交给控制通道继续使用。
pub fn open_transient_control(address: SocketAddr) -> Result<PairedControlChannel> {
    let mut stream = connect(address)?;
    let m2 = begin_transient_pairing_on(&mut stream)?;
    let srp = HapSrpClient::start(&m2.salt, &m2.server_public_key, "3939")?;
    let mut m3 = Tlv8::default();
    m3.insert(TYPE_STATE, [0x03]);
    m3.insert(TYPE_PUBLIC_KEY, srp.public_a());
    m3.insert(TYPE_PROOF, srp.proof_m1());
    let response = post_tlv_on(&mut stream, "/pair-setup", &m3.encode(), 1)?;
    let tlv = parse_pairing_response(response, 0x04)?;
    let proof = tlv
        .get(TYPE_PROOF)
        .ok_or(CoreError::Protocol("M4 缺少服务器证明"))?;
    srp.verify_server_proof(proof)?;
    let shared_secret = srp.session_key().to_vec();
    let control_write_key = hkdf_sha512(
        &shared_secret,
        b"Control-Salt",
        b"Control-Write-Encryption-Key",
        32,
    )?;
    let control_read_key = hkdf_sha512(
        &shared_secret,
        b"Control-Salt",
        b"Control-Read-Encryption-Key",
        32,
    )?;
    let keys = TransientPairingKeys {
        shared_secret,
        control_write_key,
        control_read_key,
    };
    let write_key: [u8; 32] = keys
        .control_write_key
        .as_slice()
        .try_into()
        .map_err(|_| CoreError::Protocol("控制写密钥长度错误"))?;
    let read_key: [u8; 32] = keys
        .control_read_key
        .as_slice()
        .try_into()
        .map_err(|_| CoreError::Protocol("控制读密钥长度错误"))?;
    let dacp_id = format!("{:016X}", random_u64()?);
    let active_remote = random_u32()?;
    let dacp_server = DacpServer::start(stream.local_addr()?.ip(), &dacp_id, active_remote)?;
    Ok(PairedControlChannel {
        stream,
        keys,
        writer: EncryptedFramer::new(write_key),
        reader: EncryptedFramer::new(read_key),
        encrypted_input: Vec::new(),
        plaintext_input: Vec::new(),
        next_cseq: 2,
        info_received: false,
        receiver_features: None,
        remote_events: VecDeque::new(),
        dacp_id,
        active_remote,
        dacp_server,
    })
}

impl PairedControlChannel {
    pub fn keys(&self) -> &TransientPairingKeys {
        &self.keys
    }

    /// 在已经配对的加密连接上读取 HomePod 的 `/info`。
    pub fn get_info(&mut self) -> Result<AirPlayInfo> {
        let cseq = self.next_cseq;
        self.next_cseq = self
            .next_cseq
            .checked_add(1)
            .ok_or(CoreError::Protocol("CSeq 已耗尽"))?;
        let request = format!(
            "GET /info RTSP/1.0\r\nCSeq: {cseq}\r\nUser-Agent: AirPlay/550.10\r\nDACP-ID: {}\r\nActive-Remote: {}\r\nClient-Instance: {}\r\nX-Apple-Client-Name: AirBlade\r\n\r\n",
            self.dacp_id, self.active_remote, self.dacp_id
        );
        self.write_encrypted(request.as_bytes())?;
        let response = self.read_encrypted_message()?;
        require_success(&response, "GET /info")?;
        let info = parse_airplay_info(&response.body)?;
        self.info_received = true;
        self.receiver_features = info.features;
        Ok(info)
    }

    /// 建立 AirPlay 2 控制会话并取得 event channel 端口。
    /// 必须先成功调用 `/info`，以保持协议规定的请求顺序。
    pub fn setup_session(mut self) -> Result<AirPlayControlSession> {
        if !self.info_received {
            return Err(CoreError::InvalidState("SETUP 前必须先读取 /info"));
        }
        let local_ip = self.stream.local_addr()?.ip();
        // PTP 使用定向单播，必须把控制连接实际连接到的 HomePod 地址
        // 传给主时钟，不能继续把同步包发往组播地址。
        let receiver_ip = self.stream.peer_addr()?.ip();
        let session_id = random_u32()?;
        let session_uri = format!("rtsp://{local_ip}/{session_id}");
        let session_uuid = random_uuid()?;
        let sender_id = random_sender_id()?;
        let use_ptp = self
            .receiver_features
            .is_some_and(|features| features & (1_u64 << 41) != 0);
        let (body, timing_socket, timing_protocol, ptp_master) = if use_ptp {
            let clock_id = random_u64()?;
            let clock_uuid = random_uuid()?;
            (
                encode_session_setup_ptp(
                    &sender_id,
                    &session_uuid,
                    local_ip,
                    clock_id,
                    &clock_uuid,
                )?,
                None,
                "PTP",
                Some(PtpMaster::start(local_ip, receiver_ip, clock_id)?),
            )
        } else {
            let bind_ip = match local_ip {
                IpAddr::V4(_) => IpAddr::V4(Ipv4Addr::UNSPECIFIED),
                IpAddr::V6(_) => IpAddr::V6(Ipv6Addr::UNSPECIFIED),
            };
            let socket = UdpSocket::bind(SocketAddr::new(bind_ip, 0))?;
            let port = socket.local_addr()?.port();
            (
                encode_session_setup_ntp(&sender_id, &session_uuid, port)?,
                Some(socket),
                "NTP",
                None,
            )
        };
        let response = self.send_request(
            "SETUP",
            &session_uri,
            Some("application/x-apple-binary-plist"),
            &body,
        )?;
        require_success(&response, "session SETUP")?;
        let value = parse_binary_plist(&response.body, "session SETUP")?;
        let event_port = find_number(&value, "eventPort")
            .and_then(|value| u16::try_from(value).ok())
            .filter(|value| *value != 0)
            .ok_or(CoreError::Protocol("session SETUP 缺少有效的 eventPort"))?;
        let latency_min = find_number(&value, "latencyMin");
        let latency_max = find_number(&value, "latencyMax");
        Ok(AirPlayControlSession {
            channel: self,
            timing_socket,
            timing_protocol,
            ptp_master,
            session_id,
            session_uri,
            event_port,
            latency_min,
            latency_max,
            event_channel: None,
            control_socket: None,
            audio_socket: None,
            data_port: None,
            receiver_control_port: None,
            recorded: false,
            audio_latency_samples: None,
            rtp_clock: None,
            audio_encryptor: None,
            first_audio_packet: true,
            retransmit_backlog: VecDeque::with_capacity(1000),
        })
    }

    fn send_request(
        &mut self,
        method: &str,
        uri: &str,
        content_type: Option<&str>,
        body: &[u8],
    ) -> Result<RtspMessage> {
        let cseq = self.next_cseq;
        self.next_cseq = self
            .next_cseq
            .checked_add(1)
            .ok_or(CoreError::Protocol("CSeq 已耗尽"))?;
        let mut request = format!(
            "{method} {uri} RTSP/1.0\r\nCSeq: {cseq}\r\nUser-Agent: AirPlay/550.10\r\nDACP-ID: {}\r\nActive-Remote: {}\r\nClient-Instance: {}\r\nX-Apple-Client-Name: AirBlade\r\n",
            self.dacp_id, self.active_remote, self.dacp_id
        );
        if let Some(content_type) = content_type {
            request.push_str(&format!("Content-Type: {content_type}\r\n"));
        }
        if !body.is_empty() {
            request.push_str(&format!("Content-Length: {}\r\n", body.len()));
        }
        request.push_str("\r\n");
        let mut request = request.into_bytes();
        request.extend_from_slice(body);
        self.write_encrypted(&request)?;
        self.read_response_handling_requests()
    }

    /// 等待当前请求的响应；如果 HomePod 抢先发来主动请求，先回复它再继续等。
    fn read_response_handling_requests(&mut self) -> Result<RtspMessage> {
        loop {
            let message = self.read_encrypted_message()?;
            if message.start_line.starts_with("RTSP/") {
                return Ok(message);
            }
            self.handle_remote_request(message)?;
        }
    }

    /// 非阻塞读取主控制连接上的主动请求。
    fn poll_remote_requests(&mut self) -> Result<Vec<RemoteEvent>> {
        self.stream.set_nonblocking(true)?;
        let read_result = (|| -> Result<()> {
            let mut chunk = [0_u8; 4096];
            loop {
                match self.stream.read(&mut chunk) {
                    Ok(0) => return Err(CoreError::Protocol("HomePod 提前关闭了控制连接")),
                    Ok(read) => {
                        self.encrypted_input.extend_from_slice(&chunk[..read]);
                        if self.encrypted_input.len() > 128 * 1024 {
                            return Err(CoreError::Protocol("加密控制请求过大"));
                        }
                    }
                    Err(error)
                        if matches!(
                            error.kind(),
                            std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                        ) =>
                    {
                        return Ok(());
                    }
                    Err(error) => return Err(error.into()),
                }
            }
        })();
        let restore_result = self.stream.set_nonblocking(false);
        read_result?;
        restore_result?;

        self.decrypt_complete_frames()?;
        while let Some(message_len) = rtsp_message_len(&self.plaintext_input)? {
            let bytes: Vec<u8> = self.plaintext_input.drain(..message_len).collect();
            let message = parse_rtsp(&bytes)?;
            if message.start_line.starts_with("RTSP/") {
                return Err(CoreError::Protocol("控制通道收到没有对应请求的响应"));
            }
            self.handle_remote_request(message)?;
        }
        Ok(self.remote_events.drain(..).collect())
    }

    fn handle_remote_request(&mut self, request: RtspMessage) -> Result<()> {
        let event = parse_volume(&request)?
            .map(RemoteEvent::Volume)
            .unwrap_or_else(|| RemoteEvent::Request(request.start_line.clone()));
        self.reply_ok(&request)?;
        self.remote_events.push_back(event);
        Ok(())
    }

    fn reply_ok(&mut self, request: &RtspMessage) -> Result<()> {
        let cseq = request
            .headers
            .iter()
            .find(|(name, _)| name.eq_ignore_ascii_case("cseq"))
            .map(|(_, value)| value.as_str());
        let mut response = String::from("RTSP/1.0 200 OK\r\nServer: AirTunes/550.10\r\n");
        if let Some(cseq) = cseq {
            response.push_str(&format!("CSeq: {cseq}\r\n"));
        }
        response.push_str("\r\n");
        self.write_encrypted(response.as_bytes())
    }

    fn write_encrypted(&mut self, plaintext: &[u8]) -> Result<()> {
        for chunk in plaintext.chunks(MAX_CONTROL_FRAME) {
            let frame = self.writer.seal(chunk)?;
            self.stream.write_all(&frame)?;
        }
        self.stream.flush()?;
        Ok(())
    }

    fn read_encrypted_message(&mut self) -> Result<RtspMessage> {
        loop {
            if let Some(message_len) = rtsp_message_len(&self.plaintext_input)? {
                let message: Vec<u8> = self.plaintext_input.drain(..message_len).collect();
                return parse_rtsp(&message);
            }
            self.decrypt_complete_frames()?;
            if rtsp_message_len(&self.plaintext_input)?.is_some() {
                continue;
            }
            let mut chunk = [0_u8; 4096];
            let read = self.stream.read(&mut chunk)?;
            if read == 0 {
                return Err(CoreError::Protocol("HomePod 提前关闭了控制连接"));
            }
            self.encrypted_input.extend_from_slice(&chunk[..read]);
            if self.encrypted_input.len() > 128 * 1024 {
                return Err(CoreError::Protocol("加密控制响应过大"));
            }
        }
    }

    fn decrypt_complete_frames(&mut self) -> Result<()> {
        loop {
            if self.encrypted_input.len() < 2 {
                return Ok(());
            }
            let plaintext_len =
                u16::from_le_bytes([self.encrypted_input[0], self.encrypted_input[1]]) as usize;
            if plaintext_len > MAX_CONTROL_FRAME {
                return Err(CoreError::Protocol("加密控制帧长度错误"));
            }
            let frame_len = 2 + plaintext_len + 16;
            if self.encrypted_input.len() < frame_len {
                return Ok(());
            }
            let frame: Vec<u8> = self.encrypted_input.drain(..frame_len).collect();
            let plaintext = self.reader.open(&frame)?;
            self.plaintext_input.extend_from_slice(&plaintext);
        }
    }
}

impl AirPlayControlSession {
    pub fn setup_info(&self) -> Result<SessionSetupInfo> {
        Ok(SessionSetupInfo {
            session_id: self.session_id,
            session_uri: self.session_uri.clone(),
            timing_port: self
                .timing_socket
                .as_ref()
                .map(|socket| socket.local_addr().map(|address| address.port()))
                .transpose()?
                .unwrap_or(0),
            timing_protocol: self.timing_protocol,
            event_port: self.event_port,
            latency_min: self.latency_min,
            latency_max: self.latency_max,
        })
    }

    pub fn ptp_stats(&self) -> Option<crate::ptp::PtpStats> {
        self.ptp_master.as_ref().map(PtpMaster::stats)
    }

    pub fn control_channel(&mut self) -> &mut PairedControlChannel {
        &mut self.channel
    }

    /// 连接 HomePod 在 session SETUP 中分配的反向事件端口。
    pub fn open_event_channel(&mut self) -> Result<()> {
        if self.event_channel.is_some() {
            return Ok(());
        }
        let receiver_ip = self.channel.stream.peer_addr()?.ip();
        let receiver = SocketAddr::new(receiver_ip, self.event_port);
        self.event_channel = Some(EventChannel::connect(
            receiver,
            &self.channel.keys.shared_secret,
        )?);
        Ok(())
    }

    /// event channel 在线后通知接收端进入 RECORD 状态。
    pub fn record(&mut self) -> Result<()> {
        if self.event_channel.is_none() {
            return Err(CoreError::InvalidState("RECORD 前必须建立事件通道"));
        }
        let uri = self.session_uri.clone();
        let response = self.channel.send_request("RECORD", &uri, None, &[])?;
        require_success(&response, "RECORD")?;
        self.audio_latency_samples = response
            .headers
            .iter()
            .find(|(name, _)| name.eq_ignore_ascii_case("audio-latency"))
            .and_then(|(_, value)| value.parse::<u64>().ok());
        self.recorded = true;
        Ok(())
    }

    pub fn poll_remote_events(&mut self) -> Result<Vec<RemoteEvent>> {
        let mut events = self.channel.poll_remote_requests()?;
        events.extend(
            self.channel
                .dacp_server
                .drain_volumes()
                .into_iter()
                .map(RemoteEvent::Volume),
        );
        events.extend(
            self.event_channel
                .as_mut()
                .ok_or(CoreError::InvalidState("事件通道尚未建立"))?
                .poll()?,
        );
        Ok(events)
    }

    /// 协商实时 ALAC 音频流，并连接 HomePod 返回的 RTP 数据端口。
    pub fn setup_stream(&mut self) -> Result<StreamSetupInfo> {
        const LATENCY_MIN: u64 = 11_025;
        const LATENCY_MAX: u64 = 88_200;
        if !self.recorded {
            return Err(CoreError::InvalidState("stream SETUP 前必须完成 RECORD"));
        }
        if self.data_port.is_some() {
            return Err(CoreError::InvalidState("音频流已经建立"));
        }
        let local_ip = self.channel.stream.local_addr()?.ip();
        let bind_ip = match local_ip {
            IpAddr::V4(_) => IpAddr::V4(Ipv4Addr::UNSPECIFIED),
            IpAddr::V6(_) => IpAddr::V6(Ipv6Addr::UNSPECIFIED),
        };
        let control_socket = UdpSocket::bind(SocketAddr::new(bind_ip, 0))?;
        let local_control_port = control_socket.local_addr()?.port();
        let audio_key = self
            .channel
            .keys
            .shared_secret
            .get(..32)
            .ok_or(CoreError::Protocol("音频共享密钥长度不足"))?;
        let audio_key_array: [u8; 32] = audio_key
            .try_into()
            .map_err(|_| CoreError::Protocol("音频共享密钥长度错误"))?;
        let body = encode_stream_setup(
            local_control_port,
            self.session_id,
            audio_key,
            LATENCY_MIN,
            LATENCY_MAX,
        )?;
        let uri = self.session_uri.clone();
        let response = self.channel.send_request(
            "SETUP",
            &uri,
            Some("application/x-apple-binary-plist"),
            &body,
        )?;
        require_success(&response, "stream SETUP")?;
        let value = parse_binary_plist(&response.body, "stream SETUP")?;
        let stream = value
            .as_dictionary()
            .and_then(|root| root.get("streams"))
            .and_then(plist::Value::as_array)
            .and_then(|streams| streams.first())
            .and_then(plist::Value::as_dictionary)
            .ok_or(CoreError::Protocol("stream SETUP 响应缺少 streams"))?;
        let data_port = stream
            .get("dataPort")
            .and_then(plist_number)
            .and_then(|value| u16::try_from(value).ok())
            .filter(|value| *value != 0)
            .ok_or(CoreError::Protocol("stream SETUP 缺少有效的 dataPort"))?;
        let receiver_control_port = stream
            .get("controlPort")
            .and_then(plist_number)
            .and_then(|value| u16::try_from(value).ok())
            .filter(|value| *value != 0)
            .unwrap_or(data_port);
        let receiver_ip = self.channel.stream.peer_addr()?.ip();
        let audio_socket = UdpSocket::bind(SocketAddr::new(bind_ip, 0))?;
        audio_socket.connect(SocketAddr::new(receiver_ip, data_port))?;
        control_socket.connect(SocketAddr::new(receiver_ip, receiver_control_port))?;
        control_socket.set_nonblocking(true)?;
        self.control_socket = Some(control_socket);
        self.audio_socket = Some(audio_socket);
        self.data_port = Some(data_port);
        self.receiver_control_port = Some(receiver_control_port);
        let random = random_u32()?;
        self.rtp_clock = Some(RtpClock::new(
            random as u16,
            random.rotate_left(13),
            if self.ptp_master.is_some() {
                0
            } else {
                self.session_id
            },
        ));
        self.audio_encryptor = Some(AudioEncryptor::new(audio_key_array));
        Ok(StreamSetupInfo {
            data_port,
            control_port: receiver_control_port,
            local_control_port,
            latency_min_samples: LATENCY_MIN,
            latency_max_samples: LATENCY_MAX,
            receiver_audio_latency_samples: self.audio_latency_samples,
        })
    }

    /// 编码并发送恰好 352 帧双声道 PCM。
    pub fn send_audio_packet(&mut self, samples: &[i16]) -> Result<usize> {
        let alac = crate::audio::alac::encode_uncompressed_stereo(samples)?;
        let clock = self
            .rtp_clock
            .as_mut()
            .ok_or(CoreError::InvalidState("音频流尚未建立"))?;
        let encryptor = self
            .audio_encryptor
            .as_mut()
            .ok_or(CoreError::InvalidState("音频加密器尚未建立"))?;
        let packet = clock.encrypted_packet(&alac, self.first_audio_packet, encryptor)?;
        let sent = self
            .audio_socket
            .as_ref()
            .ok_or(CoreError::InvalidState("RTP socket 尚未建立"))?
            .send(&packet)?;
        if sent != packet.len() {
            return Err(CoreError::Protocol("RTP 音频包未完整发送"));
        }
        self.first_audio_packet = false;
        let sequence = u16::from_be_bytes([packet[2], packet[3]]);
        if self.retransmit_backlog.len() == 1000 {
            self.retransmit_backlog.pop_front();
        }
        self.retransmit_backlog.push_back((sequence, packet));
        Ok(sent)
    }

    /// 处理 HomePod 的 RTP 丢包重传请求；没有请求时立即返回。
    pub fn poll_retransmit_requests(&mut self) -> Result<usize> {
        let socket = self
            .control_socket
            .as_ref()
            .ok_or(CoreError::InvalidState("控制 UDP socket 尚未建立"))?;
        let audio = self
            .audio_socket
            .as_ref()
            .ok_or(CoreError::InvalidState("RTP socket 尚未建立"))?;
        let mut resent = 0usize;
        loop {
            let mut request = [0_u8; 8];
            match socket.recv(&mut request) {
                Ok(8) => {
                    let Some((start, count)) = parse_retransmit_request(&request)? else {
                        continue;
                    };
                    for offset in 0..count {
                        let wanted = start.wrapping_add(offset);
                        if let Some((_, packet)) = self
                            .retransmit_backlog
                            .iter()
                            .find(|(sequence, _)| *sequence == wanted)
                            && audio.send(packet)? == packet.len()
                        {
                            resent += 1;
                        }
                    }
                }
                Ok(_) => return Err(CoreError::Protocol("RTP 重传请求长度错误")),
                Err(error)
                    if matches!(
                        error.kind(),
                        std::io::ErrorKind::WouldBlock | std::io::ErrorKind::TimedOut
                    ) =>
                {
                    return Ok(resent);
                }
                Err(error) => return Err(error.into()),
            }
        }
    }

    /// 发送播放时间映射。首包前必须发送带启动标记的同步包，之后约每秒一次。
    pub fn send_playback_sync(&self, initial: bool) -> Result<usize> {
        let clock_id = self
            .ptp_master
            .as_ref()
            .ok_or(CoreError::InvalidState("当前会话没有 PTP 主时钟"))?
            .clock_id();
        let timestamp = self
            .rtp_clock
            .as_ref()
            .ok_or(CoreError::InvalidState("音频流尚未建立"))?
            .timestamp();
        let packet = ptp_sync_packet(timestamp, clock_id, initial);
        let sent = self
            .control_socket
            .as_ref()
            .ok_or(CoreError::InvalidState("控制 UDP socket 尚未建立"))?
            .send(&packet)?;
        if sent != packet.len() {
            return Err(CoreError::Protocol("播放同步包未完整发送"));
        }
        Ok(sent)
    }

    /// 发送 AirPlay 协议原始 dB 音量，不做 UI 百分比换算。
    pub fn set_volume(&mut self, volume_db: f32) -> Result<()> {
        if !volume_db.is_finite() || !(-144.0..=0.0).contains(&volume_db) {
            return Err(CoreError::InvalidArgument);
        }
        let body = format!("volume: {volume_db:.6}\r\n");
        let uri = self.session_uri.clone();
        let response = self.channel.send_request(
            "SET_PARAMETER",
            &uri,
            Some("text/parameters"),
            body.as_bytes(),
        )?;
        require_success(&response, "SET_PARAMETER volume")
    }

    /// HomePod/Apple TV 的兼容性保活；每 25 秒发送一次即可。
    pub fn feedback(&mut self) -> Result<()> {
        let response = self.channel.send_request("POST", "/feedback", None, &[])?;
        require_success(&response, "POST /feedback")
    }

    pub fn teardown(&mut self) -> Result<()> {
        let mut body = Vec::new();
        plist::Value::Dictionary(plist::Dictionary::new())
            .to_writer_binary(&mut body)
            .map_err(|_| CoreError::Protocol("TEARDOWN plist 编码失败"))?;
        let uri = self.session_uri.clone();
        let response = self.channel.send_request(
            "TEARDOWN",
            &uri,
            Some("application/x-apple-binary-plist"),
            &body,
        )?;
        require_success(&response, "TEARDOWN")
    }
}

fn parse_retransmit_request(request: &[u8; 8]) -> Result<Option<(u16, u16)>> {
    if request[0] != 0x80 || request[1] != 0xd5 {
        return Ok(None);
    }
    let start = u16::from_be_bytes([request[4], request[5]]);
    let count = u16::from_be_bytes([request[6], request[7]]);
    if count == 0 || count > 1000 {
        return Err(CoreError::Protocol("RTP 重传数量错误"));
    }
    Ok(Some((start, count)))
}

fn encode_stream_setup(
    control_port: u16,
    session_id: u32,
    audio_key: &[u8],
    latency_min: u64,
    latency_max: u64,
) -> Result<Vec<u8>> {
    if audio_key.len() != 32 {
        return Err(CoreError::Protocol("音频共享密钥必须为 32 字节"));
    }
    let mut stream = plist::Dictionary::new();
    stream.insert(
        "audioFormat".into(),
        plist::Value::Integer(0x40000_u64.into()),
    );
    stream.insert("audioMode".into(), plist::Value::String("default".into()));
    stream.insert(
        "controlPort".into(),
        plist::Value::Integer((control_port as u64).into()),
    );
    stream.insert("ct".into(), plist::Value::Integer(2_u64.into()));
    stream.insert("isMedia".into(), plist::Value::Boolean(true));
    stream.insert(
        "latencyMax".into(),
        plist::Value::Integer(latency_max.into()),
    );
    stream.insert(
        "latencyMin".into(),
        plist::Value::Integer(latency_min.into()),
    );
    stream.insert("shk".into(), plist::Value::Data(audio_key.to_vec()));
    stream.insert("spf".into(), plist::Value::Integer(352_u64.into()));
    stream.insert("sr".into(), plist::Value::Integer(44_100_u64.into()));
    stream.insert("type".into(), plist::Value::Integer(0x60_u64.into()));
    stream.insert(
        "supportsDynamicStreamID".into(),
        plist::Value::Boolean(false),
    );
    stream.insert(
        "streamConnectionID".into(),
        plist::Value::Integer((session_id as u64).into()),
    );
    let mut root = plist::Dictionary::new();
    root.insert(
        "streams".into(),
        plist::Value::Array(vec![plist::Value::Dictionary(stream)]),
    );
    let mut body = Vec::new();
    plist::Value::Dictionary(root)
        .to_writer_binary(&mut body)
        .map_err(|_| CoreError::Protocol("stream SETUP plist 编码失败"))?;
    Ok(body)
}

fn encode_session_setup_ntp(
    sender_id: &str,
    session_uuid: &str,
    timing_port: u16,
) -> Result<Vec<u8>> {
    // NTP 会话只发送接收端实际需要的四个字段。
    // HomePod 遇到互相矛盾的发送端描述时可能直接不响应，而不是返回错误码。
    let mut root = plist::Dictionary::new();
    root.insert("deviceID".into(), plist::Value::String(sender_id.into()));
    root.insert(
        "sessionUUID".into(),
        plist::Value::String(session_uuid.into()),
    );
    root.insert(
        "timingPort".into(),
        plist::Value::Integer((timing_port as u64).into()),
    );
    root.insert("timingProtocol".into(), plist::Value::String("NTP".into()));
    let mut body = Vec::new();
    plist::Value::Dictionary(root)
        .to_writer_binary(&mut body)
        .map_err(|_| CoreError::Protocol("session SETUP plist 编码失败"))?;
    Ok(body)
}

fn encode_session_setup_ptp(
    sender_id: &str,
    session_uuid: &str,
    local_ip: IpAddr,
    clock_id: u64,
    clock_uuid: &str,
) -> Result<Vec<u8>> {
    let group_uuid = random_uuid()?;
    let addresses = plist::Value::Array(vec![plist::Value::String(local_ip.to_string())]);

    let mut peer = plist::Dictionary::new();
    peer.insert("ID".into(), plist::Value::String(clock_uuid.into()));
    peer.insert("DeviceType".into(), plist::Value::Integer(0_u64.into()));
    peer.insert(
        "ClockID".into(),
        plist::Value::Integer((clock_id as i64).into()),
    );
    peer.insert(
        "SupportsClockPortMatchingOverride".into(),
        plist::Value::Boolean(false),
    );
    peer.insert("Addresses".into(), addresses);

    let mut root = plist::Dictionary::new();
    root.insert("name".into(), plist::Value::String("AirBlade".into()));
    root.insert("deviceID".into(), plist::Value::String(sender_id.into()));
    root.insert(
        "sessionUUID".into(),
        plist::Value::String(session_uuid.into()),
    );
    root.insert("timingProtocol".into(), plist::Value::String("PTP".into()));
    root.insert("macAddress".into(), plist::Value::String(sender_id.into()));
    root.insert("groupUUID".into(), plist::Value::String(group_uuid));
    root.insert(
        "groupContainsGroupLeader".into(),
        plist::Value::Boolean(false),
    );
    root.insert(
        "timingPeerInfo".into(),
        plist::Value::Dictionary(peer.clone()),
    );
    root.insert(
        "timingPeerList".into(),
        plist::Value::Array(vec![plist::Value::Dictionary(peer)]),
    );
    let mut body = Vec::new();
    plist::Value::Dictionary(root)
        .to_writer_binary(&mut body)
        .map_err(|_| CoreError::Protocol("PTP session SETUP plist 编码失败"))?;
    Ok(body)
}

fn random_u32() -> Result<u32> {
    let mut bytes = [0_u8; 4];
    getrandom::getrandom(&mut bytes).map_err(|_| CoreError::Protocol("无法生成会话随机数"))?;
    Ok(u32::from_le_bytes(bytes).max(1))
}

fn random_u64() -> Result<u64> {
    let mut bytes = [0_u8; 8];
    getrandom::getrandom(&mut bytes).map_err(|_| CoreError::Protocol("无法生成 PTP 时钟标识"))?;
    Ok(u64::from_le_bytes(bytes))
}

fn random_uuid() -> Result<String> {
    let mut bytes = [0_u8; 16];
    getrandom::getrandom(&mut bytes).map_err(|_| CoreError::Protocol("无法生成会话 UUID"))?;
    bytes[6] = (bytes[6] & 0x0f) | 0x40;
    bytes[8] = (bytes[8] & 0x3f) | 0x80;
    Ok(format!(
        "{:02X}{:02X}{:02X}{:02X}-{:02X}{:02X}-{:02X}{:02X}-{:02X}{:02X}-{:02X}{:02X}{:02X}{:02X}{:02X}{:02X}",
        bytes[0],
        bytes[1],
        bytes[2],
        bytes[3],
        bytes[4],
        bytes[5],
        bytes[6],
        bytes[7],
        bytes[8],
        bytes[9],
        bytes[10],
        bytes[11],
        bytes[12],
        bytes[13],
        bytes[14],
        bytes[15]
    ))
}

fn random_sender_id() -> Result<String> {
    let mut bytes = [0_u8; 6];
    getrandom::getrandom(&mut bytes).map_err(|_| CoreError::Protocol("无法生成发送端标识"))?;
    bytes[0] = (bytes[0] | 0x02) & 0xfe;
    Ok(bytes
        .iter()
        .map(|value| format!("{value:02X}"))
        .collect::<Vec<_>>()
        .join(":"))
}

fn require_success(response: &RtspMessage, operation: &'static str) -> Result<()> {
    let status = response
        .start_line
        .split_whitespace()
        .nth(1)
        .and_then(|value| value.parse::<u16>().ok())
        .ok_or(CoreError::Protocol("RTSP 响应缺少状态码"))?;
    if status != 200 {
        return Err(CoreError::RtspStatus(operation, status));
    }
    Ok(())
}

fn parse_airplay_info(body: &[u8]) -> Result<AirPlayInfo> {
    let value = parse_binary_plist(body, "GET /info")?;
    let root = value
        .as_dictionary()
        .ok_or(CoreError::Protocol("GET /info 的根节点不是字典"))?;
    let text = |key: &str| {
        root.get(key)
            .and_then(plist::Value::as_string)
            .map(str::to_owned)
    };
    let number = |key: &str| root.get(key).and_then(plist_number);
    Ok(AirPlayInfo {
        name: text("name"),
        model: text("model"),
        device_id: text("deviceID"),
        features: number("features"),
        status_flags: number("statusFlags"),
        latency_min: find_number(&value, "latencyMin"),
        body_bytes: body.len(),
    })
}

fn parse_binary_plist(body: &[u8], operation: &'static str) -> Result<plist::Value> {
    if !body.starts_with(b"bplist00") {
        return Err(CoreError::Protocol("接收端返回的不是 binary plist"));
    }
    plist::Value::from_reader(std::io::Cursor::new(body))
        .map_err(|_| CoreError::RtspBody(operation))
}

fn plist_number(value: &plist::Value) -> Option<u64> {
    value.as_unsigned_integer().or_else(|| {
        value
            .as_signed_integer()
            .and_then(|number| number.try_into().ok())
    })
}

fn find_number(value: &plist::Value, wanted_key: &str) -> Option<u64> {
    match value {
        plist::Value::Dictionary(values) => {
            if let Some(number) = values.get(wanted_key).and_then(plist_number) {
                return Some(number);
            }
            values
                .values()
                .find_map(|child| find_number(child, wanted_key))
        }
        plist::Value::Array(values) => values
            .iter()
            .find_map(|child| find_number(child, wanted_key)),
        _ => None,
    }
}

fn connect(address: SocketAddr) -> Result<TcpStream> {
    let stream = TcpStream::connect_timeout(&address, Duration::from_secs(5))?;
    stream.set_read_timeout(Some(Duration::from_secs(5)))?;
    stream.set_write_timeout(Some(Duration::from_secs(5)))?;
    Ok(stream)
}

fn begin_transient_pairing_on(stream: &mut TcpStream) -> Result<PairSetupM2> {
    let mut m1 = Tlv8::default();
    m1.insert(TYPE_METHOD, [0x00]);
    m1.insert(TYPE_STATE, [0x01]);
    m1.insert(TYPE_FLAGS, [TRANSIENT_PAIRING_FLAG]);
    let response = post_tlv_on(stream, "/pair-setup", &m1.encode(), 0)?;
    let tlv = parse_pairing_response(response, 0x02)?;
    let salt = tlv
        .get(TYPE_SALT)
        .ok_or(CoreError::Protocol("M2 缺少盐值"))?
        .to_vec();
    let server_public_key = tlv
        .get(TYPE_PUBLIC_KEY)
        .ok_or(CoreError::Protocol("M2 缺少服务器公钥"))?
        .to_vec();
    if salt.is_empty() || server_public_key.is_empty() {
        return Err(CoreError::Protocol("M2 配对参数为空"));
    }
    Ok(PairSetupM2 {
        salt,
        server_public_key,
    })
}

fn parse_pairing_response(
    response: crate::protocol::RtspMessage,
    expected_state: u8,
) -> Result<Tlv8> {
    let status = response
        .start_line
        .split_whitespace()
        .nth(1)
        .ok_or(CoreError::Protocol("配对响应缺少状态码"))?
        .parse::<u16>()
        .map_err(|_| CoreError::Protocol("配对响应状态码错误"))?;
    if status != 200 {
        return Err(CoreError::PairingStatus(status));
    }
    let tlv = Tlv8::decode(&response.body)?;
    if let Some(error) = tlv.get(TYPE_ERROR) {
        return Err(CoreError::PairingTlv(error.first().copied().unwrap_or(0)));
    }
    if tlv.state()? != expected_state {
        return Err(CoreError::Protocol("配对响应状态不符合预期"));
    }
    Ok(tlv)
}

fn post_tlv_on(
    stream: &mut TcpStream,
    path: &str,
    body: &[u8],
    cseq: u32,
) -> Result<crate::protocol::RtspMessage> {
    // 配对请求复用 AirPlay 控制连接常见的身份头；部分 HomePod 会拒绝只带 HTTP 基础头的请求。
    let request = format!(
        "POST {path} HTTP/1.1\r\nCSeq: {cseq}\r\nUser-Agent: AirPlay/550.10\r\nConnection: keep-alive\r\nDACP-ID: 6A1B2C3D4E5F6071\r\nActive-Remote: 123456789\r\nClient-Instance: 6A1B2C3D4E5F6071\r\nX-Apple-Client-Name: AirBlade\r\nX-Apple-HKP: 4\r\nContent-Type: application/octet-stream\r\nContent-Length: {}\r\n\r\n",
        body.len()
    );
    stream.write_all(request.as_bytes())?;
    stream.write_all(body)?;
    stream.flush()?;
    read_response(stream)
}

fn read_response(stream: &mut TcpStream) -> Result<crate::protocol::RtspMessage> {
    const MAX_RESPONSE: usize = 70 * 1024;
    let mut buffer = Vec::new();
    loop {
        let mut chunk = [0_u8; 4096];
        let read = stream.read(&mut chunk)?;
        if read == 0 {
            return parse_rtsp(&buffer);
        }
        buffer.extend_from_slice(&chunk[..read]);
        if buffer.len() > MAX_RESPONSE {
            return Err(CoreError::Protocol("配对响应过大"));
        }
        if let Some(end) = buffer.windows(4).position(|v| v == b"\r\n\r\n") {
            let header = std::str::from_utf8(&buffer[..end])
                .map_err(|_| CoreError::Protocol("配对响应头编码错误"))?;
            let length = header
                .lines()
                .find_map(|line| {
                    line.split_once(':')
                        .filter(|(key, _)| key.eq_ignore_ascii_case("content-length"))
                        .and_then(|(_, value)| value.trim().parse::<usize>().ok())
                })
                .ok_or(CoreError::Protocol("配对响应缺少长度"))?;
            if buffer.len() >= end + 4 + length {
                return parse_rtsp(&buffer[..end + 4 + length]);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn m1_contains_transient_flag() {
        let mut m1 = Tlv8::default();
        m1.insert(TYPE_METHOD, [0]);
        m1.insert(TYPE_STATE, [1]);
        m1.insert(TYPE_FLAGS, [TRANSIENT_PAIRING_FLAG]);
        let decoded = Tlv8::decode(&m1.encode()).unwrap();
        assert_eq!(decoded.get(TYPE_FLAGS), Some(&[0x10][..]));
    }

    #[test]
    fn parses_binary_airplay_info_and_nested_latency() {
        let mut latency = plist::Dictionary::new();
        latency.insert("latencyMin".into(), plist::Value::Integer(250_u64.into()));
        let mut root = plist::Dictionary::new();
        root.insert("name".into(), plist::Value::String("卧室".into()));
        root.insert(
            "model".into(),
            plist::Value::String("AudioAccessory5,1".into()),
        );
        root.insert("features".into(), plist::Value::Integer(123_u64.into()));
        root.insert(
            "audioLatencies".into(),
            plist::Value::Array(vec![plist::Value::Dictionary(latency)]),
        );
        let mut encoded = Vec::new();
        plist::Value::Dictionary(root)
            .to_writer_binary(&mut encoded)
            .unwrap();
        let info = parse_airplay_info(&encoded).unwrap();
        assert_eq!(info.name.as_deref(), Some("卧室"));
        assert_eq!(info.model.as_deref(), Some("AudioAccessory5,1"));
        assert_eq!(info.features, Some(123));
        assert_eq!(info.latency_min, Some(250));
    }

    #[test]
    fn parses_retransmit_request_with_wrapping_sequence_range() {
        let request = [0x80, 0xd5, 0, 1, 0xff, 0xfe, 0, 3];
        assert_eq!(
            parse_retransmit_request(&request).unwrap(),
            Some((65534, 3))
        );
        let invalid = [0x80, 0xd5, 0, 1, 0, 1, 0, 0];
        assert!(parse_retransmit_request(&invalid).is_err());
    }
}
