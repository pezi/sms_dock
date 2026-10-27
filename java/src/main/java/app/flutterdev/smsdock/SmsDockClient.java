package app.flutterdev.smsdock;

import java.io.IOException;
import java.net.URI;
import java.net.URISyntaxException;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collection;
import java.util.HashMap;
import java.util.HashSet;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.NoSuchElementException;
import java.util.Set;
import java.util.TreeSet;
import java.util.UUID;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;
import java.util.function.Function;
import java.util.function.Predicate;
import java.util.regex.Pattern;

/**
 * One authenticated connection to SMS Dock: a synchronous, reconnecting client for the
 * WebSocket API described in {@code API.md}.
 *
 * <p>The client pins the certificate of the phone by its SHA-256 fingerprint, logs in,
 * reconnects after a lost connection, restores the subscription and tells what it missed
 * meanwhile. All methods block. Results are plain JSON values as the server sent them:
 * objects are {@code Map<String, Object>}, arrays {@code List<Object>}, integers
 * {@code Long}; their keys are described in {@code API.md}.
 *
 * <p><b>The client is not thread-safe.</b> Use one client per thread.
 *
 * <pre>{@code
 * // Password and fingerprint come from SMSDOCK_PASSWORD and SMSDOCK_FINGERPRINT.
 * try (SmsDockClient dock = new SmsDockClient("wss://192.168.1.50:9443/ws").connect()) {
 *     System.out.println(dock.status());
 * }
 * }</pre>
 *
 * <p>Wrong arguments raise {@link IllegalArgumentException} before anything is sent.
 */
public final class SmsDockClient implements AutoCloseable {

    private static final Pattern REQUEST_ID_PATTERN = Pattern.compile("[A-Za-z0-9._-]{1,64}");
    private static final Pattern GROUP_REQUEST_ID_PATTERN =
        Pattern.compile("[A-Za-z0-9._-]{1,61}");
    private static final int MAX_BACKOFF_EXPONENT = 16;
    private static final long FIRST_POLL_NANOS = TimeUnit.MILLISECONDS.toNanos(100);
    private static final long MAX_NANOS = Long.MAX_VALUE / 4;
    private static final Set<String> OUTCOME_EVENTS =
        Set.of("sms.sent", "sms.delivered", "sms.failed");

    /** Pauses the thread; replaced in tests. */
    @FunctionalInterface
    interface Sleeper {
        void sleep(long nanos) throws InterruptedException;
    }

    private final String url;
    private final URI uri;
    private final String password;
    private final byte[] fingerprint;
    private final boolean insecure;
    private final long timeoutNanos;
    private final long connectTimeoutNanos;
    private final Integer reconnectAttempts;
    private final long reconnectDelayNanos;
    private final long maxReconnectDelayNanos;
    private final int eventQueueSize;
    private final int rateLimitRetries;
    private Consumer<SmsDockClient> onReconnect;
    private Sleeper sleeper = TimeUnit.NANOSECONDS::sleep;

    private Connection connection;
    private final ArrayDeque<Map<String, Object>> events = new ArrayDeque<>();
    private final Set<String> outstanding = new HashSet<>();
    private final Map<String, Map<String, Object>> responses = new HashMap<>();
    private long requestNumber;
    private List<String> subscribedEvents;
    private String subscribedAddress;
    private boolean warned;
    private Map<String, Object> serverInfo;
    private Long epoch;
    private Long latestSeq;
    private boolean restarted;
    private Long missed = 0L;
    private long droppedEvents;
    private int connectionCount;

    /**
     * Creates a client with the default options. Password and fingerprint are read from the
     * environment variables {@code SMSDOCK_PASSWORD} and {@code SMSDOCK_FINGERPRINT}.
     *
     * @param url the {@code wss://} address the app shows
     * @throws IllegalArgumentException if the URL, the password or the fingerprint is
     *     missing or malformed
     */
    public SmsDockClient(String url) {
        this(new Builder(url));
    }

    private SmsDockClient(Builder builder) {
        URI parsed;
        try {
            parsed = new URI(builder.url == null ? "" : builder.url);
        } catch (URISyntaxException error) {
            parsed = null;
        }
        if (parsed == null || !"wss".equalsIgnoreCase(parsed.getScheme())
                || parsed.getHost() == null || parsed.getHost().isEmpty()) {
            throw new IllegalArgumentException(
                "SMS Dock needs a wss:// URL, for example wss://192.168.1.50:9443/ws");
        }
        String secret = builder.password;
        if (secret == null) {
            secret = builder.environment.apply(SmsDock.PASSWORD_ENV);
        }
        if (secret == null || secret.isEmpty()) {
            throw new IllegalArgumentException(
                "No password: pass one or set the environment variable "
                    + SmsDock.PASSWORD_ENV);
        }
        String pin = builder.fingerprint;
        if (pin == null) {
            pin = builder.environment.apply(SmsDock.FINGERPRINT_ENV);
            if (pin != null && pin.isEmpty()) {
                pin = null;
            }
        }
        if (pin == null && !builder.insecure) {
            throw new IllegalArgumentException(
                "No certificate fingerprint: pass the fingerprint shown on the Server screen "
                    + "of the app or set the environment variable " + SmsDock.FINGERPRINT_ENV);
        }
        if (!positive(builder.timeout) || !positive(builder.connectTimeout)) {
            throw new IllegalArgumentException("Timeouts must be positive");
        }
        if (builder.reconnectAttempts != null && builder.reconnectAttempts < 0) {
            throw new IllegalArgumentException("reconnectAttempts cannot be negative");
        }
        if (builder.reconnectDelay == null || builder.reconnectDelay.isNegative()
                || builder.maxReconnectDelay == null || builder.maxReconnectDelay.isNegative()) {
            throw new IllegalArgumentException("Reconnect delays cannot be negative");
        }
        if (builder.eventQueueSize < 1) {
            throw new IllegalArgumentException("eventQueueSize must be at least 1");
        }
        if (builder.rateLimitRetries < 0) {
            throw new IllegalArgumentException("rateLimitRetries cannot be negative");
        }
        this.url = builder.url;
        this.uri = parsed;
        this.password = secret;
        this.insecure = builder.insecure;
        this.fingerprint = builder.insecure ? null : SmsDock.fingerprintBytes(pin);
        this.timeoutNanos = nanos(builder.timeout);
        this.connectTimeoutNanos = nanos(builder.connectTimeout);
        this.reconnectAttempts = builder.reconnectAttempts;
        this.reconnectDelayNanos = nanos(builder.reconnectDelay);
        this.maxReconnectDelayNanos = nanos(builder.maxReconnectDelay);
        this.eventQueueSize = builder.eventQueueSize;
        this.rateLimitRetries = builder.rateLimitRetries;
        this.onReconnect = builder.onReconnect;
    }

    /**
     * Starts the options of a client.
     *
     * @param url the {@code wss://} address the app shows, for example
     *     {@code wss://192.168.1.50:9443/ws}; any other scheme is refused
     * @return a builder with the default options
     */
    public static Builder builder(String url) {
        return new Builder(url);
    }

    /**
     * Options of a {@link SmsDockClient}.
     *
     * <p>Defaults: timeout 30 s, connect timeout 10 s, 5 reconnect attempts with a delay of
     * 1 s that doubles up to 10 s, an event queue of 1000 events, 2 repeats after
     * {@code rate_limited}.
     */
    public static final class Builder {

        private final String url;
        private String password;
        private String fingerprint;
        private boolean insecure;
        private Duration timeout = Duration.ofSeconds(30);
        private Duration connectTimeout = Duration.ofSeconds(10);
        private Integer reconnectAttempts = 5;
        private Duration reconnectDelay = Duration.ofSeconds(1);
        private Duration maxReconnectDelay = Duration.ofSeconds(10);
        private int eventQueueSize = 1000;
        private int rateLimitRetries = 2;
        private Consumer<SmsDockClient> onReconnect;
        private Function<String, String> environment = System::getenv;

