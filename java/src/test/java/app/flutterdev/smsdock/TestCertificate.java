package app.flutterdev.smsdock;

import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.GeneralSecurityException;
import java.security.KeyStore;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;

/**
 * A throwaway self-signed EC certificate like the one of the phone, made with the
 * {@code keytool} command of the JDK.
 *
 * <p>The fingerprint is the one {@code keytool} prints, so that the tests compare the client
 * with an independent implementation.
 */
final class TestCertificate {

    private static final char[] SECRET = "changeit".toCharArray();
    private static final Pattern FINGERPRINT =
        Pattern.compile("(?:[0-9A-F]{2}:){31}[0-9A-F]{2}");

    private static TestCertificate server;
    private static TestCertificate other;

    private final SSLContext context;
    private final String fingerprint;
    private final byte[] der;

    private TestCertificate(SSLContext context, String fingerprint, byte[] der) {
        this.context = context;
        this.fingerprint = fingerprint;
        this.der = der;
    }

    /** Certificate of the fake server; made once per test run. */
    static synchronized TestCertificate server() {
        if (server == null) {
            server = create("server");
        }
        return server;
    }

    /** A second certificate that the client does not pin. */
    static synchronized TestCertificate other() {
        if (other == null) {
            other = create("other");
        }
        return other;
    }

    /** The TLS context of a server that presents this certificate. */
    SSLContext serverContext() {
        return context;
    }

    /** SHA-256 fingerprint as printed by keytool: {@code AB:CD:…}. */
    String fingerprint() {
        return fingerprint;
    }

    /** The certificate in DER form. */
    byte[] der() {
        return der.clone();
    }

    private static TestCertificate create(String name) {
        try {
            Path directory = Files.createTempDirectory("smsdock-test-tls");
            Path store = directory.resolve(name + ".p12");
            try {
                String keytool = Path.of(System.getProperty("java.home"), "bin", "keytool")
                    .toString();
                run(keytool, "-genkeypair", "-alias", name, "-keyalg", "EC",
                    "-groupname", "secp256r1", "-sigalg", "SHA256withECDSA",
                    "-dname", "CN=SMS Dock", "-validity", "2", "-storetype", "PKCS12",
                    "-keystore", store.toString(), "-storepass", new String(SECRET));
                String listing = run(keytool, "-list", "-alias", name,
                    "-keystore", store.toString(), "-storepass", new String(SECRET));
                Matcher matcher = FINGERPRINT.matcher(listing);
                if (!matcher.find()) {
                    throw new IOException("keytool printed no SHA-256 fingerprint");
                }
                KeyStore keys = KeyStore.getInstance("PKCS12");
                try (InputStream stream = Files.newInputStream(store)) {
                    keys.load(stream, SECRET);
                }
                KeyManagerFactory factory =
                    KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
                factory.init(keys, SECRET);
                SSLContext context = SSLContext.getInstance("TLS");
                context.init(factory.getKeyManagers(), null, null);
                return new TestCertificate(
                    context, matcher.group(), keys.getCertificate(name).getEncoded());
            } finally {
                Files.deleteIfExists(store);
                Files.deleteIfExists(directory);
            }
        } catch (IOException | GeneralSecurityException error) {
            throw new IllegalStateException(
                "Cannot create a throwaway certificate with keytool", error);
        }
    }

    private static String run(String... command) throws IOException {
        Process process = new ProcessBuilder(List.of(command)).redirectErrorStream(true).start();
        try {
            String output = new String(
                process.getInputStream().readAllBytes(), StandardCharsets.UTF_8);
            if (!process.waitFor(60, TimeUnit.SECONDS) || process.exitValue() != 0) {
                process.destroyForcibly();
                throw new IOException("keytool failed: " + output);
            }
            return output;
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new IOException(error);
        }
    }
}
