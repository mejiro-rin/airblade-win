//! AirPlay 加密控制通道的基础组件。
//! 所有输入均有长度上限，避免接收端异常报文造成无限分配。

use chacha20poly1305::{
    ChaCha20Poly1305, Nonce, Tag,
    aead::{AeadInPlace, KeyInit},
};
use hmac::{Hmac, Mac};
use sha2::Sha512;

use crate::error::{CoreError, Result};

pub const MAX_CONTROL_FRAME: usize = 1024;
const TAG_LEN: usize = 16;

/// 使用 HKDF-SHA512 从配对密钥派生彼此独立的控制通道和事件通道密钥。
pub fn hkdf_sha512(ikm: &[u8], salt: &[u8], info: &[u8], output_len: usize) -> Result<Vec<u8>> {
    if output_len == 0 || output_len > 255 * 64 {
        return Err(CoreError::InvalidArgument);
    }
    type HmacSha512 = Hmac<Sha512>;
    let effective_salt = if salt.is_empty() {
        &[0_u8; 64][..]
    } else {
        salt
    };
    let mut extract = <HmacSha512 as Mac>::new_from_slice(effective_salt)
        .map_err(|_| CoreError::InvalidArgument)?;
    extract.update(ikm);
    let prk = extract.finalize().into_bytes();
    let mut result = Vec::with_capacity(output_len);
    let mut previous = Vec::new();
    for counter in 1_u8..=255 {
        let mut expand =
            <HmacSha512 as Mac>::new_from_slice(&prk).map_err(|_| CoreError::InvalidArgument)?;
        expand.update(&previous);
        expand.update(info);
        expand.update(&[counter]);
        previous = expand.finalize().into_bytes().to_vec();
        result.extend_from_slice(&previous);
        if result.len() >= output_len {
            result.truncate(output_len);
            return Ok(result);
        }
    }
    Err(CoreError::InvalidArgument)
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RtspMessage {
    pub start_line: String,
    pub headers: Vec<(String, String)>,
    pub body: Vec<u8>,
}

impl RtspMessage {
    pub fn encode(&self) -> Result<Vec<u8>> {
        if self.start_line.is_empty() || self.start_line.contains(['\r', '\n']) {
            return Err(CoreError::Protocol("invalid start line"));
        }
        let mut result = self.start_line.as_bytes().to_vec();
        result.extend_from_slice(b"\r\n");
        for (name, value) in &self.headers {
            if name.is_empty() || name.contains(['\r', '\n', ':']) || value.contains(['\r', '\n']) {
                return Err(CoreError::Protocol("invalid header"));
            }
            result.extend_from_slice(name.as_bytes());
            result.extend_from_slice(b": ");
            result.extend_from_slice(value.as_bytes());
            result.extend_from_slice(b"\r\n");
        }
        if !self.body.is_empty()
            && !self
                .headers
                .iter()
                .any(|(k, _)| k.eq_ignore_ascii_case("content-length"))
        {
            result.extend_from_slice(format!("Content-Length: {}\r\n", self.body.len()).as_bytes());
        }
        result.extend_from_slice(b"\r\n");
        result.extend_from_slice(&self.body);
        Ok(result)
    }
}

pub fn parse_rtsp(input: &[u8]) -> Result<RtspMessage> {
    const MAX_HEADERS: usize = 64;
    const MAX_BODY: usize = 64 * 1024;
    let header_end = input
        .windows(4)
        .position(|v| v == b"\r\n\r\n")
        .ok_or(CoreError::Protocol("incomplete headers"))?;
    let header_text = std::str::from_utf8(&input[..header_end])
        .map_err(|_| CoreError::Protocol("headers not utf-8"))?;
    let mut lines = header_text.split("\r\n");
    let start_line = lines
        .next()
        .filter(|v| !v.is_empty())
        .ok_or(CoreError::Protocol("missing start line"))?
        .to_owned();
    let mut headers = Vec::new();
    let mut declared_len = 0usize;
    for line in lines {
        if headers.len() == MAX_HEADERS {
            return Err(CoreError::Protocol("too many headers"));
        }
        let (key, value) = line
            .split_once(':')
            .ok_or(CoreError::Protocol("malformed header"))?;
        if key.is_empty() {
            return Err(CoreError::Protocol("empty header"));
        }
        let value = value.trim().to_owned();
        if key.eq_ignore_ascii_case("content-length") {
            declared_len = value
                .parse()
                .map_err(|_| CoreError::Protocol("invalid content length"))?;
            if declared_len > MAX_BODY {
                return Err(CoreError::Protocol("body too large"));
            }
        }
        headers.push((key.to_owned(), value));
    }
    let body_start = header_end + 4;
    if input.len() != body_start + declared_len {
        return Err(CoreError::Protocol("truncated or trailing body"));
    }
    Ok(RtspMessage {
        start_line,
        headers,
        body: input[body_start..].to_vec(),
    })
}

/// 判断缓冲区里是否已经包含一条完整的 RTSP/HTTP 消息。
/// 返回完整消息的字节数；数据还没收齐时返回 `None`。
pub fn rtsp_message_len(input: &[u8]) -> Result<Option<usize>> {
    const MAX_HEADER_BYTES: usize = 16 * 1024;
    const MAX_BODY_BYTES: usize = 64 * 1024;

    let Some(header_end) = input.windows(4).position(|v| v == b"\r\n\r\n") else {
        if input.len() > MAX_HEADER_BYTES {
            return Err(CoreError::Protocol("RTSP 响应头过大"));
        }
        return Ok(None);
    };
    if header_end > MAX_HEADER_BYTES {
        return Err(CoreError::Protocol("RTSP 响应头过大"));
    }
    let header = std::str::from_utf8(&input[..header_end])
        .map_err(|_| CoreError::Protocol("RTSP 响应头不是 UTF-8"))?;
    let mut body_len = 0usize;
    for line in header.split("\r\n").skip(1) {
        let Some((name, value)) = line.split_once(':') else {
            return Err(CoreError::Protocol("RTSP 响应头格式错误"));
        };
        if name.eq_ignore_ascii_case("content-length") {
            body_len = value
                .trim()
                .parse::<usize>()
                .map_err(|_| CoreError::Protocol("RTSP 响应长度错误"))?;
            if body_len > MAX_BODY_BYTES {
                return Err(CoreError::Protocol("RTSP 响应正文过大"));
            }
        }
    }
    let total = header_end + 4 + body_len;
    Ok((input.len() >= total).then_some(total))
}

/// AirPlay 控制帧格式：2 字节小端密文长度 + 密文 + 16 字节认证标签。
pub struct EncryptedFramer {
    cipher: ChaCha20Poly1305,
    send_counter: u64,
    receive_counter: u64,
}

impl EncryptedFramer {
    pub fn new(key: [u8; 32]) -> Self {
        Self {
            cipher: ChaCha20Poly1305::new((&key).into()),
            send_counter: 0,
            receive_counter: 0,
        }
    }

    pub fn seal(&mut self, plaintext: &[u8]) -> Result<Vec<u8>> {
        if plaintext.len() > MAX_CONTROL_FRAME {
            return Err(CoreError::Protocol("control frame too large"));
        }
        let len = u16::try_from(plaintext.len())
            .map_err(|_| CoreError::Protocol("control frame too large"))?;
        let aad = len.to_le_bytes();
        let mut payload = plaintext.to_vec();
        let nonce = nonce(self.send_counter);
        let tag = self
            .cipher
            .encrypt_in_place_detached(&nonce, &aad, &mut payload)
            .map_err(|_| CoreError::Authentication)?;
        self.send_counter = self
            .send_counter
            .checked_add(1)
            .ok_or(CoreError::Protocol("nonce exhausted"))?;
        let mut frame = aad.to_vec();
        frame.append(&mut payload);
        frame.extend_from_slice(&tag);
        Ok(frame)
    }

    pub fn open(&mut self, frame: &[u8]) -> Result<Vec<u8>> {
        if frame.len() < 2 + TAG_LEN {
            return Err(CoreError::Protocol("short encrypted frame"));
        }
        let len = u16::from_le_bytes([frame[0], frame[1]]) as usize;
        if len > MAX_CONTROL_FRAME || frame.len() != 2 + len + TAG_LEN {
            return Err(CoreError::Protocol("invalid encrypted frame length"));
        }
        let mut payload = frame[2..2 + len].to_vec();
        let tag = Tag::from_slice(&frame[2 + len..]);
        let nonce = nonce(self.receive_counter);
        self.cipher
            .decrypt_in_place_detached(&nonce, &frame[..2], &mut payload, tag)
            .map_err(|_| CoreError::Authentication)?;
        self.receive_counter = self
            .receive_counter
            .checked_add(1)
            .ok_or(CoreError::Protocol("nonce exhausted"))?;
        Ok(payload)
    }
}

fn nonce(counter: u64) -> Nonce {
    let mut raw = [0_u8; 12];
    raw[4..].copy_from_slice(&counter.to_le_bytes());
    *Nonce::from_slice(&raw)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn encrypted_frames_authenticate() {
        let key = [7; 32];
        let mut a = EncryptedFramer::new(key);
        let mut b = EncryptedFramer::new(key);
        let f = a.seal(b"GET /info RTSP/1.0\r\n\r\n").unwrap();
        assert_eq!(b.open(&f).unwrap(), b"GET /info RTSP/1.0\r\n\r\n");
        let mut bad = f;
        bad[3] ^= 1;
        assert!(b.open(&bad).is_err());
    }
    #[test]
    fn rtsp_requires_exact_body() {
        let x = parse_rtsp(b"RTSP/1.0 200 OK\r\nContent-Length: 2\r\n\r\nok").unwrap();
        assert_eq!(x.body, b"ok");
        assert!(parse_rtsp(b"RTSP/1.0 200 OK\r\n\r\nmore").is_err());
    }
    #[test]
    fn rtsp_message_length_supports_incremental_reads() {
        let partial = b"RTSP/1.0 200 OK\r\nContent-Length: 4\r\n\r\nab";
        assert_eq!(rtsp_message_len(partial).unwrap(), None);
        let complete = b"RTSP/1.0 200 OK\r\nContent-Length: 4\r\n\r\nabcdmore";
        assert_eq!(rtsp_message_len(complete).unwrap(), Some(42));
    }
    #[test]
    fn hkdf_has_stable_length_and_domain_separation() {
        let a = hkdf_sha512(b"secret", b"Events-Salt", b"Events-Read-Encryption-Key", 32).unwrap();
        let b = hkdf_sha512(
            b"secret",
            b"Events-Salt",
            b"Events-Write-Encryption-Key",
            32,
        )
        .unwrap();
        assert_eq!(a.len(), 32);
        assert_ne!(a, b);
    }
}
