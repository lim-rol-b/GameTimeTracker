#[derive(Debug, thiserror::Error)]
pub enum PlatformError {
    #[error("{0}")]
    Message(String),
}

impl PlatformError {
    pub fn message(message: impl Into<String>) -> Self {
        PlatformError::Message(message.into())
    }
}
