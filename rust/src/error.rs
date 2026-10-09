//! The errors of this crate.

use std::fmt;

use serde_json::{Map, Value};

/// Result type of this crate.
pub type Result<T> = std::result::Result<T, Error>;

/// Everything that can go wrong in this crate.
#[derive(Debug, Clone, PartialEq)]
#[non_exhaustive]
pub enum Error {
    /// The server answered a request with an error.
    ///
    /// `code` is one of the error codes of `API.md`, `message` the text of
    /// the server. `details` holds the other fields of the error object,
    /// for example `{"retryAfter": 30}` or `{"permission": "sendSms"}`.
    ///
    /// The codes `invalid_password`, `locked_out` and `not_authenticated`
    /// mean that the login failed or the connection is no longer logged in
    /// (see [`Error::is_authentication`]). The client never retries after
    /// them, because every wrong password counts towards the lock of the
    /// phone. The code `licence_required` means that the API needs the full
    /// licence of the app (see [`Error::is_licence_required`]).
    Api {
        /// Error code of `API.md`, for example `invalid_number`.
        code: String,
        /// Text of the server.
        message: String,
        /// Other fields of the error object.
        details: Map<String, Value>,
    },
    /// The server sent something that protocol version 1 does not allow.
    Protocol(String),
    /// The connection failed and could not be restored.
    ///
    /// `close_code` and `close_reason` are set when the server closed the
    /// WebSocket with a close frame.
    ConnectionInterrupted {
        /// What happened.
        message: String,
        /// Close code of the server, if there was a close frame.
        close_code: Option<u16>,
        /// Close reason of the server, if there was one.
        close_reason: Option<String>,
    },
    /// The server presented a certificate other than the pinned one.
    ///
    /// Both fields are SHA-256 fingerprints as upper-case hex pairs
    /// separated by colons. Nothing was sent to the server.
    CertificateMismatch {
        /// The pinned fingerprint.
        expected: String,
        /// The fingerprint of the certificate the server presented.
        actual: String,
    },
    /// A wrong argument; nothing was sent.
    InvalidArgument(String),
    /// The time to wait for an event, a result or a response is over.
    Timeout(String),
}

impl Error {
    /// The error code of the server, if this is [`Error::Api`].
    pub fn code(&self) -> Option<&str> {
        match self {
            Error::Api { code, .. } => Some(code),
            _ => None,
        }
    }

    /// Seconds to wait before the next attempt, if the server said so.
    ///
    /// Set for `locked_out` and for `rate_limited` by requests per second.
    pub fn retry_after(&self) -> Option<i64> {
        match self.detail("retryAfter")? {
            Value::Number(number) => number
                .as_i64()
                .or_else(|| number.as_f64().map(|seconds| seconds as i64)),
            _ => None,
        }
    }

    /// The missing Android permission of a `permission_missing` error.
    pub fn permission(&self) -> Option<&str> {
        self.detail("permission")?.as_str()
    }

    /// Whether the server answered with an error ([`Error::Api`]).
    pub fn is_api(&self) -> bool {
        matches!(self, Error::Api { .. })
    }

    /// Whether the login failed or the connection is no longer logged in.
    ///
    /// True for the codes `invalid_password`, `locked_out` (see
    /// [`Error::retry_after`]) and `not_authenticated`.
    pub fn is_authentication(&self) -> bool {
        matches!(
            self.code(),
            Some("invalid_password" | "locked_out" | "not_authenticated")
        )
    }

    /// Whether the API needs the full licence of the app.
    pub fn is_licence_required(&self) -> bool {
        self.code() == Some("licence_required")
    }

    /// The close code of the server, if it closed the connection with one.
    ///
    /// Set for [`Error::ConnectionInterrupted`] and for the errors made
    /// from the close codes 4401, 4410 and 4403.
    pub fn close_code(&self) -> Option<u16> {
        match self {
            Error::ConnectionInterrupted { close_code, .. } => *close_code,
            Error::Api { .. } => self
                .detail("closeCode")?
                .as_u64()
                .and_then(|code| u16::try_from(code).ok()),
            _ => None,
        }
    }

