package app.flutterdev.smsdock;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.ToNumberPolicy;
import java.net.InetSocketAddress;
import java.nio.ByteBuffer;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.regex.Pattern;
import org.java_websocket.WebSocket;
import org.java_websocket.framing.CloseFrame;
import org.java_websocket.handshake.ClientHandshake;
import org.java_websocket.server.DefaultSSLWebSocketServerFactory;
import org.java_websocket.server.WebSocketServer;

/**
 * A fake SMS Dock server that speaks protocol version 1 of {@code API.md}.
 *
 * <p>It keeps all data in memory and sends nothing. It is a port of
 * {@code python/tests/fake_server.py}: the real server runs on the phone, this one follows
 * {@code API.md}. Options are plain fields; set them before {@link #begin()}. All methods may
 * be called from the test thread.
 */
final class FakeSmsDock extends WebSocketServer {

    static final List<String> EVENT_NAMES = SmsDock.EVENT_NAMES;

    private static final Gson GSON = new GsonBuilder()
        .setObjectToNumberStrategy(ToNumberPolicy.LONG_OR_DOUBLE)
        .serializeNulls()
        .disableHtmlEscaping()
        .create();
    private static final Pattern REQUEST_ID = Pattern.compile("[A-Za-z0-9._-]{1,64}");
    private static final Pattern NUMBER = Pattern.compile("\\+?[0-9]{3,15}");

    /** An error response, optionally followed by a close frame. */
    private static final class Failure extends RuntimeException {

        private final transient Map<String, Object> error = new LinkedHashMap<>();
        private final Integer closeCode;

        Failure(String code, String message, Integer closeCode, Map<String, Object> extra) {
            super(message);
            error.put("code", code);
            error.put("message", message);
            if (extra != null) {
                error.putAll(extra);
            }
            this.closeCode = closeCode;
        }

        Failure(String code, String message) {
            this(code, message, null, null);
        }
    }

    /** One connection. */
    private static final class Client {

        private final WebSocket connection;
        private boolean authenticated;
        private final Set<String> events = new HashSet<>();
        private String address;
        private final ArrayDeque<Long> requestTimes = new ArrayDeque<>();
        private int refused;
        private ScheduledFuture<?> timer;

        Client(WebSocket connection) {
            this.connection = connection;
        }
    }

    String password = "secret123";
    long authTimeoutMillis = 10_000;
    int maxClients = 8;
    long sendDelayMillis = 20;
    boolean deliveryReports;
    int requestsPerSecond = 20;
    int maxRefused = 20;
    int protocolVersion = 1;
    boolean licenceEnforced;
    boolean licensed;
    String deleteMode = "hidden";
    final Set<String> failNumbers = Collections.synchronizedSet(new HashSet<>());

    /** Numbers the phone refuses at once ({@code send_refused}); nothing is stored. */
    final Set<String> refuseNumbers = Collections.synchronizedSet(new HashSet<>());

    private final TestCertificate certificate;
    private final Object lock = new Object();
    private final CountDownLatch started = new CountDownLatch(1);
    private final ScheduledExecutorService scheduler =
        Executors.newSingleThreadScheduledExecutor(runnable -> {
            Thread thread = new Thread(runnable, "fake-smsdock");
            thread.setDaemon(true);
            return thread;
        });
    private volatile Exception startupError;

    private long epoch = System.currentTimeMillis();
    private int failedLogins;
    private int lockCount;
    private long lockedUntil;
    private final List<Map<String, Object>> sims = new ArrayList<>();
    private final List<Map<String, Object>> contacts = new ArrayList<>();
    private final List<Map<String, Object>> messages = new ArrayList<>();
    private final Map<String, Map<String, Object>> outbox = new LinkedHashMap<>();
    private final List<Map<String, Object>> sent = new ArrayList<>();
    private final List<Map<String, Object>> requests = new ArrayList<>();
    private int refusedCount;
    private int connectionCount;
    private int loginCount;
    private long nextMessageId = 2800;
    private final Map<String, Long> conversationIds = new LinkedHashMap<>();
    private final Map<String, String> bodies = new HashMap<>();
    private long sequence;
    private final List<Client> clients = new ArrayList<>();
    private final Map<String, List<String>> drops = new HashMap<>();
    private final Map<String, List<Failure>> failures = new HashMap<>();
    private final Map<String, List<Map<String, Object>>> results = new HashMap<>();
    private final Map<String, Integer> ignored = new HashMap<>();
    private List<Object> framesBeforeResponse = new ArrayList<>();
    private int hold;
    private List<Map<String, Object>> held = new ArrayList<>();

    /**
     * Creates a server on {@code wss://127.0.0.1:PORT/ws}.
     *
     * @param certificate the certificate it presents
     * @param port the port; 0 picks a free one
     */
    FakeSmsDock(TestCertificate certificate, int port) {
        super(new InetSocketAddress("127.0.0.1", port));
        this.certificate = certificate;
        setReuseAddr(true);
        setConnectionLostTimeout(0);
        setWebSocketFactory(new DefaultSSLWebSocketServerFactory(certificate.serverContext()));
        sims.add(object("subscriptionId", 1L, "slot", 0L, "name", "Play",
            "number", null, "isDefault", true));
        sims.add(object("subscriptionId", 2L, "slot", 1L, "name", "Work",
            "number", "+4369087654321", "isDefault", false));
    }

    // ------------------------------------------------------------------
    // Life cycle
    // ------------------------------------------------------------------

    /** Starts the server in background threads and waits until it listens. */
    FakeSmsDock begin() {
        start();
        try {
            if (!started.await(10, TimeUnit.SECONDS)) {
                throw new IllegalStateException("The fake server did not start");
            }
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
            throw new IllegalStateException(error);
        }
        if (startupError != null) {
            throw new IllegalStateException("The fake server failed", startupError);
        }
        return this;
    }