        private Builder(String url) {
            this.url = url;
        }

        /**
         * Sets the password set in the app. Without it the environment variable
         * {@code SMSDOCK_PASSWORD} is read. Do not write the password into the source code.
         *
         * @param password the password
         * @return this builder
         */
        public Builder password(String password) {
            this.password = password;
            return this;
        }

        /**
         * Sets the SHA-256 fingerprint of the certificate as shown on the Server screen of
         * the app: hex, with or without colons, in any case. Without it the environment
         * variable {@code SMSDOCK_FINGERPRINT} is read. The client compares it with the
         * certificate of the server during the TLS handshake and sends nothing if they
         * differ.
         *
         * @param fingerprint the fingerprint
         * @return this builder
         */
        public Builder fingerprint(String fingerprint) {
            this.fingerprint = fingerprint;
            return this;
        }

        /**
         * Skips the check of the certificate and prints a warning to standard error. For
         * tests only.
         *
         * @param insecure {@code true} to accept any certificate
         * @return this builder
         */
        public Builder insecure(boolean insecure) {
            this.insecure = insecure;
            return this;
        }

        /**
         * Sets the time to wait for a response.
         *
         * @param timeout a positive duration (default: 30 s)
         * @return this builder
         */
        public Builder timeout(Duration timeout) {
            this.timeout = timeout;
            return this;
        }

        /**
         * Sets the time to open the connection.
         *
         * @param connectTimeout a positive duration (default: 10 s)
         * @return this builder
         */
        public Builder connectTimeout(Duration connectTimeout) {
            this.connectTimeout = connectTimeout;
            return this;
        }

        /**
         * Sets how often the client reconnects after a lost connection.
         *
         * @param attempts number of attempts; 0 for none (default: 5)
         * @return this builder
         */
        public Builder reconnectAttempts(int attempts) {
            this.reconnectAttempts = attempts;
            return this;
        }

        /**
         * Lets the client reconnect without limit, which suits a program that listens for
         * days.
         *
         * @return this builder
         */
        public Builder reconnectWithoutLimit() {
            this.reconnectAttempts = null;
            return this;
        }

        /**
         * Sets the time to wait before the first reconnect attempt; it is doubled for each
         * further one.
         *
         * @param delay a duration that is not negative (default: 1 s)
         * @return this builder
         */
        public Builder reconnectDelay(Duration delay) {
            this.reconnectDelay = delay;
            return this;
        }

        /**
         * Sets the upper limit of the delay between reconnect attempts.
         *
         * @param delay a duration that is not negative (default: 10 s)
         * @return this builder
         */
        public Builder maxReconnectDelay(Duration delay) {
            this.maxReconnectDelay = delay;
            return this;
        }

        /**
         * Sets how many events are kept while nobody reads them. After that the oldest are
         * dropped and counted in {@link SmsDockClient#getDroppedEvents()}.
         *
         * @param size at least 1 (default: 1000)
         * @return this builder
         */
        public Builder eventQueueSize(int size) {
            this.eventQueueSize = size;
            return this;
        }

        /**
         * Sets how often a request is repeated after the error {@code rate_limited}, when
         * the server names the seconds to wait.
         *
         * @param retries not negative (default: 2)
         * @return this builder
         */
        public Builder rateLimitRetries(int retries) {
            this.rateLimitRetries = retries;
            return this;
        }

        /**
         * Sets the function called with the client after every reconnect, once the
         * subscription is restored. Use it to read what was missed, for example with
         * {@link SmsDockClient#iterMessages}.
         *
         * @param hook the function, or {@code null}
         * @return this builder
         */
        public Builder onReconnect(Consumer<SmsDockClient> hook) {
            this.onReconnect = hook;
            return this;
        }

        /** Replaces the environment variables; for tests. */
        Builder environment(Function<String, String> environment) {
            this.environment = environment;
            return this;
        }

        /**
         * Creates the client without connecting. The first request connects.
         *
         * @return the client
         * @throws IllegalArgumentException if an option is missing or impossible
         */
        public SmsDockClient build() {
            return new SmsDockClient(this);
        }

        /**
         * Creates the client, opens the connection and logs in.
         *
         * @return the connected client
         * @throws IllegalArgumentException if an option is missing or impossible
         * @throws SmsDockException see {@link SmsDockClient#connect()}
         */
        public SmsDockClient connect() {
            return build().connect();
        }
    }

    @Override
    public String toString() {
        return "SmsDockClient[" + url + ", " + (isConnected() ? "connected" : "closed") + "]";
    }

    // ------------------------------------------------------------------
    // Connection
    // ------------------------------------------------------------------

    /**
     * Returns the address of the API.
     *
     * @return the {@code wss://} URL
     */
    public String getUrl() {
        return url;
    }

    /**
     * Tells whether a connection is open as far as the client knows.
     *
     * @return {@code true} while connected
     */
    public boolean isConnected() {
        return connection != null;
    }

    /**
     * Returns {@code {"name", "version", "protocolVersion"}} of the app.
     *
     * @return the object, or {@code null} before the first login
     */
    public Map<String, Object> getServerInfo() {
        return serverInfo;
    }

    /**
     * Returns the subscription in force.
     *
     * @return {@code {"events": [...], "address": ...}}, or {@code null} without one
     */
    public Map<String, Object> getSubscription() {
        if (subscribedEvents == null) {
            return null;
        }
        Map<String, Object> subscription = new LinkedHashMap<>();
        subscription.put("events", new ArrayList<>(subscribedEvents));
        subscription.put("address", subscribedAddress);
        return subscription;
    }

    /**
     * Returns the number of events that wait in the queue.
     *
     * @return the number
     */
    public int getPendingEvents() {
        return events.size();
    }

    /**
     * Returns the number of events dropped because the event queue was full.
     *
     * @return the number
     */
    public long getDroppedEvents() {
        return droppedEvents;
    }

    /**
     * Returns the number of connections opened so far, reconnects included.
     *
     * @return the number
     */
    public int getConnectionCount() {
        return connectionCount;
    }

    /**
     * Returns the {@code epoch} of the last login. It identifies one run of the app.
     * Sequence numbers begin at 1 again when it changes.
     *
     * @return the epoch, or {@code null} before the first login
     */
    public Long getEpoch() {
        return epoch;
    }

    /**
     * Returns the sequence number of the newest event the client knows of: the
     * {@code latestSeq} of the last login or the {@code seq} of an event received since,
     * whichever is higher. The phone counts all events, so a client that subscribed to some
     * of them sees gaps.
     *
     * @return the sequence number, or {@code null} before the first login
     */
    public Long getLatestSeq() {
        return latestSeq;
    }

    /**
     * Tells whether the app was restarted before the last reconnect: the {@code epoch} of
     * the current connection differs from the one of the connection before. Events were
     * lost then, and their number is not known.
     *
     * @return {@code true} after a restart of the app
     */
    public boolean isServerRestarted() {
        return restarted;
    }

    /**
     * Returns the number of events of the phone during the last interruption, counted over
     * all events, subscribed or not.
     *
     * @return the number; 0 after the first connect; {@code null} when the app was
     *     restarted and the number is not known
     */
    public Long getMissedEvents() {
        return missed;
    }

    /**
     * Returns the function called after every reconnect.
     *
     * @return the function, or {@code null}
     */
    public Consumer<SmsDockClient> getOnReconnect() {
        return onReconnect;
    }

    /**
     * Sets the function called with the client after every reconnect, once the subscription
     * is restored.
     *
     * @param hook the function, or {@code null}
     */
    public void setOnReconnect(Consumer<SmsDockClient> hook) {
        this.onReconnect = hook;
    }