    fn detail(&self, name: &str) -> Option<&Value> {
        match self {
            Error::Api { details, .. } => details.get(name),
            _ => None,
        }
    }

    /// Build the error for an error object of a response.
    pub(crate) fn from_error_object(error: &Map<String, Value>) -> Error {
        let code = match error.get("code") {
            Some(Value::String(code)) => code.clone(),
            Some(other) => other.to_string(),
            None => "unknown".to_owned(),
        };
        let message = match error.get("message") {
            Some(Value::String(text)) if !text.is_empty() => text.clone(),
            _ => "no message".to_owned(),
        };
        let details = error
            .iter()
            .filter(|(key, _)| key.as_str() != "code" && key.as_str() != "message")
            .map(|(key, value)| (key.clone(), value.clone()))
            .collect();
        Error::Api {
            code,
            message,
            details,
        }
    }

    /// Build the error for a close code that stands for an API error.
    pub(crate) fn from_close_code(code: &str, message: String, close_code: u16) -> Error {
        let mut details = Map::new();
        details.insert("closeCode".to_owned(), Value::from(close_code));
        Error::Api {
            code: code.to_owned(),
            message,
            details,
        }
    }
}

impl fmt::Display for Error {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Error::Api { code, message, .. } => write!(formatter, "{code}: {message}"),
            Error::Protocol(message)
            | Error::InvalidArgument(message)
            | Error::Timeout(message)
            | Error::ConnectionInterrupted { message, .. } => formatter.write_str(message),
            Error::CertificateMismatch { expected, actual } => write!(
                formatter,
                "The certificate of the server does not match the pinned \
                 fingerprint.\n  expected: {expected}\n  received: {actual}\n\
                 Compare the fingerprint with the Server screen of the app. It \
                 changes when the certificate is regenerated or the app is \
                 reinstalled."
            ),
        }
    }
}

impl std::error::Error for Error {}

#[cfg(test)]
mod tests {
    use serde_json::json;

    use super::*;

    fn api(value: Value) -> Error {
        Error::from_error_object(value.as_object().unwrap())
    }

    #[test]
    fn api_errors_carry_their_details() {
        let error = api(json!({
            "code": "locked_out", "message": "Too many attempts.", "retryAfter": 30,
        }));
        assert_eq!(error.code(), Some("locked_out"));
        assert_eq!(error.retry_after(), Some(30));
        assert_eq!(error.permission(), None);
        assert!(error.is_api() && error.is_authentication());
        assert!(!error.is_licence_required());
        assert_eq!(error.to_string(), "locked_out: Too many attempts.");

        let error = api(json!({
            "code": "permission_missing", "message": "Missing.", "permission": "sendSms",
        }));
        assert_eq!(error.permission(), Some("sendSms"));
        assert_eq!(error.retry_after(), None);
        assert!(!error.is_authentication());
    }

    #[test]
    fn retry_after_must_be_a_number() {
        let text = api(json!({"code": "rate_limited", "message": "x", "retryAfter": "1"}));
        assert_eq!(text.retry_after(), None);
        let boolean = api(json!({"code": "rate_limited", "message": "x", "retryAfter": true}));
        assert_eq!(boolean.retry_after(), None);
        let fraction = api(json!({"code": "rate_limited", "message": "x", "retryAfter": 2.7}));
        assert_eq!(fraction.retry_after(), Some(2));
    }

    #[test]
    fn errors_without_code_or_message() {
        let error = api(json!({}));
        assert_eq!(error.to_string(), "unknown: no message");
        assert!(api(json!({"code": "licence_required"})).is_licence_required());
    }

    #[test]
    fn mismatch_names_both_fingerprints() {
        let error = Error::CertificateMismatch {
            expected: "AA".to_owned(),
            actual: "BB".to_owned(),
        };
        let text = error.to_string();
        assert!(text.contains("expected: AA") && text.contains("received: BB"));
        assert_eq!(error.close_code(), None);
        assert!(!error.is_api());
    }
}
