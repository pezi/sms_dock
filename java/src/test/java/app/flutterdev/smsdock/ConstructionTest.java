package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.Duration;
import java.util.HexFormat;
import java.util.Map;
import java.util.function.Consumer;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

/** Options of the client and the fingerprint helpers; no server is needed. */
class ConstructionTest {

    private static final String URL = "wss://192.168.1.50:9443/ws";
    private static final String PASSWORD = "secret123";
    private static final String FINGERPRINT = "AB:".repeat(31) + "AB";

    private static SmsDockClient.Builder options() {
        return SmsDockClient.builder(URL)
            .environment(name -> null)
            .password(PASSWORD)
            .fingerprint(FINGERPRINT);
    }

    @ParameterizedTest
    @ValueSource(strings = {
        "ws://192.168.1.50:9443/ws",
        "http://192.168.1.50:9443/ws",
        "https://192.168.1.50:9443/ws",
        "192.168.1.50:9443",
        "wss:///ws",
        "",
    })
    void refusesEverythingButWss(String url) {
        IllegalArgumentException error = assertThrows(IllegalArgumentException.class,
            () -> SmsDockClient.builder(url).environment(name -> null)
                .password(PASSWORD).fingerprint(FINGERPRINT).build());
        assertTrue(error.getMessage().contains("wss://"));
    }

    @Test
    void readsThePasswordFromTheEnvironment() {
        Map<String, String> environment = Map.of("SMSDOCK_PASSWORD", "from-the-environment");
        SmsDockClient client = SmsDockClient.builder(URL)
            .environment(environment::get).fingerprint(FINGERPRINT).build();
        assertFalse(client.isConnected());
    }

    @Test
    void needsAPassword() {
        IllegalArgumentException error = assertThrows(IllegalArgumentException.class,
            () -> SmsDockClient.builder(URL).environment(name -> null)
                .fingerprint(FINGERPRINT).build());
        assertTrue(error.getMessage().contains("SMSDOCK_PASSWORD"));
        assertThrows(IllegalArgumentException.class, () -> options().password("").build());
    }

    @Test
    void needsAFingerprintUnlessInsecure() {
        IllegalArgumentException error = assertThrows(IllegalArgumentException.class,
            () -> SmsDockClient.builder(URL).environment(name -> null)
                .password(PASSWORD).build());
        assertTrue(error.getMessage().contains("SMSDOCK_FINGERPRINT"));
        assertNotNull(SmsDockClient.builder(URL).environment(name -> null)
            .password(PASSWORD).insecure(true).build());
    }

    @Test
    void readsTheFingerprintFromTheEnvironment() {
        Map<String, String> good = Map.of("SMSDOCK_FINGERPRINT", FINGERPRINT.toLowerCase());
        assertNotNull(SmsDockClient.builder(URL).environment(good::get)
            .password(PASSWORD).build());
        Map<String, String> bad = Map.of("SMSDOCK_FINGERPRINT", "AB:CD");
        IllegalArgumentException error = assertThrows(IllegalArgumentException.class,
            () -> SmsDockClient.builder(URL).environment(bad::get).password(PASSWORD).build());
        assertTrue(error.getMessage().contains("SHA-256"));
    }

    @ParameterizedTest
    @ValueSource(strings = {
        "", "AB:CD",
        "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz",
        "ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB",
        "ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB",
        "ABABABABABABABABABABABABABABABABABABABAB",
    })
    void refusesMalformedFingerprints(String fingerprint) {
        IllegalArgumentException error = assertThrows(IllegalArgumentException.class,
            () -> options().fingerprint(fingerprint).build());
        assertTrue(error.getMessage().contains("SHA-256"));
    }