    /** Stops the server; clients see the close code 1001. */
    void end() {
        scheduler.shutdownNow();
        try {
            stop(2000);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
        }
    }

    /** The address of the API. */
    String url() {
        return "wss://127.0.0.1:" + getPort() + "/ws";
    }

    /** The fingerprint of the certificate the server presents. */
    String fingerprint() {
        return certificate.fingerprint();
    }

    // ------------------------------------------------------------------
    // State for assertions
    // ------------------------------------------------------------------

    int clientCount() {
        synchronized (lock) {
            return clients.size();
        }
    }

    int connectionCount() {
        synchronized (lock) {
            return connectionCount;
        }
    }

    int loginCount() {
        synchronized (lock) {
            return loginCount;
        }
    }

    int refusedCount() {
        synchronized (lock) {
            return refusedCount;
        }
    }

    long epoch() {
        synchronized (lock) {
            return epoch;
        }
    }

    /** Every request object received and not refused, in order. */
    List<Map<String, Object>> requests() {
        synchronized (lock) {
            return new ArrayList<>(requests);
        }
    }

    /** The requests with this action, in order. */
    List<Map<String, Object>> requests(String action) {
        List<Map<String, Object>> matching = new ArrayList<>();
        for (Map<String, Object> request : requests()) {
            if (action.equals(request.get("action"))) {
                matching.add(request);
            }
        }
        return matching;
    }

    /** The actions of all requests, in order. */
    List<String> actions() {
        List<String> actions = new ArrayList<>();
        for (Map<String, Object> request : requests()) {
            actions.add(String.valueOf(request.get("action")));
        }
        return actions;
    }

    /** Messages handed to the (imaginary) radio, one entry per SMS. */
    List<Map<String, Object>> sent() {
        synchronized (lock) {
            return new ArrayList<>(sent);
        }
    }

    List<Map<String, Object>> messages() {
        synchronized (lock) {
            return new ArrayList<>(messages);
        }
    }

    Map<String, Object> outboxEntry(String requestId) {
        synchronized (lock) {
            Map<String, Object> entry = outbox.get(requestId);
            return entry == null ? null : new LinkedHashMap<>(entry);
        }
    }

    // ------------------------------------------------------------------
    // Controls for tests
    // ------------------------------------------------------------------

    /** Adds a contact with one number. */
    void addContact(String name, String number, String label) {
        synchronized (lock) {
            List<Object> numbers = new ArrayList<>();
            numbers.add(object("number", number, "label", label));
            contacts.add(object("name", name, "numbers", numbers));
        }
    }

    /** Stores a message without sending an event. */
    Map<String, Object> addMessage(
            String address, String body, String direction, Long timestamp, boolean read,
            Long sim, String requestId) {
        synchronized (lock) {
            long time = timestamp == null ? System.currentTimeMillis() : timestamp;
            boolean incoming = direction.equals("in");
            nextMessageId++;
            Map<String, Object> message = object(
                "id", nextMessageId,
                "conversationId", conversationId(address),
                "address", address,
                "contact", contactName(address),
                "direction", direction,
                "body", body,
                "timestamp", time,
                "sentTimestamp", incoming ? Long.valueOf(time - 2000) : null,
                "read", read,
                "status", incoming ? "received" : "sent",
                "sim", sim,
                "requestId", requestId);
            messages.add(message);
            return message;
        }
    }

    /** Stores an incoming, read message on SIM 1 with the time of now. */
    Map<String, Object> addMessage(String address, String body) {
        return addMessage(address, body, "in", null, true, 1L, null);
    }

    /** Lets an SMS arrive: stores it and sends {@code sms.received}. */
    Map<String, Object> receiveSms(String address, String body) {
        synchronized (lock) {
            Map<String, Object> message = addMessage(address, body, "in", null, false, 1L, null);
            emit("sms.received", object("message", message));
            return message;
        }
    }

    /** Sends an event to the subscribed clients. */
    void emit(String name, Map<String, Object> data) {
        synchronized (lock) {
            sequence++;
            Map<String, Object> event = object("event", name, "seq", sequence, "data", data);
            for (Client client : new ArrayList<>(clients)) {
                if (client.authenticated && wants(client, name, data)) {
                    send(client, event);
                }
            }
        }
    }

    /** Sends a frame as it is to all clients, logged in or not. */
    void emitRaw(Object frame) {
        synchronized (lock) {
            for (Client client : new ArrayList<>(clients)) {
                send(client, frame);
            }
        }
    }

    /**
     * Behaves like a restart of the app: all connections are lost, the sequence numbers
     * begin at 1 again and {@code epoch} changes. Messages and the outbox are kept.
     */
    void restart() {
        synchronized (lock) {
            dropConnections(null, "");
            sequence = 0;
            epoch = Math.max(epoch + 1, System.currentTimeMillis());
        }
    }

    /** Closes all connections: abruptly, or with a close code. */
    void dropConnections(Integer code, String reason) {
        synchronized (lock) {
            for (Client client : new ArrayList<>(clients)) {
                if (code == null) {
                    abort(client);
                } else {
                    client.connection.close(code, reason);
                }
            }
        }
    }

    /**
     * Drops the connection at the next request with this action: {@code "before"} the
     * request is carried out, or {@code "after"} it was carried out but before the response
     * is sent. Calling it again queues one more drop.
     */
    void dropOn(String action, String when) {
        synchronized (lock) {
            drops.computeIfAbsent(action, key -> new ArrayList<>()).add(when);
        }
    }