    /**
     * Opens the connection, checks the certificate and logs in. Makes one attempt.
     *
     * @return this client
     * @throws CertificateMismatchException if the server presented another certificate
     * @throws AuthenticationException if the login failed
     * @throws LicenceRequiredException if the app has no full licence
     * @throws ProtocolException if the server does not speak protocol version 1
     * @throws ConnectionInterruptedException if the server cannot be reached
     */
    public SmsDockClient connect() {
        close();
        try {
            open();
        } catch (TransportFailure failure) {
            close();
            throw finalError("Cannot connect to " + url, failure);
        }
        return this;
    }

    /** Closes the connection. Queued events are kept. */
    @Override
    public void close() {
        Connection current = connection;
        connection = null;
        outstanding.clear();
        responses.clear();
        if (current != null) {
            current.close(Math.min(timeoutNanos, TimeUnit.SECONDS.toNanos(5)));
        }
    }

    // ------------------------------------------------------------------
    // Requests
    // ------------------------------------------------------------------

    /**
     * Sends a request without fields; see
     * {@link #request(String, Map, boolean, Duration)}.
     *
     * @param action the action as named in {@code API.md}
     * @return the {@code result} of the response
     */
    public Map<String, Object> request(String action) {
        return request(action, null, true, null);
    }

    /**
     * Sends a request; see {@link #request(String, Map, boolean, Duration)}.
     *
     * @param action the action as named in {@code API.md}
     * @param parameters the fields of the action, or {@code null}
     * @return the {@code result} of the response
     */
    public Map<String, Object> request(String action, Map<String, ?> parameters) {
        return request(action, parameters, true, null);
    }

    /**
     * Sends one request and returns the {@code result} of its response.
     *
     * <p>With {@code retry} the request is sent again after a lost connection; pass
     * {@code false} for anything that must not happen twice. The same holds for the error
     * {@code rate_limited}: the client waits the seconds the server names and repeats the
     * request.
     *
     * @param action the action as named in {@code API.md}
     * @param parameters the fields of the action as named in {@code API.md}, or {@code null}
     * @param retry whether to repeat the request after a lost connection
     * @param timeout time to wait for the response; {@code null} for the timeout of the
     *     client
     * @return the {@code result} of the response
     * @throws ApiException if the server answers with an error
     * @throws ConnectionInterruptedException if the connection could not be restored
     */
    public Map<String, Object> request(
            String action, Map<String, ?> parameters, boolean retry, Duration timeout) {
        Map<String, Object> fields = fields(parameters);
        long wait = timeout == null || timeout.isZero() ? timeoutNanos : nanos(timeout);
        int attempt = 0;
        while (true) {
            try {
                if (connection == null) {
                    open();
                }
                return requestPatiently(action, fields, wait, retry ? rateLimitRetries : 0);
            } catch (TransportFailure failure) {
                String what = "performing '" + action + "'";
                if (!retry) {
                    close();
                    throw finalError("Connection interrupted while " + what, failure);
                }
                recover(what, failure, attempt);
                attempt++;
            }
        }
    }

    /**
     * Sends a request without fields and without waiting; see
     * {@link #sendRequest(String, Map)}.
     *
     * @param action the action as named in {@code API.md}
     * @return the id of the request
     */
    public String sendRequest(String action) {
        return sendRequest(action, null);
    }

    /**
     * Sends a request without waiting and returns its id.
     *
     * <p>Several requests can be on their way at once; the server may answer them in any
     * order. Fetch each response with {@link #waitForResponse}. These two methods do not
     * reconnect and do not repeat a request that was answered with {@code rate_limited}.
     *
     * @param action the action as named in {@code API.md}
     * @param parameters the fields of the action, or {@code null}
     * @return the id of the request
     * @throws ConnectionInterruptedException if the connection is lost
     */
    public String sendRequest(String action, Map<String, ?> parameters) {
        Map<String, Object> fields = fields(parameters);
        try {
            if (connection == null) {
                open();
            }
            return sendFrame(action, fields);
        } catch (TransportFailure failure) {
            close();
            throw finalError(
                "Connection interrupted while sending '" + action + "'", failure);
        }
    }

    /**
     * Returns the result of a request sent with {@link #sendRequest}, waiting for the
     * timeout of the client.
     *
     * @param requestId the id returned by {@link #sendRequest}
     * @return the {@code result} of the response
     */
    public Map<String, Object> waitForResponse(String requestId) {
        return waitForResponse(requestId, null);
    }

    /**
     * Returns the result of a request sent with {@link #sendRequest}.
     *
     * @param requestId the id returned by {@link #sendRequest}
     * @param timeout time to wait; {@code null} for the timeout of the client
     * @return the {@code result} of the response
     * @throws ApiException if the response is an error
     * @throws SmsDockTimeoutException if no response arrives in time
     * @throws ConnectionInterruptedException if the connection was lost
     * @throws IllegalArgumentException if no request with this id is open
     */
    public Map<String, Object> waitForResponse(String requestId, Duration timeout) {
        String key = String.valueOf(requestId);
        if (!outstanding.contains(key) && !responses.containsKey(key)) {
            throw new IllegalArgumentException(
                "No request with the id '" + requestId + "' is open");
        }
        long wait = timeout == null || timeout.isZero() ? timeoutNanos : nanos(timeout);
        try {
            return awaitResponse(key, "request " + key, deadline(wait));
        } catch (TransportFailure.ReceiveTimeout late) {
            throw new SmsDockTimeoutException(late.getMessage());
        } catch (TransportFailure failure) {
            close();
            throw finalError(
                "Connection interrupted while waiting for '" + key + "'", failure);
        }
    }

    /**
     * Returns phone, SIMs, permissions, licence and limits.
     *
     * @return the Status object of {@code API.md}
     */
    public Map<String, Object> status() {
        return request("status");
    }

    /**
     * Checks the connection.
     *
     * @return {@code {"time": milliseconds}} of the phone
     */
    public Map<String, Object> ping() {
        return request("ping");
    }

    /**
     * Returns the first 50 conversations, newest first.
     *
     * @return a page: {@code {"items": [Conversation, …], "nextCursor": String or null}}
     */
    public Map<String, Object> conversations() {
        return conversations(null, 50, null);
    }

    /**
     * Returns one page of conversations, newest first.
     *
     * @param filter which conversations; {@code null} for all
     * @param limit page size, 1 to 200
     * @param cursor the {@code nextCursor} of the previous page, or {@code null}
     * @return a page: {@code {"items": [Conversation, …], "nextCursor": String or null}}
     */
    public Map<String, Object> conversations(
            ConversationFilter filter, int limit, String cursor) {
        Map<String, Object> parameters =
            (filter == null ? new ConversationFilter() : filter).toParameters();
        parameters.putAll(paging(limit, cursor));
        return request("conversations.list", parameters);
    }

    /**
     * Yields all conversations, newest first, page after page.
     *
     * @param filter which conversations; {@code null} for all
     * @return the conversations; every iteration asks the server anew
     */
    public Iterable<Map<String, Object>> iterConversations(ConversationFilter filter) {
        return iterConversations(filter, 50);
    }

    /**
     * Yields all conversations, newest first, page after page.
     *
     * @param filter which conversations; {@code null} for all
     * @param pageSize the {@code limit} of each request, 1 to 200
     * @return the conversations; every iteration asks the server anew
     */
    public Iterable<Map<String, Object>> iterConversations(
            ConversationFilter filter, int pageSize) {
        return iterate("conversations.list",
            (filter == null ? new ConversationFilter() : filter).toParameters(),
            pageSize, null);
    }

    /**
     * Yields conversations, newest first, page after page.
     *
     * @param filter which conversations; {@code null} for all
     * @param pageSize the {@code limit} of each request, 1 to 200
     * @param maxItems the iteration ends after that many conversations
     * @return the conversations; every iteration asks the server anew
     */
    public Iterable<Map<String, Object>> iterConversations(
            ConversationFilter filter, int pageSize, int maxItems) {
        return iterate("conversations.list",
            (filter == null ? new ConversationFilter() : filter).toParameters(),
            pageSize, maxItems);
    }

