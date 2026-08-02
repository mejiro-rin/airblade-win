//! RTP 音频包序列化。
//! 后续加密音频负载时，RTP 头部将作为认证附加数据，不能被中途篡改。
use crate::error::{CoreError, Result};
use chacha20poly1305::{
    ChaCha20Poly1305, Nonce,
    aead::{AeadInPlace, KeyInit},
};
use std::time::{SystemTime, UNIX_EPOCH};

pub const RTP_HEADER_LEN: usize = 12;

#[derive(Debug, Clone, Copy)]
pub struct RtpClock {
    sequence: u16,
    timestamp: u32,
    ssrc: u32,
}

pub struct AudioEncryptor {
    cipher: ChaCha20Poly1305,
}

impl AudioEncryptor {
    pub fn new(key: [u8; 32]) -> Self {
        Self {
            cipher: ChaCha20Poly1305::new((&key).into()),
        }
    }

    /// 加密 ALAC 负载。RTP 时间戳和 SSRC 作为认证数据，8 字节小端序列号附在包尾。
    pub fn encrypt(
        &self,
        header: &[u8; RTP_HEADER_LEN],
        payload: &[u8],
        sequence: u16,
    ) -> Result<Vec<u8>> {
        let nonce8 = (sequence as u64).to_le_bytes();
        let mut nonce12 = [0_u8; 12];
        nonce12[4..].copy_from_slice(&nonce8);
        let mut encrypted = payload.to_vec();
        let tag = self
            .cipher
            .encrypt_in_place_detached(Nonce::from_slice(&nonce12), &header[4..12], &mut encrypted)
            .map_err(|_| CoreError::Authentication)?;
        encrypted.extend_from_slice(&tag);
        encrypted.extend_from_slice(&nonce8);
        Ok(encrypted)
    }
}

impl RtpClock {
    pub fn new(sequence: u16, timestamp: u32, ssrc: u32) -> Self {
        Self {
            sequence,
            timestamp,
            ssrc,
        }
    }
    pub fn packet(&mut self, payload: &[u8], marker: bool) -> Result<Vec<u8>> {
        if payload.is_empty() {
            return Err(CoreError::InvalidArgument);
        }
        let mut packet = Vec::with_capacity(RTP_HEADER_LEN + payload.len());
        packet.push(0x80);
        packet.push((if marker { 0x80 } else { 0 }) | 0x60);
        packet.extend_from_slice(&self.sequence.to_be_bytes());
        packet.extend_from_slice(&self.timestamp.to_be_bytes());
        packet.extend_from_slice(&self.ssrc.to_be_bytes());
        packet.extend_from_slice(payload);
        self.sequence = self.sequence.wrapping_add(1);
        self.timestamp = self.timestamp.wrapping_add(352);
        Ok(packet)
    }

    pub fn encrypted_packet(
        &mut self,
        payload: &[u8],
        marker: bool,
        encryptor: &AudioEncryptor,
    ) -> Result<Vec<u8>> {
        if payload.is_empty() {
            return Err(CoreError::InvalidArgument);
        }
        let sequence = self.sequence;
        let mut header = [0_u8; RTP_HEADER_LEN];
        header[0] = 0x80;
        header[1] = (if marker { 0x80 } else { 0 }) | 0x60;
        header[2..4].copy_from_slice(&sequence.to_be_bytes());
        header[4..8].copy_from_slice(&self.timestamp.to_be_bytes());
        header[8..12].copy_from_slice(&self.ssrc.to_be_bytes());
        let encrypted = encryptor.encrypt(&header, payload, sequence)?;
        let mut packet = Vec::with_capacity(RTP_HEADER_LEN + encrypted.len());
        packet.extend_from_slice(&header);
        packet.extend_from_slice(&encrypted);
        self.sequence = self.sequence.wrapping_add(1);
        self.timestamp = self.timestamp.wrapping_add(352);
        Ok(packet)
    }

    pub fn timestamp(&self) -> u32 {
        self.timestamp
    }
}

/// 构造 AirPlay 2 PTP 模式的 28 字节播放同步包。
pub fn ptp_sync_packet(rtp_timestamp: u32, clock_id: u64, initial: bool) -> [u8; 28] {
    let mut packet = [0_u8; 28];
    packet[0] = if initial { 0x90 } else { 0x80 };
    packet[1] = 0xd7;
    packet[2..4].copy_from_slice(&6_u16.to_be_bytes());
    packet[4..8].copy_from_slice(&rtp_timestamp.to_be_bytes());
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default();
    let nanos = now
        .as_secs()
        .saturating_mul(1_000_000_000)
        .saturating_add(now.subsec_nanos() as u64);
    packet[8..16].copy_from_slice(&nanos.to_be_bytes());
    let first_playable = rtp_timestamp.wrapping_sub(11_025);
    packet[16..20].copy_from_slice(&first_playable.to_be_bytes());
    packet[20..28].copy_from_slice(&clock_id.to_be_bytes());
    packet
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rtp_clock_advances_per_alac_frame() {
        let mut c = RtpClock::new(1, 10, 99);
        let a = c.packet(&[1], false).unwrap();
        let b = c.packet(&[2], true).unwrap();
        assert_eq!(&a[2..4], &[0, 1]);
        assert_eq!(&b[2..4], &[0, 2]);
        assert_eq!(u32::from_be_bytes(b[4..8].try_into().unwrap()), 362);
        assert_eq!(b[1], 0xe0);
    }

    #[test]
    fn encrypted_audio_appends_tag_and_little_endian_nonce() {
        let mut clock = RtpClock::new(0x1234, 10, 99);
        let encryptor = AudioEncryptor::new([7; 32]);
        let packet = clock
            .encrypted_packet(&[1, 2, 3], true, &encryptor)
            .unwrap();
        assert_eq!(packet.len(), RTP_HEADER_LEN + 3 + 16 + 8);
        assert_eq!(&packet[packet.len() - 8..], &[0x34, 0x12, 0, 0, 0, 0, 0, 0]);
        assert_eq!(packet[1], 0xe0);
    }

    #[test]
    fn ptp_sync_uses_clock_id_and_latency_offset() {
        let packet = ptp_sync_packet(20_000, 0x1122, true);
        assert_eq!(&packet[..4], &[0x90, 0xd7, 0, 6]);
        assert_eq!(u32::from_be_bytes(packet[4..8].try_into().unwrap()), 20_000);
        assert_eq!(
            u32::from_be_bytes(packet[16..20].try_into().unwrap()),
            8_975
        );
        assert_eq!(&packet[20..28], &0x1122_u64.to_be_bytes());
    }
}
