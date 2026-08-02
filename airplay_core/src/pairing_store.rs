//! 每个 Windows 用户独立保存配对密钥。
//! 使用 DPAPI 加密后才写入磁盘，明文密钥不会写入日志。
use sha2::{Digest, Sha256};
use std::{fs, path::PathBuf};
use windows::Win32::{
    Foundation::{HLOCAL, LocalFree},
    Security::Cryptography::{
        CRYPT_INTEGER_BLOB, CRYPTPROTECT_UI_FORBIDDEN, CryptProtectData, CryptUnprotectData,
    },
};

use crate::error::{CoreError, Result};

const MAGIC: &[u8; 4] = b"ABPK";
const FORMAT_VERSION: u8 = 1;

fn path_for(device_id: &str) -> Result<PathBuf> {
    if device_id.is_empty() {
        return Err(CoreError::InvalidArgument);
    }
    let root = std::env::var_os("LOCALAPPDATA")
        .ok_or(CoreError::InvalidState("LOCALAPPDATA unavailable"))?;
    let digest = Sha256::digest(device_id.as_bytes());
    let name = digest
        .iter()
        .map(|v| format!("{v:02x}"))
        .collect::<String>();
    let folder = PathBuf::from(root).join("AirBlade").join("pairings");
    Ok(folder.join(format!("{name}.v1")))
}

pub fn save(device_id: &str, key_material: &[u8]) -> Result<()> {
    if key_material.is_empty() {
        return Err(CoreError::InvalidArgument);
    }
    let serialized = serialize_record(device_id, key_material)?;
    let input = CRYPT_INTEGER_BLOB {
        cbData: u32::try_from(serialized.len()).map_err(|_| CoreError::InvalidArgument)?,
        pbData: serialized.as_ptr() as *mut u8,
    };
    let mut encrypted = CRYPT_INTEGER_BLOB::default();
    unsafe {
        CryptProtectData(
            &input,
            None,
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut encrypted,
        )
        .map_err(|_| CoreError::Authentication)?;
    }
    let protected =
        unsafe { std::slice::from_raw_parts(encrypted.pbData, encrypted.cbData as usize).to_vec() };
    unsafe {
        let _ = LocalFree(HLOCAL(encrypted.pbData as *mut _));
    }
    let path = path_for(device_id)?;
    let folder = path
        .parent()
        .ok_or(CoreError::InvalidState("配对目录无效"))?;
    fs::create_dir_all(folder).map_err(CoreError::Network)?;
    fs::write(path, protected).map_err(CoreError::Network)
}

pub fn load(device_id: &str) -> Result<Option<Vec<u8>>> {
    let path = path_for(device_id)?;
    let encrypted = match fs::read(path) {
        Ok(data) => data,
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(e) => return Err(CoreError::Network(e)),
    };
    if encrypted.is_empty() {
        return Ok(None);
    }
    let input = CRYPT_INTEGER_BLOB {
        cbData: u32::try_from(encrypted.len()).map_err(|_| CoreError::InvalidArgument)?,
        pbData: encrypted.as_ptr() as *mut u8,
    };
    let mut plaintext = CRYPT_INTEGER_BLOB::default();
    let decrypted = unsafe {
        CryptUnprotectData(
            &input,
            None,
            None,
            None,
            None,
            CRYPTPROTECT_UI_FORBIDDEN,
            &mut plaintext,
        )
    };
    // 文件损坏、被其他 Windows 用户复制，或 DPAPI 校验失败时都视为未配对。
    if decrypted.is_err() {
        return Ok(None);
    }
    let result =
        unsafe { std::slice::from_raw_parts(plaintext.pbData, plaintext.cbData as usize).to_vec() };
    unsafe {
        let _ = LocalFree(HLOCAL(plaintext.pbData as *mut _));
    }
    Ok(parse_record(device_id, &result))
}

fn serialize_record(device_id: &str, key_material: &[u8]) -> Result<Vec<u8>> {
    if device_id.is_empty() || key_material.is_empty() {
        return Err(CoreError::InvalidArgument);
    }
    let identifier = device_id.as_bytes();
    let identifier_length =
        u16::try_from(identifier.len()).map_err(|_| CoreError::InvalidArgument)?;
    let key_length = u32::try_from(key_material.len()).map_err(|_| CoreError::InvalidArgument)?;
    let mut record = Vec::with_capacity(11 + identifier.len() + key_material.len());
    record.extend_from_slice(MAGIC);
    record.push(FORMAT_VERSION);
    record.extend_from_slice(&identifier_length.to_le_bytes());
    record.extend_from_slice(&key_length.to_le_bytes());
    record.extend_from_slice(identifier);
    record.extend_from_slice(key_material);
    Ok(record)
}

fn parse_record(expected_device_id: &str, record: &[u8]) -> Option<Vec<u8>> {
    if record.len() < 11 || &record[..4] != MAGIC || record[4] != FORMAT_VERSION {
        return None;
    }
    let identifier_length = u16::from_le_bytes([record[5], record[6]]) as usize;
    let key_length = u32::from_le_bytes([record[7], record[8], record[9], record[10]]) as usize;
    let expected_length = 11_usize
        .checked_add(identifier_length)?
        .checked_add(key_length)?;
    if record.len() != expected_length {
        return None;
    }
    let identifier_end = 11 + identifier_length;
    if record.get(11..identifier_end)? != expected_device_id.as_bytes() || key_length == 0 {
        return None;
    }
    Some(record[identifier_end..].to_vec())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn versioned_record_round_trips() {
        let record = serialize_record("06:B1:CC:F7:1E:CB", &[1, 2, 3, 4]).unwrap();
        assert_eq!(
            parse_record("06:B1:CC:F7:1E:CB", &record),
            Some(vec![1, 2, 3, 4])
        );
    }

    #[test]
    fn wrong_device_version_or_length_is_treated_as_unpaired() {
        let record = serialize_record("receiver-a", &[7; 32]).unwrap();
        assert_eq!(parse_record("receiver-b", &record), None);

        let mut wrong_version = record.clone();
        wrong_version[4] = FORMAT_VERSION + 1;
        assert_eq!(parse_record("receiver-a", &wrong_version), None);

        let mut truncated = record;
        truncated.pop();
        assert_eq!(parse_record("receiver-a", &truncated), None);
    }
}
