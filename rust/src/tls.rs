//! TLS for the self-signed certificate of the phone.
//!
//! The certificate names no host and has no issuer to check, so host name
//! and chain verification play no part. Instead the SHA-256 fingerprint of
//! the certificate is compared inside the TLS handshake, before anything
//! else is sent. The certificate store of the system is never used.

use std::io;
use std::net::{TcpStream, ToSocketAddrs};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::client::Resumption;
use rustls::crypto::{verify_tls12_signature, verify_tls13_signature, CryptoProvider};
use rustls::pki_types::{CertificateDer, ServerName, UnixTime};
use rustls::{
    CertificateError, ClientConfig, ClientConnection, DigitallySignedStruct, SignatureScheme,
    StreamOwned,
};
use sha2::{Digest, Sha256};
use subtle::ConstantTimeEq;

use crate::error::{Error, Result};

/// A TLS connection to the phone.
pub(crate) type TlsStream = StreamOwned<ClientConnection, TcpStream>;

/// SHA-256 digest of a certificate.
pub(crate) type Digest32 = [u8; 32];

const FINGERPRINT_HELP: &str = "The fingerprint must be the SHA-256 of the certificate: \
                                64 hex digits, with or without colons";

/// Why a TLS connection could not be opened.
#[derive(Debug)]
pub(crate) enum TlsFailure {
    /// The certificate of the server is not the pinned one.
    Mismatch { expected: String, actual: String },
    /// The server presented no certificate at all.
    NoCertificate,
    /// The time to connect is over.
    TimedOut,
    /// Anything else, as text.
    Other(String),
}

/// Return a SHA-256 fingerprint as 64 lower-case hex digits.
///
/// Accepts hex with or without colons, in any case, and the line printed
/// by `openssl x509 -fingerprint` (`sha256 Fingerprint=AB:CD:…`). Anything
/// else is an [`Error::InvalidArgument`].
pub fn normalize_fingerprint(fingerprint: &str) -> Result<String> {
    let mut text = fingerprint.trim();
    if let Some((_, value)) = text.rsplit_once('=') {
        text = value;
    }
    let digits: String = text
        .chars()
        .filter(|character| !character.is_whitespace() && *character != ':')
        .map(|character| character.to_ascii_lowercase())
        .collect();
    let valid = digits.len() == 64
        && digits
            .bytes()
            .all(|byte| byte.is_ascii_digit() || (b'a'..=b'f').contains(&byte));
    if !valid {
        return Err(Error::InvalidArgument(FINGERPRINT_HELP.to_owned()));
    }
    Ok(digits)
}

/// Return a fingerprint the way the app shows it: `AB:CD:…`.
pub fn format_fingerprint(fingerprint: &str) -> Result<String> {
    let digits = normalize_fingerprint(fingerprint)?.to_ascii_uppercase();
    let pairs: Vec<&str> = (0..digits.len())
        .step_by(2)
        .map(|index| &digits[index..index + 2])
        .collect();
    Ok(pairs.join(":"))
}

/// Return the SHA-256 fingerprint of a DER certificate as `AB:CD:…`.
pub fn certificate_fingerprint(der: &[u8]) -> String {
    format_digest(&digest(der))
}

/// Connect to `host:port` and return the fingerprint of its certificate.
///
/// This helper trusts nobody: it only reports what the server presented.
/// Compare the result with the fingerprint on the Server screen of the app
/// before you pin it. Nothing but the TLS handshake is exchanged.
///
/// [`DEFAULT_API_PORT`](crate::DEFAULT_API_PORT) is the port the app uses
/// unless it was changed.
pub fn fetch_fingerprint(host: &str, port: u16, timeout: Duration) -> Result<String> {
    if timeout.is_zero() {
        return Err(Error::InvalidArgument(
            "Timeouts must be positive".to_owned(),
        ));
    }
    let deadline = Instant::now() + timeout;
    let failed = |cause: String| Error::ConnectionInterrupted {
        message: format!("Cannot connect to {host}:{port}: {cause}"),
        close_code: None,
        close_reason: None,
    };
    let tcp = tcp_connect(host, port, deadline).map_err(|error| failed(error.to_string()))?;
    match handshake(tcp, host, None, deadline) {
        Ok(stream) => match stream
            .conn
            .peer_certificates()
            .and_then(|list| list.first())
        {
            Some(certificate) => Ok(certificate_fingerprint(certificate.as_ref())),
            None => Err(Error::Protocol(
                "The server presented no certificate".to_owned(),
            )),
        },
        Err(TlsFailure::NoCertificate) => Err(Error::Protocol(
            "The server presented no certificate".to_owned(),
        )),
        Err(TlsFailure::TimedOut) => Err(failed("timed out".to_owned())),
        Err(TlsFailure::Other(cause)) => Err(failed(cause)),
        Err(TlsFailure::Mismatch { .. }) => Err(failed("unexpected certificate check".to_owned())),
    }
}

