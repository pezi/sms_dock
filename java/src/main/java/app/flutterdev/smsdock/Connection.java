package app.flutterdev.smsdock;

import java.lang.reflect.Method;
import java.net.ConnectException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.WebSocket;
import java.net.http.WebSocketHandshakeException;
import java.nio.ByteBuffer;
import java.time.Duration;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.TimeoutException;
import javax.net.ssl.SSLParameters;

/**
 * One WebSocket connection over TLS, read and written by one thread at a time.
 *
 * <p>The JDK delivers frames on its own threads; this class queues them so that the client
 * can read them with a timeout. Pings of the server are answered by the JDK. While the
 * client waits for a frame and nothing arrives for {@link #pingIntervalNanos}, it sends a
 * ping of its own and gives the connection up when that is not answered in time.
 */
final class Connection implements WebSocket.Listener {

    /** Idle time after which the waiting client sends a ping. */
    static volatile long pingIntervalNanos = TimeUnit.SECONDS.toNanos(20);

    /** Time the server has to answer that ping. */
    static volatile long pingTimeoutNanos = TimeUnit.SECONDS.toNanos(20);

    private static final int HIGH_WATER = 1024;
    private static final int LOW_WATER = 256;
    private static final Object BINARY = new Object();

    private record Closed(int code, String reason) {
    }

    private record Broken(String message, Throwable cause) {
    }

    private final HttpClient http;
    private final LinkedBlockingQueue<Object> inbox = new LinkedBlockingQueue<>();
    private final CountDownLatch ended = new CountDownLatch(1);
    private final Object flowLock = new Object();
    private final StringBuilder partial = new StringBuilder();
    private boolean paused;
    private volatile WebSocket webSocket;
    private volatile Closed closed;
    private volatile long lastActivity = System.nanoTime();
    private boolean pingOutstanding;
    private long pingSentAt;
    private Object terminal;

    private Connection(HttpClient http) {
        this.http = http;
    }

    /**
     * Opens the TLS connection, checks the certificate and performs the WebSocket handshake.
     *
     * @param uri the {@code wss://} address
     * @param pin SHA-256 of the pinned certificate, or {@code null} to accept any
     * @param connectNanos longest time to wait
     * @throws CertificateMismatchException if the server presented another certificate
     * @throws TransportFailure if the connection cannot be opened
     */
    static Connection open(URI uri, byte[] pin, long connectNanos) {
        PinningTrustManager trust = new PinningTrustManager(pin);
        SSLParameters parameters = new SSLParameters();
        parameters.setProtocols(new String[] {"TLSv1.3", "TLSv1.2"});
        Duration connectTimeout = Duration.ofNanos(connectNanos);
        // A client of its own per connection: every connection does a full TLS handshake,
        // so the trust manager sees the certificate each time.
        HttpClient http = HttpClient.newBuilder()
            .version(HttpClient.Version.HTTP_1_1)
            .proxy(HttpClient.Builder.NO_PROXY)
            .sslContext(trust.context())
            .sslParameters(parameters)
            .connectTimeout(connectTimeout)
            .build();
        Connection connection = new Connection(http);
        CompletableFuture<WebSocket> opening = http.newWebSocketBuilder()
            .connectTimeout(connectTimeout)
            .buildAsync(uri, connection);
        try {
            connection.webSocket = opening.get(connectNanos, TimeUnit.NANOSECONDS);
            return connection;
        } catch (ExecutionException error) {
            shutdown(http);
            if (trust.mismatch() != null) {
                throw trust.mismatch();
            }
            Throwable cause = error.getCause() == null ? error : error.getCause();
            throw new TransportFailure(describe(cause), cause);
        } catch (TimeoutException error) {
            opening.cancel(true);
            shutdown(http);
            if (trust.mismatch() != null) {
                throw trust.mismatch();
            }
            throw new TransportFailure("timed out while connecting", error);
        } catch (InterruptedException error) {
            opening.cancel(true);
            shutdown(http);
            Thread.currentThread().interrupt();
            throw new ConnectionInterruptedException(
                "Interrupted while connecting", null, null, error);
        }
    }

    /**
     * Sends one text frame.
     *
     * @throws TransportFailure if the connection is lost
     */
    void send(String frame, long timeoutNanos) {
        try {
            webSocket.sendText(frame, true).get(timeoutNanos, TimeUnit.NANOSECONDS);
        } catch (ExecutionException | IllegalStateException error) {
            Throwable cause = error instanceof ExecutionException && error.getCause() != null
                ? error.getCause() : error;
            throw failure(describe(cause), cause);
        } catch (TimeoutException error) {
            throw failure("timed out while sending", error);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new ConnectionInterruptedException(
                "Interrupted while sending", null, null, error);
        }
    }

    /**
     * Returns the next text frame.
     *
     * @param timeoutNanos longest time to wait; {@code null} waits without limit
     * @throws TransportFailure.ReceiveTimeout if nothing arrived in time
     * @throws TransportFailure if the connection is lost or was closed by the server
     * @throws ProtocolException if the server sent a binary frame
     */
    String receive(Long timeoutNanos) {
        long started = System.nanoTime();
        while (true) {
            if (terminal != null && inbox.isEmpty()) {
                throw ended();
            }
            long wait = Long.MAX_VALUE;
            if (timeoutNanos != null) {
                wait = timeoutNanos - (System.nanoTime() - started);
                if (wait <= 0 && inbox.isEmpty()) {
                    throw new TransportFailure.ReceiveTimeout("timed out");
                }
            }
            Object item;
            try {
                item = inbox.poll(
                    Math.max(0, Math.min(wait, untilKeepalive())), TimeUnit.NANOSECONDS);
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
                throw new ConnectionInterruptedException(
                    "Interrupted while waiting", null, null, error);
            }
            if (item == null) {
                keepAlive();
                continue;
            }
            resume();
            if (item instanceof String) {
                return (String) item;
            }
            if (item == BINARY) {
                throw new ProtocolException("The server sent a binary frame");
            }
            terminal = item;
        }
    }