    @Test
    void refusesImpossibleOptions() {
        assertRefused(builder -> builder.timeout(Duration.ZERO));
        assertRefused(builder -> builder.connectTimeout(Duration.ofSeconds(-1)));
        assertRefused(builder -> builder.reconnectAttempts(-1));
        assertRefused(builder -> builder.reconnectDelay(Duration.ofSeconds(-1)));
        assertRefused(builder -> builder.maxReconnectDelay(Duration.ofSeconds(-1)));
        assertRefused(builder -> builder.eventQueueSize(0));
        assertRefused(builder -> builder.rateLimitRetries(-1));
        assertNotNull(options().reconnectAttempts(0).reconnectWithoutLimit().build());
    }

    private static void assertRefused(Consumer<SmsDockClient.Builder> change) {
        SmsDockClient.Builder builder = options();
        change.accept(builder);
        assertThrows(IllegalArgumentException.class, builder::build);
    }

    @Test
    void fingerprintForms() throws Exception {
        String digits = "0123456789abcdef".repeat(4);
        StringBuilder colons = new StringBuilder();
        for (int index = 0; index < 64; index += 2) {
            colons.append(index == 0 ? "" : ":").append(digits, index, index + 2);
        }
        String upper = colons.toString().toUpperCase();
        assertEquals(digits, SmsDock.normalizeFingerprint(digits.toUpperCase()));
        assertEquals(digits, SmsDock.normalizeFingerprint(colons.toString()));
        assertEquals(digits, SmsDock.normalizeFingerprint(" " + upper + "\n"));
        assertEquals(digits, SmsDock.normalizeFingerprint("sha256 Fingerprint=" + upper));
        assertEquals(upper, SmsDock.formatFingerprint(digits));
        assertThrows(IllegalArgumentException.class, () -> SmsDock.normalizeFingerprint(null));

        byte[] certificate = "certificate".getBytes(StandardCharsets.UTF_8);
        String expected = HexFormat.of().formatHex(
            MessageDigest.getInstance("SHA-256").digest(certificate));
        assertEquals(SmsDock.formatFingerprint(expected),
            SmsDock.certificateFingerprint(certificate));
    }

    @Test
    void textDoesNotShowThePassword() {
        SmsDockClient client = options().password("very-secret").build();
        assertFalse(client.toString().contains("very-secret"));
        assertTrue(client.toString().contains(URL));
        assertEquals(URL, client.getUrl());
    }

    @Test
    void newClientKnowsNothingYet() {
        SmsDockClient client = options().build();
        assertFalse(client.isConnected());
        assertEquals(null, client.getServerInfo());
        assertEquals(null, client.getSubscription());
        assertEquals(null, client.getEpoch());
        assertEquals(null, client.getLatestSeq());
        assertEquals(0L, client.getMissedEvents());
        assertFalse(client.isServerRestarted());
        assertEquals(0, client.getPendingEvents());
        assertEquals(0, client.getDroppedEvents());
        assertEquals(0, client.getConnectionCount());
    }

    @Test
    void allErrorsShareABaseClass() {
        ApiException api = new ApiException("not_found", "Nothing.", Map.of("retryAfter", 30L));
        assertEquals("not_found: Nothing.", api.getMessage());
        assertEquals("not_found", api.getCode());
        assertEquals("Nothing.", api.getServerMessage());
        assertEquals(30, api.getRetryAfter());
        assertEquals(null, api.getPermission());
        assertEquals("sendSms", new ApiException("permission_missing", "x",
            Map.of("permission", "sendSms")).getPermission());
        assertTrue(SmsDockException.class.isAssignableFrom(ApiException.class));
        assertTrue(ApiException.class.isAssignableFrom(AuthenticationException.class));
        assertTrue(ApiException.class.isAssignableFrom(LicenceRequiredException.class));
        assertTrue(SmsDockException.class.isAssignableFrom(ProtocolException.class));
        assertTrue(SmsDockException.class.isAssignableFrom(ConnectionInterruptedException.class));
        assertTrue(SmsDockException.class.isAssignableFrom(CertificateMismatchException.class));
        CertificateMismatchException mismatch = new CertificateMismatchException("AA", "BB");
        assertEquals("AA", mismatch.getExpected());
        assertEquals("BB", mismatch.getActual());
        assertTrue(mismatch.getMessage().contains("expected: AA"));
    }
}