    /** Answers the next request with this action with an error. */
    void failNext(String action, String code, String message, Map<String, Object> extra) {
        synchronized (lock) {
            failures.computeIfAbsent(action, key -> new ArrayList<>())
                .add(new Failure(code, message, null, extra));
        }
    }

    /** Answers the next request with this action with this result. */
    void answerNext(String action, Map<String, Object> result) {
        synchronized (lock) {
            results.computeIfAbsent(action, key -> new ArrayList<>()).add(result);
        }
    }

    /** Does not answer the next requests with this action at all. */
    void ignoreNext(String action, int times) {
        synchronized (lock) {
            ignored.merge(action, times, Integer::sum);
        }
    }

    /** Sends these frames (text or JSON objects) right before the next response. */
    void beforeNextResponse(Object... frames) {
        synchronized (lock) {
            framesBeforeResponse.addAll(List.of(frames));
        }
    }

    /** Holds the next {@code count} responses and sends them in reverse order. */
    void reverseNextResponses(int count) {
        synchronized (lock) {
            hold = count;
        }
    }

    /** Locks logins as after too many wrong passwords. */
    void lockLogins(double seconds) {
        synchronized (lock) {
            lockedUntil = System.nanoTime() + (long) (seconds * 1e9);
        }
    }

    // ------------------------------------------------------------------
    // Connections
    // ------------------------------------------------------------------

    @Override
    public void onStart() {
        started.countDown();
    }

    @Override
    public void onError(WebSocket connection, Exception error) {
        if (connection == null && started.getCount() > 0) {
            startupError = error;
            started.countDown();
        }
    }

    @Override
    public void onOpen(WebSocket connection, ClientHandshake handshake) {
        synchronized (lock) {
            if (clients.size() >= maxClients) {
                connection.close(4409, "too many clients");
                return;
            }
            Client client = new Client(connection);
            connection.setAttachment(client);
            clients.add(client);
            connectionCount++;
            if (!scheduler.isShutdown()) {
                client.timer = scheduler.schedule(() -> {
                    synchronized (lock) {
                        if (!client.authenticated) {
                            client.connection.close(4401, "not authenticated in time");
                        }
                    }
                }, authTimeoutMillis, TimeUnit.MILLISECONDS);
            }
            send(client, object("event", "hello", "protocolVersion", (long) protocolVersion,
                "authenticationRequired", true));
        }
    }

    @Override
    public void onClose(WebSocket connection, int code, String reason, boolean remote) {
        synchronized (lock) {
            Client client = connection.getAttachment();
            if (client != null) {
                clients.remove(client);
                if (client.timer != null) {
                    client.timer.cancel(false);
                }
            }
        }
    }

    @Override
    public void onMessage(WebSocket connection, ByteBuffer message) {
        connection.close(4400, "binary frame");
    }

    @Override
    public void onMessage(WebSocket connection, String frame) {
        synchronized (lock) {
            Client client = connection.getAttachment();
            if (client != null) {
                onFrame(client, frame);
            }
        }
    }

    private void abort(Client client) {
        client.connection.closeConnection(CloseFrame.ABNORMAL_CLOSE, "");
    }

    private void send(Client client, Object frame) {
        String text = frame instanceof String ? (String) frame : GSON.toJson(frame);
        try {
            client.connection.send(text);
        } catch (RuntimeException error) {
            // The connection is closed.
        }
    }

    private void onFrame(Client client, String frame) {
        Map<String, Object> request = null;
        try {
            Object decoded = GSON.fromJson(frame, Object.class);
            if (decoded instanceof Map) {
                request = cast(decoded);
            }
        } catch (RuntimeException error) {
            // Answered with invalid_json below.
        }
        if (!withinRate(client)) {
            // Every frame counts, the login and unreadable ones as well.
            Object identifier = request == null ? null : request.get("id");
            if (!(identifier instanceof String) && !(identifier instanceof Number)) {
                identifier = null;
            }
            refusedCount++;
            send(client, object("id", identifier, "ok", false, "error", object(
                "code", "rate_limited", "message", "Too many requests.", "retryAfter", 1L)));
            client.refused++;
            if (client.refused > maxRefused) {
                client.connection.close(4429, "Too many requests.");
            }
            return;
        }
        client.refused = 0;
        if (request == null) {
            respond(client, object("id", null, "ok", false, "error", object(
                "code", "invalid_json", "message", "The frame is not a JSON object.")));
            return;
        }
        requests.add(request);
        Object identifier = request.get("id");
        Object action = request.get("action");
        List<String> pending = action instanceof String ? drops.get(action) : null;
        if (pending != null && !pending.isEmpty() && pending.get(0).equals("before")) {
            pending.remove(0);
            abort(client);
            return;
        }

        Integer closeCode = null;
        Map<String, Object> response;
        try {
            Map<String, Object> result = dispatch(client, request);
            if (result == null) {
                return;
            }
            response = object("id", identifier, "ok", true, "result", result);
        } catch (Failure failure) {
            response = object("id", identifier, "ok", false, "error", failure.error);
            closeCode = failure.closeCode;
        }

        if (pending != null && !pending.isEmpty() && pending.get(0).equals("after")) {
            pending.remove(0);
            abort(client);
            return;
        }
        respond(client, response);
        if (closeCode != null) {
            Map<String, Object> error = cast(response.get("error"));
            client.connection.close(closeCode, String.valueOf(error.get("code")));
        }
    }

    /** Counts a frame; at most {@code requestsPerSecond} fit in a second. */
    private boolean withinRate(Client client) {
        long now = System.nanoTime();
        ArrayDeque<Long> times = client.requestTimes;
        while (!times.isEmpty() && now - times.peekFirst() >= TimeUnit.SECONDS.toNanos(1)) {
            times.pollFirst();
        }
        if (times.size() >= requestsPerSecond) {
            return false;
        }
        times.addLast(now);
        return true;
    }