/// Parse a pinned fingerprint into its 32 bytes.
pub(crate) fn parse_pin(fingerprint: &str) -> Result<Digest32> {
    let digits = normalize_fingerprint(fingerprint)?;
    let mut pin = [0_u8; 32];
    for (index, byte) in pin.iter_mut().enumerate() {
        *byte = u8::from_str_radix(&digits[index * 2..index * 2 + 2], 16)
            .map_err(|_| Error::InvalidArgument(FINGERPRINT_HELP.to_owned()))?;
    }
    Ok(pin)
}

/// Open a TCP connection before the deadline.
pub(crate) fn tcp_connect(host: &str, port: u16, deadline: Instant) -> io::Result<TcpStream> {
    let mut last = io::Error::new(
        io::ErrorKind::NotFound,
        format!("no address found for {host}"),
    );
    for address in (host, port).to_socket_addrs()? {
        let remaining = remaining(deadline)
            .ok_or_else(|| io::Error::new(io::ErrorKind::TimedOut, "timed out while connecting"))?;
        match TcpStream::connect_timeout(&address, remaining) {
            Ok(stream) => {
                // Requests are small; do not wait to fill packets.
                let _ = stream.set_nodelay(true);
                return Ok(stream);
            }
            Err(error) => last = error,
        }
    }
    Err(last)
}

/// Do the TLS handshake and compare the certificate with the pin.
///
/// The comparison happens inside the handshake: on a mismatch the client
/// aborts it and has sent nothing but TLS handshake messages. Without a pin
/// every certificate is accepted.
pub(crate) fn handshake(
    mut tcp: TcpStream,
    host: &str,
    pin: Option<&Digest32>,
    deadline: Instant,
) -> std::result::Result<TlsStream, TlsFailure> {
    let seen = Arc::new(Mutex::new(None));
    let provider = Arc::new(rustls::crypto::ring::default_provider());
    let verifier = PinVerifier {
        pin: pin.copied(),
        seen: Arc::clone(&seen),
        provider: Arc::clone(&provider),
    };
    let mut config = ClientConfig::builder_with_provider(provider)
        .with_safe_default_protocol_versions()
        .map_err(|error| TlsFailure::Other(error.to_string()))?
        .dangerous()
        .with_custom_certificate_verifier(Arc::new(verifier))
        .with_no_client_auth();
    // A resumed session would skip the certificate; every connection must
    // show it again.
    config.resumption = Resumption::disabled();
    let mut connection = ClientConnection::new(Arc::new(config), server_name(host))
        .map_err(|error| TlsFailure::Other(error.to_string()))?;

    while connection.is_handshaking() {
        let wait = remaining(deadline).ok_or(TlsFailure::TimedOut)?;
        let timeouts = tcp
            .set_read_timeout(Some(wait))
            .and_then(|()| tcp.set_write_timeout(Some(wait)));
        if let Err(error) = timeouts {
            return Err(TlsFailure::Other(error.to_string()));
        }
        if let Err(error) = connection.complete_io(&mut tcp) {
            let observed = *seen.lock().unwrap_or_else(|poison| poison.into_inner());
            return Err(match (pin, observed) {
                (Some(pin), Some(actual)) if !same_digest(pin, &actual) => mismatch(pin, &actual),
                _ if is_timeout(&error) => TlsFailure::TimedOut,
                _ => TlsFailure::Other(error.to_string()),
            });
        }
    }

    // Once more, with the certificate of the connection that will be used.
    let presented = connection
        .peer_certificates()
        .and_then(|list| list.first())
        .map(|certificate| digest(certificate.as_ref()));
    match (pin, presented) {
        (_, None) => return Err(TlsFailure::NoCertificate),
        (Some(pin), Some(actual)) if !same_digest(pin, &actual) => {
            return Err(mismatch(pin, &actual));
        }
        _ => {}
    }
    Ok(StreamOwned::new(connection, tcp))
}

/// Whether an I/O error means that a read or write timeout passed.
pub(crate) fn is_timeout(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut
    )
}

