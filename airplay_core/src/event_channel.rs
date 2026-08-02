//! AirPlay 2 反向事件通道。
//! HomePod 会主动发送控制请求；发送端必须解密、回复 200，并把音量变化交给上层。

use std::{
    io::{ErrorKind, Read, Write},
    net::{SocketAddr, TcpStream},
    time::Duration,
};

use crate::{
    error::{CoreError, Result},
    protocol::{
        EncryptedFramer, MAX_CONTROL_FRAME, RtspMessage, hkdf_sha512, parse_rtsp, rtsp_message_len,
    },
};

#[derive(Debug, Clone, PartialEq)]
pub enum RemoteEvent {
    Volume(f32),
    Request(String),
}

pub struct EventChannel {
    stream: TcpStream,
    writer: EncryptedFramer,
    reader: EncryptedFramer,
    encrypted_input: Vec<u8>,
    plaintext_input: Vec<u8>,
}

impl EventChannel {
    pub fn connect(receiver: SocketAddr, shared_secret: &[u8]) -> Result<Self> {
        let stream = TcpStream::connect_timeout(&receiver, Duration::from_secs(5))?;
        stream.set_nonblocking(true)?;
        stream.set_write_timeout(Some(Duration::from_secs(5)))?;

        // 事件通道是反向连接：接收 HomePod 写出的事件用 Events-Write，
        // 回复 HomePod 读取的响应用 Events-Read。
        let read_key: [u8; 32] = hkdf_sha512(
            shared_secret,
            b"Events-Salt",
            b"Events-Write-Encryption-Key",
            32,
        )?
        .try_into()
        .map_err(|_| CoreError::Protocol("事件读取密钥长度错误"))?;
        let write_key: [u8; 32] = hkdf_sha512(
            shared_secret,
            b"Events-Salt",
            b"Events-Read-Encryption-Key",
            32,
        )?
        .try_into()
        .map_err(|_| CoreError::Protocol("事件写入密钥长度错误"))?;
        Ok(Self {
            stream,
            writer: EncryptedFramer::new(write_key),
            reader: EncryptedFramer::new(read_key),
            encrypted_input: Vec::new(),
            plaintext_input: Vec::new(),
        })
    }

    /// 非阻塞处理当前已经到达的全部事件。
    pub fn poll(&mut self) -> Result<Vec<RemoteEvent>> {
        let mut network = [0_u8; 4096];
        match self.stream.read(&mut network) {
            Ok(0) => return Err(CoreError::Protocol("HomePod 关闭了事件通道")),
            Ok(read) => self.encrypted_input.extend_from_slice(&network[..read]),
            Err(error) if matches!(error.kind(), ErrorKind::WouldBlock | ErrorKind::TimedOut) => {}
            Err(error) => return Err(error.into()),
        }
        self.decrypt_frames()?;

        let mut events = Vec::new();
        while let Some(message_len) = rtsp_message_len(&self.plaintext_input)? {
            let bytes: Vec<u8> = self.plaintext_input.drain(..message_len).collect();
            let message = parse_rtsp(&bytes)?;
            if let Some(volume) = parse_volume(&message)? {
                events.push(RemoteEvent::Volume(volume));
            } else {
                events.push(RemoteEvent::Request(message.start_line.clone()));
            }
            self.reply_ok(&message)?;
        }
        Ok(events)
    }

    fn decrypt_frames(&mut self) -> Result<()> {
        loop {
            if self.encrypted_input.len() < 2 {
                return Ok(());
            }
            let length =
                u16::from_le_bytes([self.encrypted_input[0], self.encrypted_input[1]]) as usize;
            if length > MAX_CONTROL_FRAME {
                return Err(CoreError::Protocol("事件加密帧长度错误"));
            }
            let frame_len = 2 + length + 16;
            if self.encrypted_input.len() < frame_len {
                return Ok(());
            }
            let frame: Vec<u8> = self.encrypted_input.drain(..frame_len).collect();
            self.plaintext_input.extend(self.reader.open(&frame)?);
        }
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
        for chunk in response.as_bytes().chunks(MAX_CONTROL_FRAME) {
            self.stream.write_all(&self.writer.seal(chunk)?)?;
        }
        self.stream.flush()?;
        Ok(())
    }
}