    private void respond(Client client, Map<String, Object> response) {
        List<Object> frames = framesBeforeResponse;
        framesBeforeResponse = new ArrayList<>();
        for (Object frame : frames) {
            send(client, frame);
        }
        if (hold > 0) {
            held.add(response);
            if (held.size() < hold) {
                return;
            }
            List<Map<String, Object>> waiting = held;
            held = new ArrayList<>();
            hold = 0;
            Collections.reverse(waiting);
            for (Map<String, Object> item : waiting) {
                send(client, item);
            }
            return;
        }
        send(client, response);
    }

    // ------------------------------------------------------------------
    // Actions
    // ------------------------------------------------------------------

    private Map<String, Object> dispatch(Client client, Map<String, Object> request) {
        Object rawAction = request.get("action");
        if (!(rawAction instanceof String)) {
            throw new Failure("invalid_request", "The field 'action' is missing.");
        }
        String action = (String) rawAction;
        Object identifier = request.get("id");
        if (identifier != null && !(identifier instanceof String)
                && !(identifier instanceof Number)) {
            throw new Failure("invalid_request", "'id' must be a string or a number.");
        }
        if (ignored.getOrDefault(action, 0) > 0) {
            ignored.merge(action, -1, Integer::sum);
            return null;
        }
        if (action.equals("authenticate")) {
            return authenticate(client, request);
        }
        if (!client.authenticated) {
            throw new Failure("not_authenticated", "Authenticate first.");
        }
        List<Failure> injected = failures.get(action);
        if (injected != null && !injected.isEmpty()) {
            throw injected.remove(0);
        }
        List<Map<String, Object>> prepared = results.get(action);
        if (prepared != null && !prepared.isEmpty()) {
            return prepared.remove(0);
        }
        switch (action) {
            case "status":
                return status();
            case "ping":
                return object("time", System.currentTimeMillis());
            case "conversations.list":
                return conversationsList(request);
            case "messages.list":
                return messagesList(request);
            case "messages.get":
                return findMessage(request);
            case "messages.status":
                return messagesStatus(request);
            case "messages.delete":
                return messagesDelete(request);
            case "conversations.delete":
                return conversationsDelete(request);
            case "conversations.markRead":
                return conversationsMarkRead(request);
            case "contacts.list":
                return contactsList(request);
            case "messages.send":
                return messagesSend(request);
            case "messages.sendGroup":
                return messagesSendGroup(request);
            case "subscribe":
                // subscribe adds the names; its address replaces the filter, and without
                // address (or with null) the filter is removed.
                client.events.addAll(names(request));
                client.address = optional(request, "address", String.class);
                return subscription(client);
            case "unsubscribe":
                client.events.removeAll(names(request));
                if (client.events.isEmpty()) {
                    client.address = null;
                }
                return subscription(client);
            default:
                throw new Failure("unknown_action", "Unknown action '" + action + "'.");
        }
    }

    private Map<String, Object> authenticate(Client client, Map<String, Object> request) {
        loginCount++;
        // As in the app: the licence is looked at before the password, so a login without
        // licence never counts as wrong.
        if (licenceEnforced && !licensed) {
            throw new Failure("licence_required",
                "The API needs the full licence of SMS Dock.", 4403, null);
        }
        long remaining = lockedUntil - System.nanoTime();
        if (remaining > 0) {
            long seconds = (long) Math.ceil(remaining / 1e9);
            throw new Failure("locked_out",
                "Too many attempts. Try again in " + seconds + " seconds.", 4401,
                object("retryAfter", seconds));
        }
        if (!password.equals(request.get("password"))) {
            failedLogins++;
            if (failedLogins >= 5) {
                long seconds = Math.min(30L * (1L << lockCount), 900);
                lockCount++;
                failedLogins = 0;
                lockedUntil = System.nanoTime() + TimeUnit.SECONDS.toNanos(seconds);
            }
            throw new Failure("invalid_password", "Wrong password.", 4401, null);
        }
        failedLogins = 0;
        lockCount = 0;
        client.authenticated = true;
        return object(
            "protocolVersion", (long) protocolVersion,
            "app", app(),
            "latestSeq", sequence,
            "epoch", epoch);
    }

    private Map<String, Object> app() {
        return object("name", "SMS Dock", "version", "1.0.0",
            "protocolVersion", (long) protocolVersion);
    }

    private Map<String, Object> status() {
        return object(
            "app", app(),
            "sims", sims,
            "permissions", object("readSms", true, "sendSms", true, "receiveSms", true,
                "contacts", true, "phoneState", true, "notifications", true),
            "defaultSmsApp", deleteMode.equals("provider"),
            "deleteMode", deleteMode,
            "licence", object("enforced", licenceEnforced, "licensed", licensed,
                "dailyLimit", 10L, "sentToday", (long) sent.size(), "remaining", null),
            "popup", true);
    }

    private Map<String, Object> conversationsList(Map<String, Object> request) {
        String query = optional(request, "q", String.class);
        boolean unread = Boolean.TRUE.equals(optional(request, "unread", Boolean.class));
        Long sim = optional(request, "sim", Long.class);
        List<Map<String, Object>> items = conversations();
        if (query != null && !query.isEmpty()) {
            String needle = query.toLowerCase(Locale.ROOT);
            Set<Object> matching = new HashSet<>();
            for (Map<String, Object> message : messages) {
                if (lower(message.get("body")).contains(needle)) {
                    matching.add(message.get("conversationId"));
                }
            }
            items.removeIf(item -> !matching.contains(item.get("id"))
                && !lower(item.get("address")).contains(needle)
                && !lower(item.get("contact")).contains(needle));
        }
        if (unread) {
            items.removeIf(item -> (Long) item.get("unreadCount") == 0);
        }
        if (sim != null) {
            items.removeIf(item -> !sim.equals(item.get("sim")));
        }
        return page(items, request, false);
    }

