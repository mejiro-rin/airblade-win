use super::processing::{AIRPLAY_CHANNELS, ALAC_FRAMES_PER_PACKET};
use crate::error::{CoreError, Result};

/// 编码 AirPlay 实时流规定的“未压缩 ALAC”双声道帧。
/// 它是固定 352 帧的 ALAC 比特流，不是把 PCM 数据直接伪装成 ALAC。
pub fn encode_uncompressed_stereo(samples: &[i16]) -> Result<Vec<u8>> {
    if samples.len() != ALAC_FRAMES_PER_PACKET * AIRPLAY_CHANNELS {
        return Err(CoreError::InvalidArgument);
    }
    let mut bits = BitWriter::default();
    bits.push(3, 1); // 双声道 CPE 元素
    bits.push(4, 0);
    bits.push(12, 0);
    bits.push(1, 0);
    bits.push(2, 0);
    bits.push(1, 1); // 明确标记为未压缩 ALAC 帧
    for pair in samples.chunks_exact(2) {
        bits.push(16, pair[0] as u16 as u32);
        bits.push(16, pair[1] as u16 as u32);
    }
    bits.push(3, 7); // 帧结束标记
    Ok(bits.finish())
}

#[derive(Default)]
struct BitWriter {
    data: Vec<u8>,
    used: u8,
}
impl BitWriter {
    fn push(&mut self, width: u8, value: u32) {
        for shift in (0..width).rev() {
            if self.used == 0 {
                self.data.push(0);
            }
            let bit = ((value >> shift) & 1) as u8;
            let last = self.data.len() - 1;
            self.data[last] |= bit << (7 - self.used);
            self.used = (self.used + 1) % 8;
        }
    }
    fn finish(mut self) -> Vec<u8> {
        if self.used != 0 {
            self.used = 0;
        }
        self.data
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fixed_packet_encodes() {
        let frame = encode_uncompressed_stereo(&vec![0; 704]).unwrap();
        assert_eq!(frame.len(), 1412);
        assert_eq!(frame[0] >> 5, 1);
    }
}
