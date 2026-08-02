use thiserror::Error;

#[derive(Debug, Error)]
pub enum CoreError {
    #[error("invalid argument")]
    InvalidArgument,
    #[error("invalid session state: {0}")]
    InvalidState(&'static str),
    #[error("malformed protocol message: {0}")]
    Protocol(&'static str),
    #[error("authentication failed")]
    Authentication,
    #[error("network error: {0}")]
    Network(#[from] std::io::Error),
    #[error("Windows audio error: {0}")]
    Windows(#[from] windows::core::Error),
    #[error("device discovery error: {0}")]
    Discovery(String),
    #[error("pairing rejected by receiver (HTTP {0})")]
    PairingStatus(u16),
    #[error("pairing rejected by receiver (TLV error {0})")]
    PairingTlv(u8),
    #[error("{0} rejected by receiver (RTSP {1})")]
    RtspStatus(&'static str, u16),
    #[error("{0} returned an invalid binary plist")]
    RtspBody(&'static str),
}

pub type Result<T> = std::result::Result<T, CoreError>;

/// 稳定 C ABI 使用的错误码。导出函数返回值和异步错误事件共用这一映射。
pub fn abi_error_code(error: &CoreError) -> i32 {
    match error {
        CoreError::InvalidArgument => -1,
        CoreError::InvalidState(_) => -2,
        CoreError::Protocol(_) => -3,
        CoreError::Authentication => -4,
        CoreError::Network(_) => -5,
        CoreError::Discovery(_) => -6,
        CoreError::PairingStatus(_) => -7,
        CoreError::PairingTlv(_) => -8,
        CoreError::RtspStatus(_, _) => -9,
        CoreError::RtspBody(_) => -10,
        CoreError::Windows(_) => -11,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn abi_error_codes_are_stable() {
        assert_eq!(abi_error_code(&CoreError::InvalidArgument), -1);
        assert_eq!(abi_error_code(&CoreError::Authentication), -4);
        assert_eq!(abi_error_code(&CoreError::RtspStatus("SETUP", 453)), -9);
        assert_eq!(abi_error_code(&CoreError::RtspBody("INFO")), -10);
    }
}