    /** Closes the connection: a close frame when the server still listens, then the socket. */
    void close(long timeoutNanos) {
        WebSocket socket = webSocket;
        if (socket != null) {
            if (terminal == null && ended.getCount() > 0) {
                try {
                    socket.sendClose(WebSocket.NORMAL_CLOSURE, "");
                    ended.await(timeoutNanos, TimeUnit.NANOSECONDS);
                } catch (InterruptedException error) {
                    Thread.currentThread().interrupt();
                } catch (RuntimeException error) {
                    // The connection is gone already.
                }
            }
            socket.abort();
        }
        shutdown(http);
    }

    /** Returns a short text for an error of the transport. */
    static String describe(Throwable error) {
        if (error instanceof WebSocketHandshakeException) {
            int status = ((WebSocketHandshakeException) error).getResponse().statusCode();
            return "the server refused the WebSocket (HTTP " + status + ")";
        }
        String message = error.getMessage();
        if (error instanceof ConnectException && (message == null || message.isBlank())) {
            return "Connection refused";
        }
        if (message == null || message.isBlank()) {
            Throwable cause = error.getCause();
            if (cause != null && cause != error && cause.getMessage() != null) {
                return cause.getMessage();
            }
            return error.getClass().getSimpleName();
        }
        return message;
    }

    private TransportFailure failure(String message, Throwable cause) {
        Closed frame = closed;
        if (frame != null) {
            return new TransportFailure(message, frame.code(), frame.reason(), cause);
        }
        return new TransportFailure(message, cause);
    }

    private TransportFailure ended() {
        if (terminal instanceof Closed) {
            Closed frame = (Closed) terminal;
            return new TransportFailure(
                "closed by the server", frame.code(), frame.reason(), null);
        }
        Broken broken = (Broken) terminal;
        return failure(broken.message(), broken.cause());
    }

    private long untilKeepalive() {
        long now = System.nanoTime();
        long next = pingOutstanding
            ? pingSentAt + pingTimeoutNanos - now
            : lastActivity + pingIntervalNanos - now;
        return Math.max(TimeUnit.MILLISECONDS.toNanos(1), next);
    }

    private void keepAlive() {
        long now = System.nanoTime();
        if (pingOutstanding) {
            if (lastActivity - pingSentAt >= 0) {
                pingOutstanding = false;
            } else if (now - pingSentAt >= pingTimeoutNanos) {
                terminal = new Broken("the server does not answer pings", null);
                webSocket.abort();
            }
        } else if (now - lastActivity >= pingIntervalNanos && terminal == null) {
            try {
                webSocket.sendPing(ByteBuffer.allocate(0));
            } catch (RuntimeException error) {
                // A ping is on its way already, or the connection is gone.
            }
            pingOutstanding = true;
            pingSentAt = now;
        }
    }

    private void deliver(WebSocket socket, Object item) {
        inbox.add(item);
        synchronized (flowLock) {
            if (inbox.size() < HIGH_WATER) {
                socket.request(1);
            } else {
                paused = true;
            }
        }
    }

    private void resume() {
        synchronized (flowLock) {
            if (paused && inbox.size() < LOW_WATER) {
                paused = false;
                webSocket.request(1);
            }
        }
    }

    private void end(Object item) {
        inbox.add(item);
        ended.countDown();
    }

    @Override
    public void onOpen(WebSocket socket) {
        socket.request(1);
    }

    @Override
    public CompletionStage<?> onText(WebSocket socket, CharSequence data, boolean last) {
        lastActivity = System.nanoTime();
        partial.append(data);
        if (partial.length() > SmsDock.MAX_INCOMING_BYTES) {
            partial.setLength(0);
            end(new Broken("the server sent a frame that is too large", null));
            socket.abort();
        } else if (last) {
            String frame = partial.toString();
            partial.setLength(0);
            deliver(socket, frame);
        } else {
            socket.request(1);
        }
        return null;
    }

    @Override
    public CompletionStage<?> onBinary(WebSocket socket, ByteBuffer data, boolean last) {
        lastActivity = System.nanoTime();
        if (last) {
            deliver(socket, BINARY);
        } else {
            socket.request(1);
        }
        return null;
    }

    @Override
    public CompletionStage<?> onPing(WebSocket socket, ByteBuffer message) {
        lastActivity = System.nanoTime();
        socket.request(1);
        return null;
    }

    @Override
    public CompletionStage<?> onPong(WebSocket socket, ByteBuffer message) {
        lastActivity = System.nanoTime();
        socket.request(1);
        return null;
    }

    @Override
    public CompletionStage<?> onClose(WebSocket socket, int statusCode, String reason) {
        Closed frame = new Closed(statusCode, reason);
        closed = frame;
        end(frame);
        return null;
    }

    @Override
    public void onError(WebSocket socket, Throwable error) {
        end(new Broken(describe(error), error));
    }

    /** Stops the threads of the HTTP client; {@code shutdownNow} exists from Java 21 on. */
    private static void shutdown(HttpClient http) {
        try {
            Method method = HttpClient.class.getMethod("shutdownNow");
            method.invoke(http);
        } catch (ReflectiveOperationException | RuntimeException error) {
            // Java 17: the threads end when the client is collected.
        }
    }
}