    private Map<String, Object> messagesList(Map<String, Object> request) {
        String address = optional(request, "address", String.class);
        Long conversation = optional(request, "conversationId", Long.class);
        String direction = optional(request, "direction", String.class);
        String query = optional(request, "q", String.class);
        Long start = optional(request, "from", Long.class);
        Long end = optional(request, "to", Long.class);
        Long sim = optional(request, "sim", Long.class);
        boolean unread = Boolean.TRUE.equals(optional(request, "unread", Boolean.class));
        if (direction == null) {
            direction = "all";
        }
        if (!List.of("in", "out", "all").contains(direction)) {
            throw new Failure("invalid_request", "'direction' must be 'in', 'out' or 'all'.");
        }
        List<Map<String, Object>> items = new ArrayList<>(messages);
        items.sort(NEWEST_FIRST);
        if (conversation != null) {
            // conversationId wins if address is given too.
            items.removeIf(message -> !conversation.equals(message.get("conversationId")));
        } else if (address != null) {
            items.removeIf(message -> !samePeer((String) message.get("address"), address));
        }
        if (!direction.equals("all")) {
            String wanted = direction;
            items.removeIf(message -> !wanted.equals(message.get("direction")));
        }
        if (query != null && !query.isEmpty()) {
            String needle = query.toLowerCase(Locale.ROOT);
            items.removeIf(message -> !lower(message.get("body")).contains(needle));
        }
        if (start != null) {
            items.removeIf(message -> (Long) message.get("timestamp") < start);
        }
        if (end != null) {
            items.removeIf(message -> (Long) message.get("timestamp") > end);
        }
        if (sim != null) {
            items.removeIf(message -> !sim.equals(message.get("sim")));
        }
        if (unread) {
            items.removeIf(message -> Boolean.TRUE.equals(message.get("read")));
        }
        return page(items, request, false);
    }

    private Map<String, Object> findMessage(Map<String, Object> request) {
        Long identifier = required(request, "messageId", Long.class);
        for (Map<String, Object> message : messages) {
            if (identifier.equals(message.get("id"))) {
                return message;
            }
        }
        throw new Failure("not_found", "No message with the id " + identifier + ".");
    }

    private Map<String, Object> messagesStatus(Map<String, Object> request) {
        String requestId = required(request, "requestId", String.class);
        Map<String, Object> entry = outbox.get(requestId);
        if (entry == null) {
            throw new Failure("not_found", "No message with the requestId " + requestId + ".");
        }
        return new LinkedHashMap<>(entry);
    }

    private Map<String, Object> messagesDelete(Map<String, Object> request) {
        Map<String, Object> message = findMessage(request);
        messages.remove(message);
        later(0, () -> emit("sms.deleted", object(
            "id", message.get("id"),
            "conversationId", message.get("conversationId"),
            "address", message.get("address"),
            "mode", deleteMode)));
        return object("deleted", 1L, "mode", deleteMode);
    }

    private Map<String, Object> conversationsDelete(Map<String, Object> request) {
        Long identifier = required(request, "conversationId", Long.class);
        List<Map<String, Object>> doomed = new ArrayList<>();
        for (Map<String, Object> message : messages) {
            if (identifier.equals(message.get("conversationId"))) {
                doomed.add(message);
            }
        }
        if (doomed.isEmpty()) {
            throw new Failure("not_found", "This conversation does not exist.");
        }
        doomed.sort(NEWEST_FIRST);
        Object address = doomed.get(0).get("address");
        messages.removeAll(doomed);
        later(0, () -> emit("sms.deleted", object(
            "id", null, "conversationId", identifier, "address", address,
            "mode", deleteMode)));
        return object("deleted", (long) doomed.size(), "mode", deleteMode);
    }

    private Map<String, Object> conversationsMarkRead(Map<String, Object> request) {
        // As in the app: no event; the default SMS app is checked first.
        Long identifier = required(request, "conversationId", Long.class);
        if (!deleteMode.equals("provider")) {
            throw new Failure("not_default_app",
                "Only the default SMS app can mark messages as read.");
        }
        boolean found = false;
        for (Map<String, Object> message : messages) {
            if (identifier.equals(message.get("conversationId"))) {
                message.put("read", true);
                found = true;
            }
        }
        if (!found) {
            throw new Failure("not_found", "This conversation does not exist.");
        }
        return object("ok", true);
    }

    private Map<String, Object> contactsList(Map<String, Object> request) {
        String query = optional(request, "q", String.class);
        List<Map<String, Object>> items = new ArrayList<>(contacts);
        items.sort(Comparator.comparing(contact -> lower(contact.get("name"))));
        if (query != null && !query.isEmpty()) {
            String needle = query.toLowerCase(Locale.ROOT);
            items.removeIf(contact -> {
                if (lower(contact.get("name")).contains(needle)) {
                    return false;
                }
                for (Object number : (List<?>) contact.get("numbers")) {
                    if (lower(((Map<?, ?>) number).get("number")).contains(needle)) {
                        return false;
                    }
                }
                return true;
            });
        }
        return page(items, request, true);
    }

