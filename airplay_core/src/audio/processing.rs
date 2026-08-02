use crate::error::{CoreError, Result};

pub const AIRPLAY_SAMPLE_RATE: u32 = 44_100;
pub const AIRPLAY_CHANNELS: usize = 2;
pub const ALAC_FRAMES_PER_PACKET: usize = 352;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SampleEncoding {
    Float,
    SignedPcm,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct InputPcmFormat {
    pub sample_rate: u32,
    pub channels: usize,
    pub block_align: usize,
    pub container_bits: u16,
    pub valid_bits: u16,
    pub encoding: SampleEncoding,
}

impl InputPcmFormat {
    pub fn validate(self) -> Result<Self> {
        let bytes_per_sample = usize::from(self.container_bits / 8);
        if self.sample_rate == 0
            || self.channels == 0
            || !self.container_bits.is_multiple_of(8)
            || bytes_per_sample == 0
            || self.block_align < self.channels * bytes_per_sample
            || self.valid_bits == 0
            || self.valid_bits > self.container_bits
            || (self.encoding == SampleEncoding::Float
                && (self.container_bits != 32 || self.valid_bits != 32))
            || (self.encoding == SampleEncoding::SignedPcm
                && !matches!(self.container_bits, 8 | 16 | 24 | 32))
        {
            return Err(CoreError::Protocol("不支持的 WASAPI 混音格式"));
        }
        Ok(self)
    }

    /// 按 WASAPI 的 block alignment 解码交错采样，避免把整数 PCM 误当成 f32。
    pub fn decode(self, bytes: &[u8]) -> Result<Vec<f32>> {
        let format = self.validate()?;
        if !bytes.len().is_multiple_of(format.block_align) {
            return Err(CoreError::Protocol("WASAPI 音频数据没有按帧对齐"));
        }
        let frames = bytes.len() / format.block_align;
        let sample_bytes = usize::from(format.container_bits / 8);
        let mut output = Vec::with_capacity(frames * format.channels);
        for frame in 0..frames {
            for channel in 0..format.channels {
                let offset = frame * format.block_align + channel * sample_bytes;
                let sample = &bytes[offset..offset + sample_bytes];
                output.push(decode_sample(format, sample));
            }
        }
        Ok(output)
    }
}

fn decode_sample(format: InputPcmFormat, bytes: &[u8]) -> f32 {
    if format.encoding == SampleEncoding::Float {
        return f32::from_le_bytes(bytes.try_into().expect("已验证为 32 位浮点")).clamp(-1.0, 1.0);
    }
    let raw = match format.container_bits {
        8 => (bytes[0] as i64) - 128,
        16 => i16::from_le_bytes(bytes.try_into().expect("已验证为 16 位 PCM")) as i64,
        24 => {
            let value = (bytes[0] as i32) | ((bytes[1] as i32) << 8) | ((bytes[2] as i32) << 16);
            ((value << 8) >> 8) as i64
        }
        32 => i32::from_le_bytes(bytes.try_into().expect("已验证为 32 位 PCM")) as i64,
        _ => unreachable!("格式已提前验证"),
    };
    let shift = format.container_bits - format.valid_bits;
    let aligned = raw >> shift;
    let scale = (1_u64 << (format.valid_bits - 1)) as f32;
    (aligned as f32 / scale).clamp(-1.0, 1.0)
}

/// 交错排列、已归一化的 PCM 采样处理器。
/// 格式转换放在发送边界之前，确保 RTP 发送层不依赖 WASAPI 的混音格式。
#[derive(Debug, Clone)]
pub struct PcmConverter {
    input_rate: u32,
    input_channels: usize,
    phase: f64,
    pending: Vec<f32>,
}

impl PcmConverter {
    pub fn new(input_rate: u32, input_channels: usize) -> Result<Self> {
        if input_rate == 0 || input_channels == 0 {
            return Err(CoreError::InvalidArgument);
        }
        Ok(Self {
            input_rate,
            input_channels,
            phase: 0.0,
            pending: Vec::new(),
        })
    }

    /// 用线性插值将任意声道数的 f32 交错输入转换成 44.1 kHz 双声道。
    /// WASAPI 原始字节流应在采集边界先解码为 f32，再交由这里处理。
    pub fn convert(&mut self, input: &[f32]) -> Vec<f32> {
        let complete_samples = input.len() / self.input_channels * self.input_channels;
        self.pending.extend_from_slice(&input[..complete_samples]);
        let frames = self.pending.len() / self.input_channels;
        if frames < 2 {
            return Vec::new();
        }
        let step = self.input_rate as f64 / AIRPLAY_SAMPLE_RATE as f64;
        let mut out = Vec::new();
        while self.phase + 1.0 < frames as f64 {
            let i = self.phase as usize;
            let fraction = (self.phase - i as f64) as f32;
            for channel in 0..AIRPLAY_CHANNELS {
                let a = channel_sample(&self.pending, self.input_channels, i, channel);
                let b = channel_sample(&self.pending, self.input_channels, i + 1, channel);
                out.push((a + (b - a) * fraction).clamp(-1.0, 1.0));
            }
            self.phase += step;
        }
        let consumed_frames = (self.phase as usize).min(frames - 1);
        self.pending.drain(..consumed_frames * self.input_channels);
        self.phase -= consumed_frames as f64;
        out
    }
}

fn channel_sample(data: &[f32], channels: usize, frame: usize, channel: usize) -> f32 {
    if channels == 1 {
        data[frame]
    } else {
        data[frame * channels + channel.min(channels - 1)]
    }
}

pub fn f32_to_i16(input: &[f32]) -> Vec<i16> {
    input
        .iter()
        .map(|v| (v.clamp(-1.0, 1.0) * i16::MAX as f32).round() as i16)
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn mono_is_expanded_and_resampled() {
        let mut c = PcmConverter::new(48_000, 1).unwrap();
        let source = vec![0.25; 480];
        let out = c.convert(&source);
        assert_eq!(out.len() / 2, 441);
        assert!(out.iter().all(|v| (*v - 0.25).abs() < 0.001));
    }

    #[test]
    fn streaming_resampler_keeps_fractional_boundary() {
        let mut one_shot = PcmConverter::new(48_000, 1).unwrap();
        let source: Vec<f32> = (0..960).map(|value| value as f32 / 960.0).collect();
        let expected = one_shot.convert(&source);
        let mut streaming = PcmConverter::new(48_000, 1).unwrap();
        let mut actual = streaming.convert(&source[..333]);
        actual.extend(streaming.convert(&source[333..]));
        assert_eq!(actual.len(), expected.len());
        assert!(
            actual
                .iter()
                .zip(expected)
                .all(|(left, right)| (left - right).abs() < 0.0001)
        );
    }

    #[test]
    fn decodes_float_and_integer_wasapi_formats() {
        let float = InputPcmFormat {
            sample_rate: 48_000,
            channels: 2,
            block_align: 8,
            container_bits: 32,
            valid_bits: 32,
            encoding: SampleEncoding::Float,
        };
        let mut float_bytes = Vec::new();
        float_bytes.extend_from_slice(&0.5_f32.to_le_bytes());
        float_bytes.extend_from_slice(&(-0.25_f32).to_le_bytes());
        assert_eq!(float.decode(&float_bytes).unwrap(), vec![0.5, -0.25]);

        let pcm = InputPcmFormat {
            sample_rate: 44_100,
            channels: 1,
            block_align: 2,
            container_bits: 16,
            valid_bits: 16,
            encoding: SampleEncoding::SignedPcm,
        };
        let decoded = pcm.decode(&16_384_i16.to_le_bytes()).unwrap();
        assert!((decoded[0] - 0.5).abs() < 0.001);
    }

    #[test]
    fn decodes_24_valid_bits_in_32_bit_container() {
        let format = InputPcmFormat {
            sample_rate: 48_000,
            channels: 1,
            block_align: 4,
            container_bits: 32,
            valid_bits: 24,
            encoding: SampleEncoding::SignedPcm,
        };
        let raw = (0x400000_i32 << 8).to_le_bytes();
        let decoded = format.decode(&raw).unwrap();
        assert!((decoded[0] - 0.5).abs() < 0.001);
    }
}
