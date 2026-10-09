"""Python client for the SMS Dock WebSocket API."""

from .client import (
    API_PROTOCOL_VERSION,
    DEFAULT_API_PORT,
    EVENT_NAMES,
    FINGERPRINT_ENV,
    PASSWORD_ENV,
    ApiError,
    AuthenticationError,
    CertificateMismatchError,
    ConnectionInterrupted,
    LicenceRequiredError,
    ProtocolError,
    SmsDockClient,
    SmsDockError,
    certificate_fingerprint,
    fetch_fingerprint,
    format_fingerprint,
    normalize_fingerprint,
)

__version__ = "0.1.0"

__all__ = [
    "API_PROTOCOL_VERSION",
    "DEFAULT_API_PORT",
    "EVENT_NAMES",
    "FINGERPRINT_ENV",
    "PASSWORD_ENV",
    "ApiError",
    "AuthenticationError",
    "CertificateMismatchError",
    "ConnectionInterrupted",
    "LicenceRequiredError",
    "ProtocolError",
    "SmsDockClient",
    "SmsDockError",
    "certificate_fingerprint",
    "fetch_fingerprint",
    "format_fingerprint",
    "normalize_fingerprint",
]
