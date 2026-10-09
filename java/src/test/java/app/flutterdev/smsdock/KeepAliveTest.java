package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.io.InputStream;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.time.Duration;
import java.util.Base64;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import javax.net.ssl.SSLServerSocket;
import javax.net.ssl.SSLSocket;
import org.junit.jupiter.api.AfterEach;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

/** The pings of a client that waits and hears nothing. */
class KeepAliveTest extends ServerTestBase {

    private long interval;
    private long timeout;

    @BeforeEach
    void shortTimes() {
        interval = Connection.pingIntervalNanos;
        timeout = Connection.pingTimeoutNanos;
        Connection.pingIntervalNanos = TimeUnit.MILLISECONDS.toNanos(100);
        Connection.pingTimeoutNanos = TimeUnit.MILLISECONDS.toNanos(300);
    }

    @AfterEach
    void usualTimes() {
        Connection.pingIntervalNanos = interval;
        Connection.pingTimeoutNanos = timeout;
    }

    @Test
    void aQuietConnectionThatAnswersPingsIsKept() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        client.subscribe("sms.received");
        assertEquals(List.of(), list(client.events(Duration.ofMillis(900))));
        assertEquals(1, client.getConnectionCount());
        assertTrue(client.isConnected());
    }

    @Test
    void aServerThatAnswersNoPingsIsGivenUp() throws Exception {
        // A server that opens the WebSocket and then neither sends nor reads anything.
        try (SSLServerSocket listener = (SSLServerSocket) TestCertificate.server()
                .serverContext().getServerSocketFactory()
                .createServerSocket(0, 1, InetAddress.getLoopbackAddress())) {
            Thread thread = later(() -> {
                try (SSLSocket socket = (SSLSocket) listener.accept()) {
                    socket.getOutputStream().write(upgrade(socket.getInputStream()));
                    socket.getOutputStream().flush();
                    Thread.sleep(3000);
                } catch (IOException | InterruptedException error) {
                    // The test is over.
                }
            });
            SmsDockClient client = track(SmsDockClient
                .builder("wss://127.0.0.1:" + listener.getLocalPort() + "/ws")
                .environment(name -> null).password(PASSWORD)
                .fingerprint(TestCertificate.server().fingerprint())
                .timeout(Duration.ofSeconds(10)).build());
            long started = System.nanoTime();
            ConnectionInterruptedException error =
                assertThrows(ConnectionInterruptedException.class, client::connect);
            assertTrue(error.getMessage().contains("does not answer pings"), error.getMessage());
            assertTrue(System.nanoTime() - started < TimeUnit.SECONDS.toNanos(5));
            thread.interrupt();
        }
    }

    /** Reads the WebSocket request and returns the response that accepts it. */
    private static byte[] upgrade(InputStream input) throws IOException {
        StringBuilder request = new StringBuilder();
        while (request.indexOf("\r\n\r\n") < 0) {
            int value = input.read();
            if (value < 0) {
                throw new IOException("The client closed the connection");
            }
            request.append((char) value);
        }
        Matcher key = Pattern.compile("(?i)sec-websocket-key: *(\\S+)").matcher(request);
        if (!key.find()) {
            throw new IOException("No WebSocket request");
        }
        try {
            String accept = Base64.getEncoder().encodeToString(
                MessageDigest.getInstance("SHA-1").digest(
                    (key.group(1) + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")
                        .getBytes(StandardCharsets.ISO_8859_1)));
            return ("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\n"
                + "Connection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n")
                .getBytes(StandardCharsets.ISO_8859_1);
        } catch (java.security.NoSuchAlgorithmException error) {
            throw new IOException(error);
        }
    }
}