    private Map<String, Object> messagesSend(Map<String, Object> request) {
        String to = required(request, "to", String.class);
        String body = required(request, "body", String.class);
        Long sim = optional(request, "sim", Long.class);
        String requestId = optional(request, "requestId", String.class);
        if (requestId != null && !REQUEST_ID.matcher(requestId).matches()) {
            throw new Failure("invalid_request",
                "'requestId' must be 1 to 64 characters of A-Z a-z 0-9 . _ -.");
        }
        String number = normalizeNumber(to);
        if (!NUMBER.matcher(number).matches()) {
            throw new Failure("invalid_number", "'to' is not a phone number.");
        }
        if (body.isEmpty()) {
            throw new Failure("invalid_request", "'body' is empty.");
        }
        long parts = countParts(body);
        if (parts > 10) {
            throw new Failure("message_too_long",
                "The message needs " + parts + " SMS parts; at most 10 are allowed.");
        }
        Map<String, Object> known = requestId == null ? null : outbox.get(requestId);
        if (known != null) {
            // A repeat: never sent twice, answered with the current status.
            if (!bodies.get(requestId).equals(body)
                    || !samePeer((String) known.get("to"), number)) {
                throw new Failure("invalid_request",
                    "'requestId' was already used for another message.");
            }
            return object("requestId", known.get("requestId"), "to", known.get("to"),
                "parts", known.get("parts"), "status", known.get("status"));
        }
        sim = subscription(sim);
        if (requestId == null) {
            requestId = UUID.randomUUID().toString().replace("-", "");
        }
        return sendResult(accept(requestId, number, body, parts, sim, null));
    }

    /** Returns the SIM to send with; the first one without {@code sim}. */
    private Long subscription(Long sim) {
        if (sim == null) {
            return 1L;
        }
        if (sims.stream().noneMatch(s -> sim.equals(s.get("subscriptionId")))) {
            throw new Failure("sim_unavailable", "No SIM with the subscription id " + sim + ".");
        }
        return sim;
    }

    /** Queues one message that passed the checks and starts sending it. */
    private Map<String, Object> accept(String requestId, String number, String body,
            long parts, Long sim, String group) {
        Map<String, Object> entry = object(
            "requestId", requestId, "to", number, "parts", parts, "partsSent", 0L,
            "status", "queued", "errorCode", null, "messageId", null, "sim", sim,
            "source", "api", "createdAt", System.currentTimeMillis());
        outbox.put(requestId, entry);
        bodies.put(requestId, body);
        Map<String, Object> record = object("requestId", requestId, "to", number,
            "body", body, "sim", sim);
        if (group != null) {
            record.put("group", group);
        }
        sent.add(record);
        emit("sms.queued", object("requestId", requestId, "to", number, "parts", parts,
            "sim", sim, "source", "api"));
        if (refuseNumbers.contains(number)) {
            // The phone did not even take the message.
            entry.put("status", "failed");
            emit("sms.failed", object("requestId", requestId, "id", null,
                "conversationId", null, "to", number, "errorCode", null,
                "reason", "send_refused"));
            return entry;
        }
        if (parts > 1 && !failNumbers.contains(number)) {
            // Some parts are sent, some are left.
            later(sendDelayMillis / 2, () -> {
                entry.put("status", "sending");
                entry.put("partsSent", 1L);
            });
        }
        later(sendDelayMillis, () -> transmit(entry, body));
        return entry;
    }

    private static Map<String, Object> sendResult(Map<String, Object> entry) {
        return object("requestId", entry.get("requestId"), "to", entry.get("to"),
            "parts", entry.get("parts"), "status", entry.get("status"));
    }

    private static final List<String> STATUS_ORDER =
        List.of("queued", "sending", "sent", "delivered");

    /** The GroupSendResult of {@code members}, as the app builds it. */
    private static Map<String, Object> groupResult(String requestId,
            List<Map<String, Object>> members) {
        String status = members.stream()
            .map(m -> (String) m.get("status"))
            .filter(value -> !"failed".equals(value))
            .min((a, b) -> Integer.compare(STATUS_ORDER.indexOf(a), STATUS_ORDER.indexOf(b)))
            .orElse("failed");
        List<Object> numbers = new ArrayList<>();
        List<Object> results = new ArrayList<>();
        for (Map<String, Object> member : members) {
            numbers.add(member.get("to"));
            results.add(sendResult(member));
        }
        return object("requestId", requestId, "to", numbers, "parts", members.get(0).get("parts"),
            "status", status, "messages", results);
    }

    /** The members of a group sent before, oldest first; empty for an unknown group. */
    private List<Map<String, Object>> knownGroup(String requestId) {
        List<Map<String, Object>> members = new ArrayList<>();
        for (int index = 1; outbox.containsKey(requestId + "." + index); index++) {
            members.add(outbox.get(requestId + "." + index));
        }
        return members;
    }

