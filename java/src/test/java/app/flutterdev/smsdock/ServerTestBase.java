package app.flutterdev.smsdock;

import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.function.BooleanSupplier;
import org.junit.jupiter.api.AfterEach;

/**
 * Fixtures: fake SMS Dock servers and clients connected to them over real TLS with a
 * throwaway certificate. Everything a test started is stopped after it.
 */
abstract class ServerTestBase {

    static final String PASSWORD = "secret123";
    static final long BASE_TIME = 1790672531000L;
    static final String ANNA = "+4369912345678";
    static final String BEN = "+4369087654321";

    private final List<FakeSmsDock> servers = new ArrayList<>();
    private final List<SmsDockClient> clients = new ArrayList<>();

    @AfterEach
    void stopEverything() {
        clients.forEach(SmsDockClient::close);
        servers.forEach(FakeSmsDock::end);
    }

    /** Stores 29 messages in three conversations. */
    static void fill(FakeSmsDock server) {
        server.addContact("Anna Example", ANNA, "Mobile");
        server.addContact("Ben Example", BEN, "Work");
        for (int index = 0; index < 25; index++) {
            boolean incoming = index % 2 == 0;
            server.addMessage(ANNA, "Anna " + index, incoming ? "in" : "out",
                BASE_TIME + index * 1000L, index < 22, 1L, null);
        }
        server.addMessage(BEN, "Your invoice is attached", "out", BASE_TIME - 5000, true, 2L,
            null);
        server.addMessage(BEN, "Thanks for the invoice", "in", BASE_TIME - 4000, true, 2L, null);
        server.addMessage(BEN, "See you", "in", BASE_TIME - 3000, true, 2L, null);
        server.addMessage("A1", "Your bill is ready", "in", BASE_TIME - 9000, false, 1L, null);
    }

    /** A filled fake server that is not started yet; set its options, then begin(). */
    FakeSmsDock newServer() {
        return newServer(TestCertificate.server(), 0);
    }

    FakeSmsDock newServer(TestCertificate certificate, int port) {
        FakeSmsDock server = new FakeSmsDock(certificate, port);
        fill(server);
        servers.add(server);
        return server;
    }

    /** A running fake server with the data of {@link #fill}. */
    FakeSmsDock startServer() {
        return newServer().begin();
    }

    /** The options of a client for this server, with short delays. */
    SmsDockClient.Builder options(FakeSmsDock server) {
        return SmsDockClient.builder(server.url())
            .environment(name -> null)
            .password(PASSWORD)
            .fingerprint(server.fingerprint())
            .reconnectDelay(Duration.ofMillis(10))
            .timeout(Duration.ofSeconds(5));
    }

    /** Builds the client and closes it after the test. */
    SmsDockClient track(SmsDockClient client) {
        clients.add(client);
        return client;
    }

    /** A client that is connected and logged in. */
    SmsDockClient connect(FakeSmsDock server) {
        return track(options(server).build()).connect();
    }

    /** Polls until the condition holds; returns whether it did. */
    static boolean waitUntil(BooleanSupplier condition) {
        return waitUntil(condition, 3000);
    }

    static boolean waitUntil(BooleanSupplier condition, long milliseconds) {
        long deadline = System.nanoTime() + milliseconds * 1_000_000;
        while (System.nanoTime() < deadline) {
            if (condition.getAsBoolean()) {
                return true;
            }
            try {
                Thread.sleep(10);
            } catch (InterruptedException error) {
                Thread.currentThread().interrupt();
                return false;
            }
        }
        return condition.getAsBoolean();
    }

    /** Runs a task on another thread and returns the thread. */
    static Thread later(Runnable task) {
        Thread thread = new Thread(task, "test-helper");
        thread.setDaemon(true);
        thread.start();
        return thread;
    }

    static void sleep(long milliseconds) {
        try {
            Thread.sleep(milliseconds);
        } catch (InterruptedException error) {
            Thread.currentThread().interrupt();
        }
    }

    /** The items of a page. */
    @SuppressWarnings("unchecked")
    static List<Map<String, Object>> items(Map<String, Object> page) {
        return (List<Map<String, Object>>) page.get("items");
    }

    /** One field of every item. */
    static List<Object> field(Iterable<Map<String, Object>> items, String name) {
        List<Object> values = new ArrayList<>();
        items.forEach(item -> values.add(item.get(name)));
        return values;
    }

    static <T> List<T> list(Iterable<T> items) {
        List<T> values = new ArrayList<>();
        items.forEach(values::add);
        return values;
    }

    /** An sms.received event frame with this sequence number. */
    static Map<String, Object> eventFrame(long seq) {
        return FakeSmsDock.object("event", "sms.received", "seq", seq, "data",
            FakeSmsDock.object("message", FakeSmsDock.object(
                "id", 3000 + seq, "conversationId", 170L, "address", ANNA, "contact", null,
                "direction", "in", "body", "Event " + seq, "timestamp", BASE_TIME,
                "sentTimestamp", BASE_TIME, "read", false, "status", "received", "sim", 1L,
                "requestId", null)));
    }
}