    /**
     * Returns the newest 50 messages.
     *
     * @return a page: {@code {"items": [Message, …], "nextCursor": String or null}}
     */
    public Map<String, Object> messages() {
        return messages(null, 50, null);
    }

    /**
     * Returns the first page of the messages a filter selects, newest first.
     *
     * @param filter which messages; {@code null} for all
     * @param limit page size, 1 to 200
     * @return a page: {@code {"items": [Message, …], "nextCursor": String or null}}
     */
    public Map<String, Object> messages(MessageFilter filter, int limit) {
        return messages(filter, limit, null);
    }

    /**
     * Returns one page of messages, newest first.
     *
     * @param filter which messages; {@code null} for all
     * @param limit page size, 1 to 200
     * @param cursor the {@code nextCursor} of the previous page, or {@code null}
     * @return a page: {@code {"items": [Message, …], "nextCursor": String or null}}
     */
    public Map<String, Object> messages(MessageFilter filter, int limit, String cursor) {
        Map<String, Object> parameters =
            (filter == null ? new MessageFilter() : filter).toParameters();
        parameters.putAll(paging(limit, cursor));
        return request("messages.list", parameters);
    }

    /**
     * Yields all messages a filter selects, newest first, following {@code nextCursor}.
     *
     * @param filter which messages; {@code null} for all
     * @return the messages; every iteration asks the server anew
     */
    public Iterable<Map<String, Object>> iterMessages(MessageFilter filter) {
        return iterMessages(filter, 50);
    }

    /**
     * Yields all messages a filter selects, newest first, following {@code nextCursor}.
     *
     * @param filter which messages; {@code null} for all
     * @param pageSize the {@code limit} of each request, 1 to 200
     * @return the messages; every iteration asks the server anew
     */
    public Iterable<Map<String, Object>> iterMessages(MessageFilter filter, int pageSize) {
        return iterate("messages.list",
            (filter == null ? new MessageFilter() : filter).toParameters(), pageSize, null);
    }

    /**
     * Yields messages, newest first, following {@code nextCursor}. The pages are requested
     * as the iteration goes on.
     *
     * @param filter which messages; {@code null} for all
     * @param pageSize the {@code limit} of each request, 1 to 200
     * @param maxItems the iteration ends after that many messages
     * @return the messages; every iteration asks the server anew
     */
    public Iterable<Map<String, Object>> iterMessages(
            MessageFilter filter, int pageSize, int maxItems) {
        return iterate("messages.list",
            (filter == null ? new MessageFilter() : filter).toParameters(),
            pageSize, maxItems);
    }

    /**
     * Returns one message.
     *
     * @param messageId {@code id} of the message
     * @return the Message object
     * @throws ApiException with the code {@code not_found} if it does not exist
     */
    public Map<String, Object> getMessage(long messageId) {
        return request("messages.get", Map.of("messageId", messageId));
    }

    /**
     * Sends an SMS on the SMS SIM of the phone, with a random {@code requestId}; see
     * {@link #send(String, String, Long, String)}.
     *
     * @param to the phone number
     * @param body the text, at most 10 SMS parts
     * @return the SendResult: {@code {"requestId", "to", "parts", "status"}}
     */
    public Map<String, Object> send(String to, String body) {
        return send(to, body, null, null);
    }

    /**
     * Sends an SMS and returns the SendResult.
     *
     * <p>The message is on its way when this method returns; use {@link #waitForResult}
     * for the outcome.
     *
     * <p>{@code requestId} makes the request safe to repeat: the phone sends a message with
     * the same {@code requestId} only once and remembers the id for 30 days. A repeat
     * returns the result of the first request with the current {@code status}, which may be
     * {@code sent} already. A repeat never sends again, also not after the first attempt
     * failed: the status is {@code failed} then, and a new attempt needs a new
     * {@code requestId}. A repeat with another recipient or text fails with
     * {@code invalid_request}.
     *
     * <p>Without {@code requestId} the client creates a random one. If the connection drops
     * before the response arrives, the request is repeated with the same id.
     *
     * <p>In the result {@code to} is the number as it is sent, without spaces and other
     * ignored characters.
     *
     * @param to the phone number
     * @param body the text, at most 10 SMS parts
     * @param sim subscription id of the SIM; {@code null} for the SMS SIM of the phone
     * @param requestId your own id, 1 to 64 characters of {@code A-Z a-z 0-9 . _ -};
     *     {@code null} for a random one
     * @return the SendResult: {@code {"requestId", "to", "parts", "status"}}
     */
    public Map<String, Object> send(String to, String body, Long sim, String requestId) {
        if (to == null || to.isBlank()) {
            throw new IllegalArgumentException("'to' must be a phone number");
        }
        if (body == null || body.isEmpty()) {
            throw new IllegalArgumentException("'body' must be a text");
        }
        String identifier = requestId;
        if (identifier == null) {
            identifier = UUID.randomUUID().toString().replace("-", "");
        } else if (!REQUEST_ID_PATTERN.matcher(identifier).matches()) {
            throw new IllegalArgumentException(
                "requestId must be 1 to 64 characters of A-Z a-z 0-9 . _ -");
        }
        Map<String, Object> parameters = new LinkedHashMap<>();
        parameters.put("to", to);
        parameters.put("body", body);
        parameters.put("requestId", identifier);
        if (sim != null) {
            parameters.put("sim", sim);
        }
        return request("messages.send", parameters);
    }

    /**
     * Sends one text to several numbers on the SMS SIM of the phone, with a random
     * {@code requestId}; see {@link #sendGroup(List, String, Long, String)}.
     *
     * @param to 1 to 20 phone numbers
     * @param body the text, at most 10 SMS parts
     * @return the GroupSendResult: {@code {"requestId", "to", "parts", "status", "messages"}}
     */
    public Map<String, Object> sendGroup(List<String> to, String body) {
        return sendGroup(to, body, null, null);
    }

    /**
     * Sends one text to several numbers, one SMS each, and returns the GroupSendResult.
     *
     * <p>Every recipient gets a separate SMS and does not see the others; repeated numbers
     * are sent once. The result holds one SendResult per recipient in {@code messages},
     * in the order of {@code to}. Their request ids are {@code <requestId>.1},
     * {@code <requestId>.2} and so on; {@link #waitForResult} and {@link #messageStatus}
     * take them like those of single messages. {@code status} is the least advanced status
     * of the members, or {@code failed} when every member failed.
     *
     * <p>The phone checks the whole group before it sends anything: a number that is not a
     * phone number ({@code invalid_number}), a text that is too long, fewer messages left in
     * the daily limit of the free version than the group has recipients
     * ({@code quota_exceeded}) or more SMS parts than the 20 of one minute, parts times
     * recipients ({@code invalid_request}), refuse the whole group. A refusal by the phone
     * for one recipient does not stop the others; that member has the status
     * {@code failed}.
     *
     * <p>{@code requestId} works as for {@link #send(String, String, Long, String)}, for the
     * whole group: the same id with the same text and numbers is not sent again but answered
     * with the stored members and their current status.
     *
     * @param to 1 to 20 phone numbers
     * @param body the text, at most 10 SMS parts
     * @param sim subscription id of the SIM; {@code null} for the SMS SIM of the phone
     * @param requestId your own id of the group, 1 to 61 characters of
     *     {@code A-Z a-z 0-9 . _ -}; {@code null} for a random one
     * @return the GroupSendResult: {@code {"requestId", "to", "parts", "status", "messages"}}
     */
    public Map<String, Object> sendGroup(List<String> to, String body, Long sim,
            String requestId) {
        if (to == null || to.isEmpty()) {
            throw new IllegalArgumentException("'to' must be a list of phone numbers");
        }
        for (String number : to) {
            if (number == null || number.isBlank()) {
                throw new IllegalArgumentException("'to' must be a list of phone numbers");
            }
        }
        if (to.size() > SmsDock.MAX_GROUP_RECIPIENTS) {
            throw new IllegalArgumentException(
                "'to' holds more than " + SmsDock.MAX_GROUP_RECIPIENTS + " numbers");
        }
        if (body == null || body.isEmpty()) {
            throw new IllegalArgumentException("'body' must be a text");
        }
        String identifier = requestId;
        if (identifier == null) {
            identifier = UUID.randomUUID().toString().replace("-", "");
        } else if (!GROUP_REQUEST_ID_PATTERN.matcher(identifier).matches()) {
            throw new IllegalArgumentException(
                "requestId of a group must be 1 to 61 characters of A-Z a-z 0-9 . _ -");
        }
        Map<String, Object> parameters = new LinkedHashMap<>();
        parameters.put("to", new ArrayList<>(to));
        parameters.put("body", body);
        parameters.put("requestId", identifier);
        if (sim != null) {
            parameters.put("sim", sim);
        }
        return request("messages.sendGroup", parameters);
    }