pub(crate) fn parse_volume(message: &RtspMessage) -> Result<Option<f32>> {
    if !message.start_line.starts_with("SET_PARAMETER ") {
        return Ok(None);
    }
    let body = std::str::from_utf8(&message.body)
        .map_err(|_| CoreError::Protocol("音量事件正文不是 UTF-8"))?;
    for line in body.lines() {
        let Some((name, value)) = line.split_once([':', '=']) else {
            continue;
        };
        if !name.trim().eq_ignore_ascii_case("volume") {
            continue;
        }
        let volume = value
            .trim()
            .parse::<f32>()
            .map_err(|_| CoreError::Protocol("音量事件数值错误"))?;
        if !volume.is_finite() || !(-144.0..=0.0).contains(&volume) {
            return Err(CoreError::Protocol("音量事件超出 AirPlay 范围"));
        }
        return Ok(Some(volume));
    }
    Ok(None)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{
        net::TcpListener,
        thread,
        time::{Duration, Instant},
    };

    #[test]
    fn parses_receiver_volume_event() {
        let message = parse_rtsp(
            b"SET_PARAMETER rtsp://sender/1 RTSP/1.0\r\nCSeq: 4\r\nContent-Length: 17\r\n\r\nvolume: -18.500\r\n",
        )
        .unwrap();
        assert_eq!(parse_volume(&message).unwrap(), Some(-18.5));
    }

    #[test]
    fn rejects_invalid_receiver_volume() {
        let message = RtspMessage {
            start_line: "SET_PARAMETER / RTSP/1.0".into(),
            headers: Vec::new(),
            body: b"volume: 5\r\n".to_vec(),
        };
        assert!(parse_volume(&message).is_err());
    }

    #[test]
    fn encrypted_channel_receives_volume_and_replies_ok() {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let receiver = listener.local_addr().unwrap();
        let shared_secret = [0x5a; 32];

        let receiver_thread = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream
                .set_read_timeout(Some(Duration::from_secs(2)))
                .unwrap();

            // 测试端扮演 HomePod：写事件使用 Events-Write，读回复使用 Events-Read。
            let event_write_key: [u8; 32] = hkdf_sha512(
                &shared_secret,
                b"Events-Salt",
                b"Events-Write-Encryption-Key",
                32,
            )
            .unwrap()
            .try_into()
            .unwrap();
            let event_read_key: [u8; 32] = hkdf_sha512(
                &shared_secret,
                b"Events-Salt",
                b"Events-Read-Encryption-Key",
                32,
            )
            .unwrap()
            .try_into()
            .unwrap();
            let mut writer = EncryptedFramer::new(event_write_key);
            let mut reader = EncryptedFramer::new(event_read_key);

            let request = b"SET_PARAMETER rtsp://sender/1 RTSP/1.0\r\nCSeq: 9\r\nContent-Length: 17\r\n\r\nvolume: -23.500\r\n";
            stream.write_all(&writer.seal(request).unwrap()).unwrap();

            let mut length_bytes = [0_u8; 2];
            stream.read_exact(&mut length_bytes).unwrap();
            let encrypted_length = u16::from_le_bytes(length_bytes) as usize;
            let mut frame = Vec::with_capacity(2 + encrypted_length + 16);
            frame.extend_from_slice(&length_bytes);
            frame.resize(2 + encrypted_length + 16, 0);
            stream.read_exact(&mut frame[2..]).unwrap();
            let response = reader.open(&frame).unwrap();
            assert!(response.starts_with(b"RTSP/1.0 200 OK\r\n"));
            assert!(response.windows(9).any(|part| part == b"CSeq: 9\r\n"));
        });

        let mut channel = EventChannel::connect(receiver, &shared_secret).unwrap();
        let deadline = Instant::now() + Duration::from_secs(2);
        let received = loop {
            let events = channel.poll().unwrap();
            if !events.is_empty() {
                break events;
            }
            assert!(Instant::now() < deadline, "等待加密音量事件超时");
            thread::sleep(Duration::from_millis(5));
        };
        assert_eq!(received, vec![RemoteEvent::Volume(-23.5)]);
        receiver_thread.join().unwrap();
    }
}