/// Time left until the deadline; `None` when it has passed.
pub(crate) fn remaining(deadline: Instant) -> Option<Duration> {
    let left = deadline.saturating_duration_since(Instant::now());
    (!left.is_zero()).then_some(left)
}

fn digest(der: &[u8]) -> Digest32 {
    Sha256::digest(der).into()
}

fn format_digest(digest: &Digest32) -> String {
    let pairs: Vec<String> = digest.iter().map(|byte| format!("{byte:02X}")).collect();
    pairs.join(":")
}

/// Compare two digests in constant time.
fn same_digest(first: &Digest32, second: &Digest32) -> bool {
    first.ct_eq(second).into()
}

fn mismatch(pin: &Digest32, actual: &Digest32) -> TlsFailure {
    TlsFailure::Mismatch {
        expected: format_digest(pin),
        actual: format_digest(actual),
    }
}

/// The name for the TLS handshake. It is not verified; the URL of the
/// phone usually holds an IP address, for which no name is sent.
fn server_name(host: &str) -> ServerName<'static> {
    ServerName::try_from(host.to_owned())
        .unwrap_or_else(|_| ServerName::IpAddress(std::net::Ipv4Addr::UNSPECIFIED.into()))
}

/// Accepts exactly the pinned certificate, or any without a pin.
///
/// The signature of the handshake is always verified with the key of the
/// certificate, so the server must own that key.
#[derive(Debug)]
struct PinVerifier {
    pin: Option<Digest32>,
    seen: Arc<Mutex<Option<Digest32>>>,
    provider: Arc<CryptoProvider>,
}

impl ServerCertVerifier for PinVerifier {
    fn verify_server_cert(
        &self,
        end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> std::result::Result<ServerCertVerified, rustls::Error> {
        let actual = digest(end_entity.as_ref());
        *self
            .seen
            .lock()
            .unwrap_or_else(|poison| poison.into_inner()) = Some(actual);
        match &self.pin {
            Some(pin) if !same_digest(pin, &actual) => Err(rustls::Error::InvalidCertificate(
                CertificateError::ApplicationVerificationFailure,
            )),
            _ => Ok(ServerCertVerified::assertion()),
        }
    }

    fn verify_tls12_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> std::result::Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls12_signature(
            message,
            cert,
            dss,
            &self.provider.signature_verification_algorithms,
        )
    }

    fn verify_tls13_signature(
        &self,
        message: &[u8],
        cert: &CertificateDer<'_>,
        dss: &DigitallySignedStruct,
    ) -> std::result::Result<HandshakeSignatureValid, rustls::Error> {
        verify_tls13_signature(
            message,
            cert,
            dss,
            &self.provider.signature_verification_algorithms,
        )
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.provider
            .signature_verification_algorithms
            .supported_schemes()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const DIGITS: &str = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    fn colons() -> String {
        let pairs: Vec<&str> = (0..64).step_by(2).map(|i| &DIGITS[i..i + 2]).collect();
        pairs.join(":")
    }

    #[test]
    fn fingerprint_forms() {
        let colons = colons();
        let upper = colons.to_uppercase();
        assert_eq!(
            normalize_fingerprint(&DIGITS.to_uppercase()).unwrap(),
            DIGITS
        );
        assert_eq!(normalize_fingerprint(&colons).unwrap(), DIGITS);
        assert_eq!(
            normalize_fingerprint(&format!(" {upper}\n")).unwrap(),
            DIGITS
        );
        assert_eq!(
            normalize_fingerprint(&format!("sha256 Fingerprint={upper}")).unwrap(),
            DIGITS
        );
        assert_eq!(format_fingerprint(DIGITS).unwrap(), upper);
    }

    #[test]
    fn malformed_fingerprints() {
        for text in [
            "",
            "AB:CD",
            &"zz".repeat(32),
            &"AB".repeat(31),
            &"AB".repeat(33),
            &"AB".repeat(20),
            &"ä".repeat(32),
        ] {
            let error = normalize_fingerprint(text).unwrap_err();
            assert!(matches!(&error, Error::InvalidArgument(m) if m.contains("SHA-256")));
            assert!(parse_pin(text).is_err());
        }
    }

    #[test]
    fn fingerprint_of_a_certificate() {
        // The well-known SHA-256 of the ASCII text "abc".
        let known = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        let actual = certificate_fingerprint(b"abc");
        assert_eq!(actual, format_fingerprint(known).unwrap());
        assert_eq!(parse_pin(&actual).unwrap(), digest(b"abc"));
    }
}
