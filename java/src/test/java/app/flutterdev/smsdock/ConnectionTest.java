package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.PrintStream;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.atomic.AtomicInteger;
import javax.net.ssl.SSLServerSocket;
import javax.net.ssl.SSLSocket;
import org.junit.jupiter.api.Test;

/** Login and the check of the certificate. */
class ConnectionTest extends ServerTestBase {

    @Test
    void logsIn() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        assertTrue(client.isConnected());
        assertEquals("SMS Dock", client.getServerInfo().get("name"));
        assertEquals(1L, client.getServerInfo().get("protocolVersion"));
        assertEquals(1, client.getConnectionCount());
        assertEquals(1, server.loginCount());
        assertEquals(List.of("authenticate"), server.actions());
        assertEquals(server.epoch(), client.getEpoch());
        assertEquals(0L, client.getLatestSeq());
        assertTrue(client.toString().contains("connected"));
    }

    @Test
    void tryWithResourcesCloses() {
        FakeSmsDock server = startServer();
        SmsDockClient kept;
        try (SmsDockClient client = options(server).connect()) {
            kept = client;
            assertTrue(client.isConnected());
            assertTrue(waitUntil(() -> server.clientCount() == 1));
        }
        assertFalse(kept.isConnected());
        assertTrue(waitUntil(() -> server.clientCount() == 0));
    }

    @Test
    void firstRequestConnects() {
        FakeSmsDock server = startServer();
        SmsDockClient client = track(options(server).build());
        assertFalse(client.isConnected());
        assertEquals("hidden", client.status().get("deleteMode"));
        assertTrue(client.isConnected());
    }

    @Test
    void wrongPassword() {
        FakeSmsDock server = startServer();
        SmsDockClient client = track(options(server).password("wrong-password").build());
        AuthenticationException error =
            assertThrows(AuthenticationException.class, client::connect);
        assertEquals("invalid_password", error.getCode());
        assertFalse(error.getMessage().contains("wrong-password"));
        assertFalse(client.isConnected());
        assertEquals(1, server.loginCount());
    }

    @Test
    void wrongPasswordIsNotRetriedByRequests() {
        FakeSmsDock server = startServer();
        SmsDockClient client = track(options(server).password("wrong-password").build());
        assertThrows(AuthenticationException.class, client::status);
        assertEquals(1, server.loginCount());
        assertEquals(1, client.getConnectionCount());
    }

    @Test
    void lockedOut() {
        FakeSmsDock server = startServer();
        server.lockLogins(30);
        AuthenticationException error = assertThrows(AuthenticationException.class,
            () -> connect(server));
        assertEquals("locked_out", error.getCode());
        assertTrue(error.getRetryAfter() >= 29 && error.getRetryAfter() <= 30);
        assertEquals(1, server.loginCount());
    }

    @Test
    void licenceRequired() {
        FakeSmsDock server = newServer();
        server.licenceEnforced = true;
        server.begin();
        LicenceRequiredException error = assertThrows(LicenceRequiredException.class,
            () -> connect(server));
        assertEquals("licence_required", error.getCode());
        assertEquals(1, server.loginCount());
    }

    @Test
    void refusesOtherProtocolVersions() {
        FakeSmsDock server = newServer();
        server.protocolVersion = 2;
        server.begin();
        ProtocolException error = assertThrows(ProtocolException.class, () -> connect(server));
        assertTrue(error.getMessage().contains("protocol version 2"));
        assertEquals(0, server.loginCount());
    }

    @Test
    void closeCode4401WhileLoggingIn() {
        FakeSmsDock server = newServer();
        server.authTimeoutMillis = 200;
        server.begin();
        server.ignoreNext("authenticate", 1);
        AuthenticationException error = assertThrows(AuthenticationException.class,
            () -> connect(server));
        assertEquals("not_authenticated", error.getCode());
        assertEquals(4401L, error.getDetails().get("closeCode"));
        assertEquals(1, server.connectionCount());
    }

    @Test
    void tooManyClients() {
        FakeSmsDock server = newServer();
        server.maxClients = 1;
        server.begin();
        connect(server);
        ConnectionInterruptedException error = assertThrows(
            ConnectionInterruptedException.class, () -> connect(server));
        assertEquals(4409, error.getCloseCode());
        assertEquals("too many clients", error.getCloseReason());
        assertTrue(error.getMessage().contains("4409"));
    }

    @Test
    void nothingListens() {
        FakeSmsDock server = startServer();
        String url = server.url();
        server.end();
        SmsDockClient client = track(SmsDockClient.builder(url).environment(name -> null)
            .password(PASSWORD).fingerprint(server.fingerprint())
            .connectTimeout(Duration.ofSeconds(2)).build());
        ConnectionInterruptedException error =
            assertThrows(ConnectionInterruptedException.class, client::connect);
        assertTrue(error.getMessage().startsWith("Cannot connect to " + url));
        assertNotNull(error.getCause());
        assertEquals(null, error.getCloseCode());
    }

    // ------------------------------------------------------------------
    // Certificate
    // ------------------------------------------------------------------

    @Test
    void pinsTheCertificate() {
        FakeSmsDock server = startServer();
        // The fingerprint comes from keytool, an independent implementation.
        assertEquals(server.fingerprint(),
            SmsDock.certificateFingerprint(TestCertificate.server().der()));
        String plain = server.fingerprint().replace(":", "").toLowerCase();
        SmsDockClient client = track(options(server).fingerprint(plain).build()).connect();
        assertEquals("hidden", client.status().get("deleteMode"));
    }

    @Test
    void certificateMismatch() {
        FakeSmsDock server = startServer();
        String pinned = TestCertificate.other().fingerprint();
        SmsDockClient client = track(options(server).fingerprint(pinned).build());
        List<Long> pauses = new ArrayList<>();
        client.setSleeper(pauses::add);
        CertificateMismatchException error =
            assertThrows(CertificateMismatchException.class, client::connect);
        assertEquals(pinned, error.getExpected());
        assertEquals(server.fingerprint(), error.getActual());
        assertTrue(error.getMessage().contains(pinned));
        assertTrue(error.getMessage().contains(server.fingerprint()));
        assertFalse(client.isConnected());
        // Not even the WebSocket request was sent, and a request does not try again.
        assertThrows(CertificateMismatchException.class, client::status);
        assertEquals(0, server.connectionCount());
        assertEquals(List.of(), server.requests());
        assertEquals(0, client.getConnectionCount());
        assertEquals(List.of(), pauses);
    }

    @Test
    void nothingIsSentToAServerWithAnotherCertificate() throws Exception {
        AtomicInteger received = new AtomicInteger(-1);
        try (SSLServerSocket listener = (SSLServerSocket) TestCertificate.other()
                .serverContext().getServerSocketFactory()
                .createServerSocket(0, 1, InetAddress.getLoopbackAddress())) {
            Thread thread = later(() -> {
                try (SSLSocket socket = (SSLSocket) listener.accept()) {
                    socket.setSoTimeout(3000);
                    socket.startHandshake();
                    received.set(Math.max(0, socket.getInputStream().read(new byte[4096])));
                } catch (IOException error) {
                    // The client gave up during or right after the handshake.
                    received.set(0);
                }
            });
            String url = "wss://127.0.0.1:" + listener.getLocalPort() + "/ws";
            SmsDockClient client = track(SmsDockClient.builder(url).environment(name -> null)
                .password(PASSWORD).fingerprint(TestCertificate.server().fingerprint())
                .build());
            CertificateMismatchException error =
                assertThrows(CertificateMismatchException.class, client::connect);
            assertEquals(TestCertificate.other().fingerprint(), error.getActual());
            thread.join(5000);
        }
        assertEquals(0, received.get(), "bytes of application data the server received");
    }

    @Test
    void newCertificateAfterReconnect() {
        FakeSmsDock server = startServer();
        int port = server.getPort();
        SmsDockClient client = connect(server);
        assertNotNull(client.ping().get("time"));
        server.end();
        FakeSmsDock impostor = newServer(TestCertificate.other(), port).begin();
        assertThrows(CertificateMismatchException.class, client::status);
        assertEquals(0, impostor.loginCount());
        assertEquals(List.of(), impostor.requests());
    }

    @Test
    void fetchFingerprint() {
        FakeSmsDock server = startServer();
        assertEquals(server.fingerprint(),
            SmsDock.fetchFingerprint("127.0.0.1", server.getPort(), Duration.ofSeconds(5)));
        assertEquals(List.of(), server.requests());
        int port = server.getPort();
        server.end();
        assertThrows(ConnectionInterruptedException.class,
            () -> SmsDock.fetchFingerprint("127.0.0.1", port, Duration.ofSeconds(2)));
    }

    @Test
    void insecureSkipsTheCheckAndWarns() {
        FakeSmsDock server = startServer();
        PrintStream original = System.err;
        ByteArrayOutputStream captured = new ByteArrayOutputStream();
        System.setErr(new PrintStream(captured, true, StandardCharsets.UTF_8));
        try {
            SmsDockClient client = track(SmsDockClient.builder(server.url())
                .environment(name -> null).password(PASSWORD).insecure(true)
                .fingerprint(TestCertificate.other().fingerprint()).build());
            client.connect();
            assertEquals("hidden", client.status().get("deleteMode"));
            client.connect();
        } finally {
            System.setErr(original);
        }
        String warning = captured.toString(StandardCharsets.UTF_8);
        assertTrue(warning.startsWith("smsdock: WARNING:"));
        assertTrue(warning.contains("not checked"));
        assertEquals(1, warning.lines().count());
        assertFalse(warning.contains(PASSWORD));
    }
}
