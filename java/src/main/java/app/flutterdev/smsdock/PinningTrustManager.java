package app.flutterdev.smsdock;

import java.net.Socket;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLEngine;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509ExtendedTrustManager;

/**
 * Trust manager for the self-signed certificate of the phone.
 *
 * <p>The certificate names no host and has no issuer to check, so neither the host name nor
 * the chain is verified and the trust store of the system plays no part. Instead the SHA-256
 * of the certificate is compared with the pinned fingerprint in constant time. This happens
 * inside the TLS handshake: on a mismatch the handshake fails and nothing else is sent, not
 * even the WebSocket request. Without a fingerprint nothing is compared.
 *
 * <p>The class extends {@link X509ExtendedTrustManager} so that the JDK applies no endpoint
 * identification of its own.
 */
final class PinningTrustManager extends X509ExtendedTrustManager {

    private final byte[] pinned;
    private volatile CertificateMismatchException mismatch;
    private volatile byte[] presented;

    /**
     * Creates the trust manager.
     *
     * @param pinned SHA-256 of the only accepted certificate; {@code null} accepts all
     */
    PinningTrustManager(byte[] pinned) {
        this.pinned = pinned == null ? null : pinned.clone();
    }

    /** Returns a TLS context that uses this trust manager and nothing else. */
    SSLContext context() {
        try {
            SSLContext context = SSLContext.getInstance("TLS");
            context.init(null, new TrustManager[] {this}, null);
            return context;
        } catch (GeneralSecurityException error) {
            throw new IllegalStateException("TLS is not available", error);
        }
    }

    /** The mismatch found in the handshake, or {@code null}. */
    CertificateMismatchException mismatch() {
        return mismatch;
    }

    /** The DER certificate the server presented, or {@code null} before the handshake. */
    byte[] presented() {
        byte[] der = presented;
        return der == null ? null : der.clone();
    }

    private void check(X509Certificate[] chain) throws CertificateException {
        byte[] der = chain == null || chain.length == 0 ? null : chain[0].getEncoded();
        presented = der;
        if (pinned == null) {
            return;
        }
        byte[] actual = SmsDock.sha256(der == null ? new byte[0] : der);
        // MessageDigest.isEqual compares in constant time.
        if (der == null || der.length == 0 || !MessageDigest.isEqual(actual, pinned)) {
            mismatch = new CertificateMismatchException(
                SmsDock.formatDigest(pinned),
                der == null || der.length == 0 ? "no certificate" : SmsDock.formatDigest(actual));
            throw new CertificateException("The certificate does not match the pinned one");
        }
    }

    @Override
    public void checkServerTrusted(X509Certificate[] chain, String authType, Socket socket)
            throws CertificateException {
        check(chain);
    }

    @Override
    public void checkServerTrusted(X509Certificate[] chain, String authType, SSLEngine engine)
            throws CertificateException {
        check(chain);
    }

    @Override
    public void checkServerTrusted(X509Certificate[] chain, String authType)
            throws CertificateException {
        check(chain);
    }

    @Override
    public void checkClientTrusted(X509Certificate[] chain, String authType, Socket socket)
            throws CertificateException {
        throw new CertificateException("This trust manager is for clients only");
    }

    @Override
    public void checkClientTrusted(X509Certificate[] chain, String authType, SSLEngine engine)
            throws CertificateException {
        throw new CertificateException("This trust manager is for clients only");
    }

    @Override
    public void checkClientTrusted(X509Certificate[] chain, String authType)
            throws CertificateException {
        throw new CertificateException("This trust manager is for clients only");
    }

    @Override
    public X509Certificate[] getAcceptedIssuers() {
        return new X509Certificate[0];
    }
}