    private Map<String, Object> messagesSendGroup(Map<String, Object> request) {
        List<?> to = required(request, "to", List.class);
        String body = required(request, "body", String.class);
        Long sim = optional(request, "sim", Long.class);
        String requestId = optional(request, "requestId", String.class);
        if (to.isEmpty()) {
            throw new Failure("invalid_request", "'to' is missing.");
        }
        List<String> numbers = new ArrayList<>();
        for (Object item : to) {
            if (!(item instanceof String)) {
                throw new Failure("invalid_request", "'to' must contain strings.");
            }
            String number = normalizeNumber((String) item);
            if (!NUMBER.matcher(number).matches()) {
                throw new Failure("invalid_number",
                    "'to' contains '" + item + "', which is not a phone number.");
            }
            if (numbers.stream().noneMatch(known -> samePeer(known, number))) {
                numbers.add(number);
            }
        }
        if (numbers.size() > 20) {
            throw new Failure("invalid_request",
                "A group message goes to at most 20 recipients.");
        }
        if (body.isEmpty()) {
            throw new Failure("invalid_request", "'body' is empty.");
        }
        long parts = countParts(body);
        if (parts > 10) {
            throw new Failure("message_too_long",
                "The message needs " + parts + " SMS parts; at most 10 are allowed.");
        }
        if (requestId != null
                && (!REQUEST_ID.matcher(requestId).matches() || requestId.length() > 61)) {
            throw new Failure("invalid_request",
                "'requestId' of a group must be 1 to 61 characters of A-Z a-z 0-9 . _ -.");
        }
        if (requestId == null) {
            requestId = UUID.randomUUID().toString().replace("-", "");
        }
        List<Map<String, Object>> known = knownGroup(requestId);
        if (!known.isEmpty()) {
            boolean same = known.size() == numbers.size()
                && known.stream().allMatch(m -> bodies.get(m.get("requestId")).equals(body))
                && numbers.stream().allMatch(number -> known.stream()
                    .anyMatch(m -> samePeer((String) m.get("to"), number)));
            if (!same) {
                throw new Failure("invalid_request",
                    "'requestId' was already used for another message.");
            }
            return groupResult(requestId, known);
        }
        long total = parts * numbers.size();
        if (total > 20) {
            throw new Failure("invalid_request", "This group needs " + total
                + " SMS parts; at most 20 can be sent per minute. Send to fewer recipients "
                + "or shorten the text.");
        }
        Long subscription = subscription(sim);
        List<Map<String, Object>> members = new ArrayList<>();
        for (int index = 0; index < numbers.size(); index++) {
            members.add(accept(requestId + "." + (index + 1), numbers.get(index), body, parts,
                subscription, requestId));
        }
        return groupResult(requestId, members);
    }

    private void transmit(Map<String, Object> entry, String body) {
        String number = (String) entry.get("to");
        Object requestId = entry.get("requestId");
        if (failNumbers.contains(number)) {
            // The phone has not stored the message: no id, no conversation.
            entry.put("status", "failed");
            entry.put("errorCode", 1L);
            emit("sms.failed", object("requestId", requestId, "id", null,
                "conversationId", null, "to", number, "errorCode", 1L,
                "reason", "generic_failure"));
            return;
        }
        Map<String, Object> message = addMessage(
            number, body, "out", null, true, (Long) entry.get("sim"), (String) requestId);
        entry.put("status", "sent");
        entry.put("partsSent", entry.get("parts"));
        entry.put("messageId", message.get("id"));
        emit("sms.sent", object("requestId", requestId,
            "conversationId", message.get("conversationId"), "to", number,
            "message", message));
        if (deliveryReports) {
            later(sendDelayMillis, () -> {
                entry.put("status", "delivered");
                message.put("status", "delivered");
                emit("sms.delivered", object("requestId", requestId, "id", message.get("id"),
                    "conversationId", message.get("conversationId"), "to", number));
            });
        }
    }

    private void later(long delayMillis, Runnable task) {
        if (scheduler.isShutdown()) {
            return;
        }
        scheduler.schedule(() -> {
            synchronized (lock) {
                task.run();
            }
        }, delayMillis, TimeUnit.MILLISECONDS);
    }

    // ------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------

    private static final Comparator<Map<String, Object>> NEWEST_FIRST =
        Comparator.<Map<String, Object>, Long>comparing(item -> (Long) item.get("timestamp"))
            .thenComparing(item -> (Long) item.get("id"))
            .reversed();

    private Long conversationId(String address) {
        for (Map.Entry<String, Long> known : conversationIds.entrySet()) {
            if (samePeer(known.getKey(), address)) {
                return known.getValue();
            }
        }
        long identifier = 170 + conversationIds.size();
        conversationIds.put(address, identifier);
        return identifier;
    }

    private String contactName(String address) {
        for (Map<String, Object> contact : contacts) {
            for (Object number : (List<?>) contact.get("numbers")) {
                if (samePeer((String) ((Map<?, ?>) number).get("number"), address)) {
                    return (String) contact.get("name");
                }
            }
        }
        return null;
    }

    private List<Map<String, Object>> conversations() {
        Map<Object, List<Map<String, Object>>> grouped = new LinkedHashMap<>();
        for (Map<String, Object> message : messages) {
            grouped.computeIfAbsent(message.get("conversationId"), key -> new ArrayList<>())
                .add(message);
        }
        List<Map<String, Object>> items = new ArrayList<>();
        grouped.forEach((identifier, group) -> {
            Map<String, Object> newest = Collections.min(group, NEWEST_FIRST);
            String body = (String) newest.get("body");
            items.add(object(
                "id", identifier,
                "address", newest.get("address"),
                "contact", newest.get("contact"),
                "snippet", body.substring(0, Math.min(160, body.length())),
                "timestamp", newest.get("timestamp"),
                "direction", newest.get("direction"),
                "messageCount", (long) group.size(),
                "unreadCount", group.stream()
                    .filter(message -> !Boolean.TRUE.equals(message.get("read"))).count(),
                "sim", newest.get("sim")));
        });
        items.sort(NEWEST_FIRST);
        return items;
    }

    private static Map<String, Object> subscription(Client client) {
        List<String> events = new ArrayList<>();
        for (String name : EVENT_NAMES) {
            if (client.events.contains(name)) {
                events.add(name);
            }
        }
        return object("events", events, "address", client.address);
    }