    /**
     * Returns the outbox entry of a message sent through SMS Dock.
     *
     * @param requestId the {@code requestId} of {@link #send}
     * @return the Outbox entry of {@code API.md}
     */
    public Map<String, Object> messageStatus(String requestId) {
        Map<String, Object> parameters = new LinkedHashMap<>();
        parameters.put("requestId", requestId);
        return request("messages.status", parameters);
    }

    /**
     * Waits until a sent message succeeded or failed, for the timeout of the client; see
     * {@link #waitForResult(String, Duration, boolean, Duration)}.
     *
     * @param requestId the {@code requestId} of {@link #send}
     * @return the outbox entry with the status {@code sent}, {@code delivered} or
     *     {@code failed}
     */
    public Map<String, Object> waitForResult(String requestId) {
        return waitForResult(requestId, null, false, Duration.ofSeconds(1));
    }

    /**
     * Waits until a sent message succeeded or failed; see
     * {@link #waitForResult(String, Duration, boolean, Duration)}.
     *
     * @param requestId the {@code requestId} of {@link #send}
     * @param timeout longest time to wait; {@code null} for the timeout of the client
     * @return the outbox entry with the status {@code sent}, {@code delivered} or
     *     {@code failed}
     */
    public Map<String, Object> waitForResult(String requestId, Duration timeout) {
        return waitForResult(requestId, timeout, false, Duration.ofSeconds(1));
    }

    /**
     * Waits until a sent message succeeded or failed.
     *
     * <p>Returns the outbox entry once its {@code status} is {@code sent},
     * {@code delivered} or {@code failed}. With {@code delivered} the status {@code sent}
     * is not enough: the method waits for the delivery report, which some networks never
     * send. Check {@code status} and {@code errorCode} of the result; a failed message
     * raises no exception.
     *
     * <p>The method asks the server with {@code messages.status}: soon after the start,
     * then less often, at last every {@code pollInterval}. If the connection is subscribed
     * to {@code sms.sent}, {@code sms.delivered} and {@code sms.failed} it asks as soon as
     * such an event arrives. The events stay in the queue.
     *
     * @param requestId the {@code requestId} of {@link #send}
     * @param timeout longest time to wait; {@code null} for the timeout of the client
     * @param delivered whether to wait for the delivery report
     * @param pollInterval longest time between two questions, positive
     * @return the outbox entry
     * @throws SmsDockTimeoutException after the timeout
     */
    public Map<String, Object> waitForResult(
            String requestId, Duration timeout, boolean delivered, Duration pollInterval) {
        if (!positive(pollInterval)) {
            throw new IllegalArgumentException("pollInterval must be positive");
        }
        Set<String> done = delivered
            ? Set.of("delivered", "failed") : Set.of("sent", "delivered", "failed");
        long interval = nanos(pollInterval);
        long end = deadline(
            timeout == null || timeout.isZero() ? timeoutNanos : nanos(timeout));
        long pause = Math.min(FIRST_POLL_NANOS, interval);
        while (true) {
            Map<String, Object> entry = messageStatus(requestId);
            Object status = entry.get("status");
            if (status instanceof String && done.contains(status)) {
                return entry;
            }
            long remaining = end - System.nanoTime();
            if (remaining <= 0) {
                throw new SmsDockTimeoutException(
                    "No result for the message '" + requestId + "' in time; its status is '"
                        + status + "'");
            }
            waitForOutcomeEvent(requestId, deadline(Math.min(pause, remaining)));
            pause = Math.min(pause * 2, interval);
        }
    }

    /**
     * Deletes one message.
     *
     * <p>With mode {@code hidden} the message disappears from SMS Dock but stays on the
     * phone. If the connection drops after the server deleted the message, the repeated
     * request fails with {@code not_found}.
     *
     * @param messageId {@code id} of the message
     * @return {@code {"deleted": 1, "mode": …}}
     */
    public Map<String, Object> deleteMessage(long messageId) {
        return request("messages.delete", Map.of("messageId", messageId));
    }

    /**
     * Deletes a conversation.
     *
     * @param conversationId {@code id} of the conversation
     * @return {@code {"deleted": n, "mode": …}}; {@code deleted} is the number of messages
     */
    public Map<String, Object> deleteConversation(long conversationId) {
        return request("conversations.delete", Map.of("conversationId", conversationId));
    }

    /**
     * Marks all messages of a conversation as read. Android lets only the default SMS app
     * change messages; otherwise the error is {@code not_default_app}.
     *
     * @param conversationId {@code id} of the conversation
     * @return {@code {"ok": true}}
     */
    public Map<String, Object> markRead(long conversationId) {
        return request("conversations.markRead", Map.of("conversationId", conversationId));
    }

    /**
     * Returns the first 50 contacts, sorted by name.
     *
     * @return a page: {@code {"items": [Contact, …], "nextCursor": String or null}}
     */
    public Map<String, Object> contacts() {
        return contacts(null, 50, null);
    }

    /**
     * Returns one page of contacts, sorted by name.
     *
     * @param query searches names and numbers; {@code null} for all contacts
     * @param limit page size, 1 to 200
     * @param cursor the {@code nextCursor} of the previous page, or {@code null}
     * @return a page: {@code {"items": [Contact, …], "nextCursor": String or null}}
     */
    public Map<String, Object> contacts(String query, int limit, String cursor) {
        Map<String, Object> parameters = contactFilter(query);
        parameters.putAll(paging(limit, cursor));
        return request("contacts.list", parameters);
    }

    /**
     * Yields all contacts, sorted by name, page after page.
     *
     * @param query searches names and numbers; {@code null} for all contacts
     * @return the contacts; every iteration asks the server anew
     */
    public Iterable<Map<String, Object>> iterContacts(String query) {
        return iterContacts(query, 50);
    }

    /**
     * Yields all contacts, sorted by name, page after page.
     *
     * @param query searches names and numbers; {@code null} for all contacts
     * @param pageSize the {@code limit} of each request, 1 to 200
     * @return the contacts; every iteration asks the server anew
     */
    public Iterable<Map<String, Object>> iterContacts(String query, int pageSize) {
        return iterate("contacts.list", contactFilter(query), pageSize, null);
    }

    /**
     * Yields contacts, sorted by name, page after page.
     *
     * @param query searches names and numbers; {@code null} for all contacts
     * @param pageSize the {@code limit} of each request, 1 to 200
     * @param maxItems the iteration ends after that many contacts
     * @return the contacts; every iteration asks the server anew
     */
    public Iterable<Map<String, Object>> iterContacts(
            String query, int pageSize, int maxItems) {
        return iterate("contacts.list", contactFilter(query), pageSize, maxItems);
    }

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    /**
     * Asks for events of all numbers; see {@link #subscribe(Collection, String)}.
     *
     * @param events one event name or several, or {@code "*"} for all
     * @return the subscription now in force
     */
    public Map<String, Object> subscribe(String... events) {
        return subscribe(Arrays.asList(events), null);
    }

