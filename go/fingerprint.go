package smsdock

import (
	"context"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"encoding/hex"
	"net"
	"regexp"
	"strconv"
	"strings"
	"time"
)

var (
	fingerprintPattern   = regexp.MustCompile(`^[0-9a-f]{64}$`)
	fingerprintSeparator = regexp.MustCompile(`[\s:]`)
)

// NormalizeFingerprint returns a SHA-256 fingerprint as 64 lower-case hex
// digits.
//
// It accepts hex with or without colons, in any case, and the line
// printed by "openssl x509 -fingerprint" (sha256 Fingerprint=AB:CD:…).
// Anything else is an [ArgumentError].
func NormalizeFingerprint(fingerprint string) (string, error) {
	text := strings.TrimSpace(fingerprint)
	if index := strings.LastIndex(text, "="); index >= 0 {
		text = text[index+1:]
	}
	text = strings.ToLower(fingerprintSeparator.ReplaceAllString(text, ""))
	if !fingerprintPattern.MatchString(text) {
		return "", argumentError(
			"The fingerprint must be the SHA-256 of the certificate: " +
				"64 hex digits, with or without colons")
	}
	return text, nil
}

// FormatFingerprint returns a fingerprint the way the app shows it:
// AB:CD:….
func FormatFingerprint(fingerprint string) (string, error) {
	digits, err := NormalizeFingerprint(fingerprint)
	if err != nil {
		return "", err
	}
	return formatDigits(digits), nil
}

// CertificateFingerprint returns the SHA-256 fingerprint of a DER
// certificate as AB:CD:….
func CertificateFingerprint(der []byte) string {
	digest := sha256.Sum256(der)
	return formatDigits(hex.EncodeToString(digest[:]))
}

// FetchFingerprint connects to host:port and returns the fingerprint of
// its certificate. A port of 0 means [DefaultAPIPort], a timeout of 0
// means 10 seconds.
//
// This helper trusts nobody: it only reports what the server presented.
// Compare the result with the fingerprint on the Server screen of the app
// before you pin it. Nothing but the TLS handshake is exchanged.
func FetchFingerprint(
	ctx context.Context, host string, port int, timeout time.Duration,
) (string, error) {
	if port == 0 {
		port = DefaultAPIPort
	}
	if timeout <= 0 {
		timeout = 10 * time.Second
	}
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	dialer := tls.Dialer{Config: tlsConfig(nil, nil)}
	address := net.JoinHostPort(host, strconv.Itoa(port))
	connection, err := dialer.DialContext(ctx, "tcp", address)
	if err != nil {
		return "", err
	}
	defer connection.Close()
	certificates := connection.(*tls.Conn).ConnectionState().PeerCertificates
	if len(certificates) == 0 {
		return "", protocolError("The server presented no certificate")
	}
	return CertificateFingerprint(certificates[0].Raw), nil
}

func formatDigits(digits string) string {
	digits = strings.ToUpper(digits)
	pairs := make([]string, 0, len(digits)/2)
	for index := 0; index+2 <= len(digits); index += 2 {
		pairs = append(pairs, digits[index:index+2])
	}
	return strings.Join(pairs, ":")
}

// tlsConfig returns the TLS configuration for the self-signed certificate
// of the phone.
//
// The certificate names no host and has no issuer to check, so host name
// and chain verification are off (InsecureSkipVerify). Instead the
// fingerprint of the certificate is compared during the TLS handshake,
// before anything else is sent. Without a fingerprint nothing is
// compared. A mismatch is also stored in *mismatch, if that is given.
func tlsConfig(
	pinned []byte, mismatch **CertificateMismatchError,
) *tls.Config {
	config := &tls.Config{
		InsecureSkipVerify: true, //nolint:gosec // pinned instead
		MinVersion:         tls.VersionTLS12,
	}
	if pinned != nil {
		config.VerifyConnection = func(state tls.ConnectionState) error {
			err := checkPin(state, pinned)
			if err != nil && mismatch != nil {
				*mismatch = err
			}
			if err != nil {
				return err
			}
			return nil
		}
	}
	return config
}

// checkPin returns a CertificateMismatchError unless the digest of the
// certificate of the server equals the pinned one. The comparison takes
// constant time.
func checkPin(
	state tls.ConnectionState, pinned []byte,
) *CertificateMismatchError {
	expected := formatDigits(hex.EncodeToString(pinned))
	if len(state.PeerCertificates) == 0 {
		return &CertificateMismatchError{
			Expected: expected, Actual: "no certificate",
		}
	}
	actual := sha256.Sum256(state.PeerCertificates[0].Raw)
	if subtle.ConstantTimeCompare(actual[:], pinned) != 1 {
		return &CertificateMismatchError{
			Expected: expected,
			Actual:   formatDigits(hex.EncodeToString(actual[:])),
		}
	}
	return nil
}