    /** Returns the event names of a request, with {@code *} replaced. */
    private static List<String> names(Map<String, Object> request) {
        Object names = request.get("events");
        if (!(names instanceof List) || ((List<?>) names).isEmpty()) {
            throw new Failure("invalid_request", "'events' is missing.");
        }
        List<String> list = new ArrayList<>();
        for (Object name : (List<?>) names) {
            if (!(name instanceof String)) {
                throw new Failure("invalid_request", "'events' is missing.");
            }
            list.add((String) name);
        }
        if (list.contains("*")) {
            return EVENT_NAMES;
        }
        for (String name : list) {
            if (!EVENT_NAMES.contains(name)) {
                throw new Failure("invalid_request", "Unknown event '" + name + "'.");
            }
        }
        return list;
    }

    /**
     * Whether a client subscribed to an event and its filter lets it pass. The filter
     * applies to {@code sms.*} events that name a peer.
     */
    private static boolean wants(Client client, String name, Map<String, Object> data) {
        if (!client.events.contains(name)) {
            return false;
        }
        if (client.address == null || !name.startsWith("sms.")) {
            return true;
        }
        Object message = data.get("message");
        Object peer = message instanceof Map ? ((Map<?, ?>) message).get("address") : null;
        if (peer == null) {
            peer = data.get("to");
        }
        if (peer == null) {
            peer = data.get("address");
        }
        if (!(peer instanceof String)) {
            return true;
        }
        return samePeer((String) peer, client.address);
    }

    private static Map<String, Object> page(
            List<Map<String, Object>> items, Map<String, Object> request, boolean byIndex) {
        Object rawLimit = request.containsKey("limit") ? request.get("limit") : 50L;
        if (!(rawLimit instanceof Long) || (Long) rawLimit < 1 || (Long) rawLimit > 200) {
            throw new Failure("invalid_request", "'limit' must be between 1 and 200.");
        }
        int limit = ((Long) rawLimit).intValue();
        String cursor = optional(request, "cursor", String.class);
        int start = 0;
        if (cursor != null && byIndex) {
            if (!cursor.matches("[0-9]+")) {
                throw new Failure("invalid_request", "'cursor' is invalid.");
            }
            start = Integer.parseInt(cursor);
        } else if (cursor != null) {
            List<String> keys = new ArrayList<>();
            for (Map<String, Object> item : items) {
                keys.add(item.get("timestamp") + ":" + item.get("id"));
            }
            if (!keys.contains(cursor)) {
                throw new Failure("invalid_request", "'cursor' is invalid.");
            }
            start = keys.indexOf(cursor) + 1;
        }
        List<Map<String, Object>> chunk = new ArrayList<>(items.subList(
            Math.min(start, items.size()), Math.min(start + limit, items.size())));
        String nextCursor = null;
        if (!chunk.isEmpty() && start + limit < items.size()) {
            Map<String, Object> last = chunk.get(chunk.size() - 1);
            nextCursor = byIndex ? String.valueOf(start + chunk.size())
                : last.get("timestamp") + ":" + last.get("id");
        }
        return object("items", chunk, "nextCursor", nextCursor);
    }

    private static <T> T required(Map<String, Object> request, String name, Class<T> kind) {
        T value = optional(request, name, kind);
        if (value == null) {
            throw new Failure("invalid_request", "The field '" + name + "' is missing.");
        }
        return value;
    }

    private static <T> T optional(Map<String, Object> request, String name, Class<T> kind) {
        Object value = request.get(name);
        if (value == null) {
            return null;
        }
        if (!kind.isInstance(value)) {
            throw new Failure("invalid_request", "The field '" + name + "' is malformed.");
        }
        return kind.cast(value);
    }

    // ------------------------------------------------------------------
    // Phone numbers
    // ------------------------------------------------------------------

    /** Removes the characters that are ignored in phone numbers. */
    static String normalizeNumber(String number) {
        return number.strip().replaceAll("[\\s\\-./()]", "");
    }

    static boolean isNumber(String address) {
        return normalizeNumber(address).matches("\\+?[0-9]+");
    }

    private static String addressKey(String address) {
        if (!isNumber(address)) {
            return address.strip().toLowerCase(Locale.ROOT);
        }
        String digits = normalizeNumber(address);
        if (digits.startsWith("00") && digits.length() > 4) {
            return "+" + digits.substring(2);
        }
        return digits;
    }

    /** Whether two addresses mean the same peer ("Phone numbers" in API.md). */
    static boolean samePeer(String first, String second) {
        String a = addressKey(first);
        String b = addressKey(second);
        if (a.equals(b)) {
            return true;
        }
        if (!isNumber(first) || !isNumber(second)) {
            return false;
        }
        if (a.startsWith("+") && b.startsWith("+")) {
            return false;
        }
        if (a.startsWith("+")) {
            return ends(a, b);
        }
        if (b.startsWith("+")) {
            return ends(b, a);
        }
        return ends(a, b) || ends(b, a);
    }

    private static boolean ends(String full, String national) {
        String significant = national.replaceFirst("^0+", "");
        return significant.length() >= 7 && full.endsWith(significant);
    }

    /** Returns the number of SMS parts of a text (simplified alphabet test). */
    static long countParts(String body) {
        boolean plain = body.chars().allMatch(character -> character < 128);
        int single = plain ? 160 : 70;
        int multi = plain ? 153 : 67;
        if (body.length() <= single) {
            return 1;
        }
        return (body.length() + multi - 1) / multi;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /** Builds a JSON object from keys and values, in this order. */
    static Map<String, Object> object(Object... pairs) {
        Map<String, Object> map = new LinkedHashMap<>();
        for (int index = 0; index < pairs.length; index += 2) {
            map.put((String) pairs[index], pairs[index + 1]);
        }
        return map;
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> cast(Object value) {
        return (Map<String, Object>) value;
    }

    private static String lower(Object value) {
        return value == null ? "" : String.valueOf(value).toLowerCase(Locale.ROOT);
    }
}