    /**
     * Asks for events and returns the subscription now in force.
     *
     * <p>The names are added to the subscription. {@code address} limits the {@code sms.*}
     * events to one number and replaces the limit set before; without it the limit is
     * removed. Numbers are compared as the phone does: {@code +43 699 1234567} and
     * {@code 0699 1234567} are the same.
     *
     * <p>In the result {@code *} is replaced by the names it stands for. The client
     * remembers the subscription and restores it after a reconnect.
     *
     * @param events one event name or several, or {@code "*"} for all
     * @param address deliver {@code sms.*} events only for this number; {@code null} for
     *     all numbers
     * @return the subscription now in force: {@code {"events": [...], "address": ...}}
     */
    public Map<String, Object> subscribe(Collection<String> events, String address) {
        Map<String, Object> parameters = new LinkedHashMap<>();
        parameters.put("events", eventNames(events));
        if (address != null) {
            parameters.put("address", address);
        }
        Map<String, Object> result = request("subscribe", parameters);
        rememberSubscription(result);
        return result;
    }

    /**
     * Stops events; see {@link #unsubscribe(Collection)}.
     *
     * @param events one event name or several, or {@code "*"} for all
     * @return the subscription now in force
     */
    public Map<String, Object> unsubscribe(String... events) {
        return unsubscribe(Arrays.asList(events));
    }

    /**
     * Stops events and returns the subscription now in force. When no name is left, the
     * limit to one number is removed too.
     *
     * @param events one event name or several, or {@code "*"} for all
     * @return the subscription now in force: {@code {"events": [...], "address": ...}}
     */
    public Map<String, Object> unsubscribe(Collection<String> events) {
        Map<String, Object> parameters = new LinkedHashMap<>();
        parameters.put("events", eventNames(events));
        Map<String, Object> result = request("unsubscribe", parameters);
        rememberSubscription(result);
        return result;
    }

    /**
     * Yields events as they arrive, without limit of time; see {@link #events(Duration)}.
     *
     * @return the events
     */
    public Iterable<Map<String, Object>> events() {
        return events(null);
    }

    /**
     * Yields events as they arrive.
     *
     * <p>Events that were queued while a request waited for its response come first.
     * Subscribe first, a new connection gets no events. After a lost connection the
     * iteration reconnects and goes on.
     *
     * @param timeout the longest time to wait for the next event; the iteration ends when
     *     it passes. {@code null} waits without limit.
     * @return the events; {@code hasNext()} of its iterator blocks until one arrives
     * @throws ConnectionInterruptedException (while iterating) when reconnecting fails
     */
    public Iterable<Map<String, Object>> events(Duration timeout) {
        Long wait = timeout == null ? null : Long.valueOf(nanos(timeout));
        return () -> new EventIterator(wait);
    }

    /**
     * Returns the next event with this name, waiting for the timeout of the client; see
     * {@link #waitForEvent(Collection, Duration, Predicate)}.
     *
     * @param name the event name
     * @return the event
     */
    public Map<String, Object> waitForEvent(String name) {
        return waitForEvent(List.of(name), null, null);
    }

    /**
     * Returns the next event with this name; see
     * {@link #waitForEvent(Collection, Duration, Predicate)}.
     *
     * @param name the event name
     * @param timeout longest time to wait; {@code null} for the timeout of the client
     * @return the event
     */
    public Map<String, Object> waitForEvent(String name, Duration timeout) {
        return waitForEvent(List.of(name), timeout, null);
    }

