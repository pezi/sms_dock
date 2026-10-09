package app.flutterdev.smsdock;

import java.io.IOException;
import java.net.InetSocketAddress;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.time.Duration;
import java.util.List;
import java.util.Locale;
import java.util.regex.Pattern;
import javax.net.ssl.SSLSocket;

/**
 * Constants of the SMS Dock WebSocket API and helpers for certificate fingerprints.
 *
 * <p>The protocol is described in {@code API.md} at the top of the repository.
 */
public final class SmsDock {

    /** Protocol version this client speaks. */
    public static final int API_PROTOCOL_VERSION = 1;

    /** Default port of the WebSocket API. */
    public static final int DEFAULT_API_PORT = 9443;

    /** Largest frame the server accepts from a client, in bytes. */
    public static final int MAX_FRAME_BYTES = 64 * 1024;

    /** Largest frame the client accepts. Answers may exceed 64 KiB. */
    public static final int MAX_INCOMING_BYTES = 4 * 1024 * 1024;

    /** Largest value of {@code limit}. */
    public static final int MAX_PAGE_SIZE = 200;

    /** Environment variable that holds the password. */
    public static final String PASSWORD_ENV = "SMSDOCK_PASSWORD";

    /** Environment variable that holds the certificate fingerprint. */
    public static final String FINGERPRINT_ENV = "SMSDOCK_FINGERPRINT";

    /** All event names of protocol version 1. */
    public static final List<String> EVENT_NAMES = List.of(
        "sms.received",
        "sms.queued",
        "sms.sent",
        "sms.delivered",
        "sms.failed",
        "sms.deleted",
        "conversations.changed");

    private static final Pattern FINGERPRINT_PATTERN = Pattern.compile("[0-9a-f]{64}");
    private static final char[] HEX = "0123456789ABCDEF".toCharArray();

    private SmsDock() {
    }

    /**
     * Returns a SHA-256 fingerprint as 64 lower-case hex digits.
     *
     * <p>Accepts hex with or without colons, in any case, and the line printed by
     * {@code openssl x509 -fingerprint} ({@code sha256 Fingerprint=AB:CD:…}).
     *
     * @param fingerprint the fingerprint in one of these forms
     * @return 64 lower-case hex digits
     * @throws IllegalArgumentException for anything else
     */
    public static String normalizeFingerprint(String fingerprint) {
        String text = fingerprint == null ? "" : fingerprint.strip();
        int equals = text.lastIndexOf('=');
        if (equals >= 0) {
            text = text.substring(equals + 1);
        }
        text = text.replaceAll("[\\s:]", "").toLowerCase(Locale.ROOT);
        if (!FINGERPRINT_PATTERN.matcher(text).matches()) {
            throw new IllegalArgumentException(
                "The fingerprint must be the SHA-256 of the certificate: "
                    + "64 hex digits, with or without colons");
        }
        return text;
    }

    /**
     * Returns a fingerprint the way the app shows it: {@code AB:CD:…}.
     *
     * @param fingerprint the fingerprint in one of the forms of {@link #normalizeFingerprint}
     * @return upper-case hex pairs separated by colons
     * @throws IllegalArgumentException if it is no SHA-256 fingerprint
     */
    public static String formatFingerprint(String fingerprint) {
        return formatDigest(fingerprintBytes(fingerprint));
    }

    /**
     * Returns the SHA-256 fingerprint of a DER certificate as {@code AB:CD:…}.
     *
     * @param der the certificate in DER form
     * @return upper-case hex pairs separated by colons
     */
    public static String certificateFingerprint(byte[] der) {
        return formatDigest(sha256(der));
    }

    /**
     * Connects to {@code host} on the default port and returns the fingerprint of its
     * certificate; see {@link #fetchFingerprint(String, int, Duration)}.
     *
     * @param host address of the phone
     * @return the fingerprint as {@code AB:CD:…}
     */
    public static String fetchFingerprint(String host) {
        return fetchFingerprint(host, DEFAULT_API_PORT, Duration.ofSeconds(10));
    }

    /**
     * Connects to {@code host:port} and returns the fingerprint of its certificate.
     *
     * <p>This helper trusts nobody: it only reports what the server presented. Compare the
     * result with the fingerprint on the Server screen of the app before you pin it. Nothing
     * but the TLS handshake is exchanged.
     *
     * @param host address of the phone
     * @param port port of the API
     * @param timeout longest time to wait
     * @return the fingerprint as {@code AB:CD:…}
     * @throws ConnectionInterruptedException if the server cannot be reached
     * @throws ProtocolException if the server presented no certificate
     */
    public static String fetchFingerprint(String host, int port, Duration timeout) {
        if (timeout == null || timeout.isZero() || timeout.isNegative()) {
            throw new IllegalArgumentException("Timeouts must be positive");
        }
        int milliseconds = (int) Math.min(Integer.MAX_VALUE, Math.max(1, timeout.toMillis()));
        PinningTrustManager trust = new PinningTrustManager(null);
        try (SSLSocket socket = (SSLSocket) trust.context().getSocketFactory().createSocket()) {
            socket.connect(new InetSocketAddress(host, port), milliseconds);
            socket.setSoTimeout(milliseconds);
            socket.startHandshake();
        } catch (IOException | RuntimeException error) {
            throw new ConnectionInterruptedException(
                "Cannot connect to " + host + ":" + port + ": " + Connection.describe(error),
                null, null, error);
        }
        byte[] der = trust.presented();
        if (der == null || der.length == 0) {
            throw new ProtocolException("The server presented no certificate");
        }
        return certificateFingerprint(der);
    }

    /** Returns the 32 bytes of a fingerprint given in any accepted form. */
    static byte[] fingerprintBytes(String fingerprint) {
        String digits = normalizeFingerprint(fingerprint);
        byte[] bytes = new byte[digits.length() / 2];
        for (int index = 0; index < bytes.length; index++) {
            bytes[index] = (byte) Integer.parseInt(
                digits.substring(index * 2, index * 2 + 2), 16);
        }
        return bytes;
    }

    /** Returns a digest as upper-case hex pairs separated by colons. */
    static String formatDigest(byte[] digest) {
        StringBuilder text = new StringBuilder(digest.length * 3);
        for (byte value : digest) {
            if (text.length() > 0) {
                text.append(':');
            }
            text.append(HEX[(value >> 4) & 0x0F]).append(HEX[value & 0x0F]);
        }
        return text.toString();
    }

    static byte[] sha256(byte[] data) {
        try {
            return MessageDigest.getInstance("SHA-256").digest(data);
        } catch (NoSuchAlgorithmException error) {
            throw new IllegalStateException("SHA-256 is not available", error);
        }
    }
}
