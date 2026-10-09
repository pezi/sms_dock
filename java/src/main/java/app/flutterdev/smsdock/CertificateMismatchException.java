package app.flutterdev.smsdock;

/**
 * The server presented a certificate other than the pinned one.
 *
 * <p>{@link #getExpected()} and {@link #getActual()} are SHA-256 fingerprints as upper-case
 * hex pairs separated by colons. Nothing was sent to the server.
 */
public class CertificateMismatchException extends SmsDockException {

    private final String expected;
    private final String actual;

    /**
     * Creates the error.
     *
     * @param expected the pinned fingerprint
     * @param actual the fingerprint of the certificate the server presented
     */
    public CertificateMismatchException(String expected, String actual) {
        super("The certificate of the server does not match the pinned fingerprint.\n"
            + "  expected: " + expected + "\n  received: " + actual + "\n"
            + "Compare the fingerprint with the Server screen of the app. It changes when "
            + "the certificate is regenerated or the app is reinstalled.");
        this.expected = expected;
        this.actual = actual;
    }

    /**
     * Returns the pinned fingerprint.
     *
     * @return the fingerprint as {@code AB:CD:…}
     */
    public String getExpected() {
        return expected;
    }

    /**
     * Returns the fingerprint of the certificate the server presented.
     *
     * @return the fingerprint as {@code AB:CD:…}
     */
    public String getActual() {
        return actual;
    }
}