    /**
     * Returns the next event with one of these names. Other events stay in the queue.
     *
     * @param names one event name or several; {@code "*"} stands for all
     * @param timeout longest time to wait; {@code null} for the timeout of the client
     * @param predicate called with each candidate, returns {@code true} for the wanted
     *     one; {@code null} accepts the first
     * @return the event
     * @throws SmsDockTimeoutException after the timeout
     */
    public Map<String, Object> waitForEvent(
            Collection<String> names,
            Duration timeout,
            Predicate<Map<String, Object>> predicate) {
        Set<String> wanted = new TreeSet<>(eventNames(names));
        Predicate<Map<String, Object>> matches = event ->
            (wanted.contains("*") || wanted.contains(String.valueOf(event.get("event"))))
                && (predicate == null || predicate.test(event));
        Iterator<Map<String, Object>> queued = events.iterator();
        while (queued.hasNext()) {
            Map<String, Object> event = queued.next();
            if (matches.test(event)) {
                queued.remove();
                return event;
            }
        }
        long end = deadline(
            timeout == null || timeout.isZero() ? timeoutNanos : nanos(timeout));
        String label = String.join(", ", wanted);
        while (true) {
            Map<String, Object> event = readEvent(end, "waiting for " + label);
            if (event == null) {
                throw new SmsDockTimeoutException("No event " + label + " in time");
            }
            if (matches.test(event)) {
                return event;
            }
            queueEvent(event);
        }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    /** Replaces the pauses of the client; for tests. */
    void setSleeper(Sleeper sleeper) {
        this.sleeper = sleeper;
    }

    /** Connects, pins the certificate, logs in and restores the subscription. */
    private void open() {
        if (insecure && !warned) {
            warned = true;
            System.err.println(
                "smsdock: WARNING: the certificate of the server is not checked (insecure). "
                    + "Anyone on the network can read the password and the messages.");
        }
        // The certificate is compared inside the TLS handshake; see PinningTrustManager.
        connection = Connection.open(uri, fingerprint, connectTimeoutNanos);
        connectionCount++;
        boolean reconnected = epoch != null;
        try {
            handshake();
            if (reconnected && onReconnect != null) {
                onReconnect.accept(this);
            }
        } catch (RuntimeException | Error error) {
            close();
            throw error;
        }
    }

    private void handshake() {
        Map<String, Object> hello = receive(timeoutNanos);
        if (!"hello".equals(hello.get("event"))) {
            throw new ProtocolException("Expected 'hello', received something else");
        }
        if (!isVersion(hello.get("protocolVersion"))) {
            throw new ProtocolException(
                "The server speaks protocol version " + hello.get("protocolVersion")
                    + ", this client version " + SmsDock.API_PROTOCOL_VERSION);
        }
        Map<String, Object> login = new LinkedHashMap<>();
        login.put("password", password);
        Map<String, Object> result =
            requestPatiently("authenticate", login, timeoutNanos, rateLimitRetries);
        if (!isVersion(result.get("protocolVersion"))) {
            throw new ProtocolException(
                "The login answered with protocol version " + result.get("protocolVersion"));
        }
        Object app = result.get("app");
        serverInfo = app instanceof Map ? object(app, "app") : null;
        noteLogin(result.get("epoch"), result.get("latestSeq"));
        if (subscribedEvents != null && !subscribedEvents.isEmpty()) {
            Map<String, Object> parameters = new LinkedHashMap<>();
            parameters.put("events", new ArrayList<>(subscribedEvents));
            if (subscribedAddress != null) {
                parameters.put("address", subscribedAddress);
            }
            requestPatiently("subscribe", parameters, timeoutNanos, rateLimitRetries);
        }
    }

    /** Compares epoch and sequence number with the connection before. */
    private void noteLogin(Object newEpoch, Object latest) {
        if (!isInteger(newEpoch) || !isInteger(latest)) {
            throw new ProtocolException("The login answered without 'epoch' and 'latestSeq'");
        }
        long currentEpoch = ((Number) newEpoch).longValue();
        long currentSeq = ((Number) latest).longValue();
        if (epoch == null) {
            restarted = false;
            missed = 0L;
        } else if (currentEpoch != epoch) {
            restarted = true;
            missed = null;
        } else {
            long known = latestSeq == null ? 0 : latestSeq;
            restarted = false;
            missed = Math.max(0, currentSeq - known);
        }
        epoch = currentEpoch;
        latestSeq = currentSeq;
    }

    /**
     * Sends a request; waits and repeats it while it is rate limited. Only when the server
     * names the seconds to wait: the limit of messages per minute comes without them and is
     * reported at once.
     */
    private Map<String, Object> requestPatiently(
            String action, Map<String, Object> parameters, long timeout, int retries) {
        int refused = 0;
        while (true) {
            try {
                return requestOnce(action, parameters, timeout);
            } catch (ApiException error) {
                Integer wait = error.getRetryAfter();
                if (!"rate_limited".equals(error.getCode())
                        || wait == null
                        || TimeUnit.SECONDS.toNanos(wait) > timeout
                        || refused >= retries) {
                    throw error;
                }
                refused++;
                pause(TimeUnit.SECONDS.toNanos(Math.max(0, wait)));
            }
        }
    }

    /**
     * Closes after a failure and waits before the next attempt. Throws instead when the
     * failure is final or no attempt is left.
     */
    private void recover(String what, TransportFailure failure, int attempt) {
        close();
        SmsDockException fatal = fatalError(failure);
        if (fatal != null) {
            throw fatal;
        }
        if (reconnectAttempts != null && attempt >= reconnectAttempts) {
            throw interrupted("Connection interrupted while " + what, failure);
        }
        int exponent = Math.min(attempt, MAX_BACKOFF_EXPONENT);
        double delay = Math.min(
            (double) reconnectDelayNanos * (1L << exponent), (double) maxReconnectDelayNanos);
        pause((long) delay);
    }

    private void pause(long nanos) {
        try {
            sleeper.sleep(nanos);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            close();
            throw new ConnectionInterruptedException(
                "Interrupted while waiting", null, null, error);
        }
    }

    private Map<String, Object> requestOnce(
            String action, Map<String, Object> parameters, long timeout) {
        String key = sendFrame(action, parameters);
        return awaitResponse(key, "'" + action + "'", deadline(timeout));
    }

    private String sendFrame(String action, Map<String, Object> parameters) {
        Connection current = connection;
        if (current == null) {
            throw new ConnectionInterruptedException("No connection to SMS Dock");
        }
        requestNumber++;
        String key = "java-" + requestNumber;
        Map<String, Object> payload = new LinkedHashMap<>();
        payload.put("id", key);
        payload.put("action", action);
        payload.putAll(parameters);
        String frame = Json.compact(payload);
        if (frame.getBytes(StandardCharsets.UTF_8).length > SmsDock.MAX_FRAME_BYTES) {
            throw new IllegalArgumentException(
                "The request is larger than " + SmsDock.MAX_FRAME_BYTES / 1024 + " KiB");
        }
        current.send(frame, timeoutNanos);
        outstanding.add(key);
        return key;
    }

    private Map<String, Object> awaitResponse(String key, String label, long end) {
        while (true) {
            Map<String, Object> response = responses.remove(key);
            if (response == null) {
                long remaining = end - System.nanoTime();
                if (remaining <= 0) {
                    throw new TransportFailure.ReceiveTimeout(
                        "No response to " + label + " in time");
                }
                Map<String, Object> message;
                try {
                    message = receive(remaining);
                } catch (TransportFailure.ReceiveTimeout late) {
                    throw new TransportFailure.ReceiveTimeout(
                        "No response to " + label + " in time");
                }
                if (message.containsKey("event")) {
                    queueEvent(message);
                } else {
                    fileResponse(message);
                }
                continue;
            }
            outstanding.remove(key);
            if (Boolean.TRUE.equals(response.get("ok"))) {
                return object(response.get("result"), "result of " + label);
            }
            Object error = response.get("error");
            if (!Boolean.FALSE.equals(response.get("ok")) || !(error instanceof Map)) {
                // The response is not shown: it may hold the text of a message.
                throw new ProtocolException("Malformed response to " + label);
            }
            throw apiError(object(error, "error"));
        }
    }

    /** Keeps a response for the request that waits for it. */
    private void fileResponse(Map<String, Object> response) {
        Object identifier = response.get("id");
        String key = identifier == null ? null : String.valueOf(identifier);
        if (key == null && outstanding.size() == 1) {
            // The server could not read the id of the only open request.
            key = outstanding.iterator().next();
        }
        if (key != null && outstanding.contains(key)) {
            responses.put(key, response);
        }
        // Anything else answers a request nobody waits for any more.
    }

    private Map<String, Object> receive(Long timeout) {
        Connection current = connection;
        if (current == null) {
            throw new ConnectionInterruptedException("No connection to SMS Dock");
        }
        String frame = current.receive(timeout);
        Object decoded;
        try {
            decoded = Json.parse(frame);
        } catch (IOException error) {
            throw new ProtocolException("The server sent invalid JSON");
        }
        Map<String, Object> message = object(decoded, "frame");
        Object sequence = message.get("seq");
        if (message.containsKey("event") && isInteger(sequence)) {
            long number = ((Number) sequence).longValue();
            if (latestSeq == null || number > latestSeq) {
                latestSeq = number;
            }
        }
        return message;
    }

    private void queueEvent(Map<String, Object> event) {
        if (events.size() >= eventQueueSize) {
            events.pollFirst();
            droppedEvents++;
        }
        events.addLast(event);
    }

    /**
     * Reads from the connection until an event arrives. Returns the event without queueing
     * it, or {@code null} when the deadline passed. Reconnects when needed.
     */
    private Map<String, Object> readEvent(Long end, String what) {
        int attempt = 0;
        while (true) {
            try {
                if (connection == null) {
                    open();
                }
            } catch (TransportFailure failure) {
                recover(what, failure, attempt);
                attempt++;
                continue;
            }
            Long wait = null;
            if (end != null) {
                wait = end - System.nanoTime();
                if (wait <= 0) {
                    return null;
                }
            }
            Map<String, Object> message;
            try {
                message = receive(wait);
            } catch (TransportFailure.ReceiveTimeout late) {
                return null;
            } catch (TransportFailure failure) {
                recover(what, failure, attempt);
                attempt++;
                continue;
            }
            attempt = 0;
            if (message.containsKey("event")) {
                return message;
            }
            fileResponse(message);
        }
    }

    /** Returns at the deadline, or earlier when an outcome of the message arrives. */
    private void waitForOutcomeEvent(String requestId, long end) {
        while (true) {
            Map<String, Object> event = readEvent(end, "waiting for the result");
            if (event == null) {
                return;
            }
            queueEvent(event);
            Object data = event.get("data");
            if (OUTCOME_EVENTS.contains(String.valueOf(event.get("event")))
                    && data instanceof Map
                    && requestId != null
                    && requestId.equals(((Map<?, ?>) data).get("requestId"))) {
                return;
            }
        }
    }

    private Iterable<Map<String, Object>> iterate(
            String action, Map<String, Object> filters, int pageSize, Integer maxItems) {
        if (maxItems != null && maxItems < 0) {
            throw new IllegalArgumentException("maxItems cannot be negative");
        }
        paging(pageSize, null);
        return () -> new PageIterator(action, filters, pageSize, maxItems);
    }

    private void rememberSubscription(Map<String, Object> result) {
        Object names = result.get("events");
        if (!(names instanceof List)) {
            throw new ProtocolException("Malformed subscription");
        }
        List<String> remembered = new ArrayList<>();
        for (Object name : (List<?>) names) {
            remembered.add(String.valueOf(name));
        }
        Object address = result.get("address");
        subscribedEvents = remembered;
        subscribedAddress = address instanceof String ? (String) address : null;
    }

    /** Follows {@code nextCursor} from page to page, one request at a time. */
    private final class PageIterator implements Iterator<Map<String, Object>> {

        private final String action;
        private final Map<String, Object> filters;
        private final int pageSize;
        private final Integer maxItems;
        private Iterator<?> items;
        private Object nextCursor;
        private String cursor;
        private int count;
        private boolean finished;

        PageIterator(
                String action, Map<String, Object> filters, int pageSize, Integer maxItems) {
            this.action = action;
            this.filters = filters;
            this.pageSize = pageSize;
            this.maxItems = maxItems;
        }

        @Override
        public boolean hasNext() {
            while (!finished) {
                if (maxItems != null && count >= maxItems) {
                    finished = true;
                    break;
                }
                if (items != null) {
                    if (items.hasNext()) {
                        return true;
                    }
                    items = null;
                    if (nextCursor == null) {
                        finished = true;
                        break;
                    }
                    if (!(nextCursor instanceof String) || nextCursor.equals(cursor)) {
                        finished = true;
                        throw new ProtocolException(
                            action + " returned the cursor '" + nextCursor + "'");
                    }
                    cursor = (String) nextCursor;
                }
                int limit = maxItems == null ? pageSize : Math.min(pageSize, maxItems - count);
                Map<String, Object> parameters = new LinkedHashMap<>(filters);
                parameters.putAll(paging(limit, cursor));
                Map<String, Object> page = request(action, parameters);
                Object list = page.get("items");
                if (!(list instanceof List)) {
                    finished = true;
                    throw new ProtocolException(action + " returned no list of items");
                }
                items = ((List<?>) list).iterator();
                nextCursor = page.get("nextCursor");
            }
            return false;
        }

        @Override
        public Map<String, Object> next() {
            if (!hasNext()) {
                throw new NoSuchElementException();
            }
            count++;
            return object(items.next(), "item of " + action);
        }
    }

    /** Hands out queued events first, then reads from the connection. */
    private final class EventIterator implements Iterator<Map<String, Object>> {

        private final Long timeout;
        private Map<String, Object> next;
        private boolean finished;

        EventIterator(Long timeout) {
            this.timeout = timeout;
        }

        @Override
        public boolean hasNext() {
            if (next != null) {
                return true;
            }
            if (finished) {
                return false;
            }
            next = events.pollFirst();
            if (next == null) {
                Long end = timeout == null ? null : Long.valueOf(deadline(timeout));
                next = readEvent(end, "waiting for events");
            }
            finished = next == null;
            return !finished;
        }

        @Override
        public Map<String, Object> next() {
            if (!hasNext()) {
                throw new NoSuchElementException();
            }
            Map<String, Object> event = next;
            next = null;
            return event;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ApiException apiError(Map<String, Object> error) {
        Object rawCode = error.get("code");
        String code = rawCode == null ? "unknown" : String.valueOf(rawCode);
        Object rawMessage = error.get("message");
        String message = rawMessage == null || String.valueOf(rawMessage).isEmpty()
            ? "no message" : String.valueOf(rawMessage);
        Map<String, Object> details = new LinkedHashMap<>(error);
        details.remove("code");
        details.remove("message");
        switch (code) {
            case "invalid_password":
            case "locked_out":
            case "not_authenticated":
                return new AuthenticationException(code, message, details);
            case "licence_required":
                return new LicenceRequiredException(code, message, details);
            default:
                return new ApiException(code, message, details);
        }
    }

    /** Returns the error to raise for a close code that forbids a retry, or null. */
    private static SmsDockException fatalError(TransportFailure failure) {
        Integer code = failure.closeCode();
        if (code == null) {
            return null;
        }
        String reason = failure.closeReason();
        Map<String, Object> details = new LinkedHashMap<>();
        details.put("closeCode", Long.valueOf(code));
        SmsDockException fatal;
        switch (code) {
            case 4401:
                fatal = new AuthenticationException("not_authenticated",
                    reason != null ? reason
                        : "The server closed the connection: not logged in", details);
                break;
            case 4410:
                fatal = new AuthenticationException("not_authenticated",
                    reason != null ? reason : "The password was changed in the app", details);
                break;
            case 4403:
                fatal = new LicenceRequiredException("licence_required",
                    reason != null ? reason
                        : "The API needs the full licence of the app", details);
                break;
            case 1009:
                fatal = new ProtocolException(
                    "The server closed the connection: frame larger than 64 KiB");
                break;
            case 4400:
                fatal = new ProtocolException(
                    "The server closed the connection: protocol error"
                        + (reason != null ? " (" + reason + ")" : ""));
                break;
            default:
                return null;
        }
        fatal.initCause(failure);
        return fatal;
    }

    private static ConnectionInterruptedException interrupted(
            String message, TransportFailure failure) {
        Integer code = failure.closeCode();
        String reason = failure.closeReason();
        String cause;
        if (code != null) {
            cause = "closed by the server with code " + code;
            if (reason != null) {
                cause += " (" + reason + ")";
            }
        } else {
            cause = failure.getMessage() == null
                ? failure.getClass().getSimpleName() : failure.getMessage();
        }
        Throwable origin = failure.getCause() == null ? failure : failure.getCause();
        return new ConnectionInterruptedException(message + ": " + cause, code, reason, origin);
    }

    private static SmsDockException finalError(String message, TransportFailure failure) {
        SmsDockException fatal = fatalError(failure);
        return fatal != null ? fatal : interrupted(message, failure);
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> object(Object value, String label) {
        if (!(value instanceof Map)) {
            throw new ProtocolException("The " + label + " is not a JSON object");
        }
        return (Map<String, Object>) value;
    }

    private static boolean isInteger(Object value) {
        return value instanceof Long || value instanceof Integer;
    }

    private static boolean isVersion(Object value) {
        return value instanceof Number
            && ((Number) value).doubleValue() == SmsDock.API_PROTOCOL_VERSION;
    }

    private static boolean positive(Duration duration) {
        return duration != null && !duration.isZero() && !duration.isNegative();
    }

    private static long nanos(Duration duration) {
        try {
            return Math.min(duration.toNanos(), MAX_NANOS);
        } catch (ArithmeticException error) {
            return duration.isNegative() ? -MAX_NANOS : MAX_NANOS;
        }
    }

    private static long deadline(long nanosFromNow) {
        return System.nanoTime() + Math.min(nanosFromNow, MAX_NANOS);
    }

    private static Map<String, Object> fields(Map<String, ?> parameters) {
        Map<String, Object> fields = new LinkedHashMap<>();
        if (parameters != null) {
            fields.putAll(parameters);
        }
        if (fields.containsKey("id")) {
            throw new IllegalArgumentException(
                "'id' is the id of the request; the client sets it");
        }
        return fields;
    }

    private static Map<String, Object> paging(int limit, String cursor) {
        if (limit < 1 || limit > SmsDock.MAX_PAGE_SIZE) {
            throw new IllegalArgumentException(
                "limit must be between 1 and " + SmsDock.MAX_PAGE_SIZE);
        }
        Map<String, Object> parameters = new LinkedHashMap<>();
        parameters.put("limit", Long.valueOf(limit));
        if (cursor != null) {
            parameters.put("cursor", cursor);
        }
        return parameters;
    }

    private static Map<String, Object> contactFilter(String query) {
        Map<String, Object> parameters = new LinkedHashMap<>();
        if (query != null) {
            parameters.put("q", query);
        }
        return parameters;
    }

    private static List<String> eventNames(Collection<String> names) {
        if (names == null || names.isEmpty()) {
            throw new IllegalArgumentException("events must be one event name or several");
        }
        for (String name : names) {
            if (name == null || name.isEmpty()) {
                throw new IllegalArgumentException(
                    "events must be one event name or several");
            }
        }
        return new ArrayList<>(names);
    }
}
