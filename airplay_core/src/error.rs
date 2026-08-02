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
