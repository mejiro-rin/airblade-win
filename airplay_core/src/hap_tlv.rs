//! HomeKit 配对协议使用的 TLV8 编解码。
//! 超过 255 字节的值会按协议拆成多个同类型字段；解析时会自动拼回原始值。
use crate::error::{CoreError, Result};

pub const TYPE_METHOD: u8 = 0x00;
pub const TYPE_IDENTIFIER: u8 = 0x01;
pub const TYPE_SALT: u8 = 0x02;
pub const TYPE_PUBLIC_KEY: u8 = 0x03;
pub const TYPE_PROOF: u8 = 0x04;
pub const TYPE_ENCRYPTED_DATA: u8 = 0x05;
pub const TYPE_STATE: u8 = 0x06;
pub const TYPE_ERROR: u8 = 0x07;
pub const TYPE_SIGNATURE: u8 = 0x0a;
pub const TYPE_FLAGS: u8 = 0x13;

#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct Tlv8 {
    // HomePod 的部分配对处理对字段顺序敏感，因此必须保留调用方的插入顺序。
    values: Vec<(u8, Vec<u8>)>,
}

impl Tlv8 {
    pub fn insert(&mut self, kind: u8, value: impl AsRef<[u8]>) {
        if let Some((_, current)) = self
            .values
            .iter_mut()
            .find(|(existing, _)| *existing == kind)
        {
            *current = value.as_ref().to_vec();
        } else {
            self.values.push((kind, value.as_ref().to_vec()));
        }
    }
    pub fn get(&self, kind: u8) -> Option<&[u8]> {
        self.values
            .iter()
            .find(|(existing, _)| *existing == kind)
            .map(|(_, value)| value.as_slice())
    }
    pub fn state(&self) -> Result<u8> {
        let value = self
            .get(TYPE_STATE)
            .ok_or(CoreError::Protocol("TLV 缺少状态字段"))?;
        if value.len() != 1 {
            return Err(CoreError::Protocol("TLV 状态字段长度错误"));
        }
        Ok(value[0])
    }
    pub fn encode(&self) -> Vec<u8> {
        let mut result = Vec::new();
        for (kind, value) in &self.values {
            if value.is_empty() {
                result.extend_from_slice(&[*kind, 0]);
                continue;
            }
            for part in value.chunks(u8::MAX as usize) {
                result.push(*kind);
                result.push(part.len() as u8);
                result.extend_from_slice(part);
            }
        }
        result
    }
    pub fn decode(data: &[u8]) -> Result<Self> {
        const MAX_VALUE_LEN: usize = 16 * 1024;
        let mut offset = 0;
        let mut values = Vec::<(u8, Vec<u8>)>::new();
        while offset < data.len() {
            if data.len() - offset < 2 {
                return Err(CoreError::Protocol("TLV 字段头不完整"));
            }
            let kind = data[offset];
            let len = data[offset + 1] as usize;
            offset += 2;
            if data.len() - offset < len {
                return Err(CoreError::Protocol("TLV 字段值不完整"));
            }
            let value = if let Some((_, value)) =
                values.iter_mut().find(|(existing, _)| *existing == kind)
            {
                value
            } else {
                values.push((kind, Vec::new()));
                &mut values.last_mut().expect("刚插入的 TLV 字段必然存在").1
            };
            if value.len() + len > MAX_VALUE_LEN {
                return Err(CoreError::Protocol("TLV 字段过长"));
            }
            value.extend_from_slice(&data[offset..offset + len]);
            offset += len;
        }
        Ok(Self { values })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn large_values_round_trip_across_fragments() {
        let mut tlv = Tlv8::default();
        tlv.insert(TYPE_PUBLIC_KEY, vec![0x5a; 400]);
        let decoded = Tlv8::decode(&tlv.encode()).unwrap();
        assert_eq!(decoded.get(TYPE_PUBLIC_KEY).unwrap(), vec![0x5a; 400]);
    }
    #[test]
    fn malformed_data_is_rejected() {
        assert!(Tlv8::decode(&[TYPE_STATE]).is_err());
        assert!(Tlv8::decode(&[TYPE_STATE, 2, 1]).is_err());
    }
    #[test]
    fn state_requires_one_byte() {
        let mut tlv = Tlv8::default();
        tlv.insert(TYPE_STATE, [3]);
        assert_eq!(tlv.state().unwrap(), 3);
    }
}
