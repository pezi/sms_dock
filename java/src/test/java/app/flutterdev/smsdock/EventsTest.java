package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.time.Duration;
import java.util.Iterator;
import java.util.List;
import java.util.Map;
import java.util.Set;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

/** Subscriptions, the event queue and responses that arrive out of order. */
class EventsTest extends ServerTestBase {

    private static final Duration SHORT = Duration.ofMillis(100);

    private FakeSmsDock server;
    private SmsDockClient client;

    @BeforeEach
    void connect() {
        server = startServer();
        client = connect(server);
    }

    @Test
    void subscribeAndUnsubscribe() {
        assertNull(client.getSubscription());
        Map<String, Object> result = client.subscribe("sms.received");
        assertEquals(List.of("sms.received"), result.get("events"));
        assertTrue(result.containsKey("address"));
        assertNull(result.get("address"));
        assertEquals(result, client.getSubscription());

        result = client.subscribe(List.of("sms.sent", "sms.failed"), ANNA);
        assertEquals(List.of("sms.received", "sms.sent", "sms.failed"), result.get("events"));
        assertEquals(ANNA, result.get("address"));
        assertEquals(ANNA, client.getSubscription().get("address"));

        // Each subscribe sets the filter anew: without address it is removed.
        result = client.subscribe("*");
        assertEquals(SmsDock.EVENT_NAMES, result.get("events"));
        assertNull(client.getSubscription().get("address"));
        Map<String, Object> request = server.requests("subscribe").get(2);
        assertEquals(List.of("*"), request.get("events"));
        assertFalse(request.containsKey("address"));

        result = client.unsubscribe("sms.received", "sms.queued");
        assertEquals(5, ((List<?>) result.get("events")).size());
        assertEquals(List.of(), client.unsubscribe(List.of("*")).get("events"));
        assertEquals(List.of(), client.getSubscription().get("events"));

        assertEquals("invalid_request", assertThrows(ApiException.class,
            () -> client.subscribe("sms.unknown")).getCode());
        assertThrows(IllegalArgumentException.class, () -> client.subscribe());
        assertThrows(IllegalArgumentException.class, () -> client.subscribe(""));
        assertThrows(IllegalArgumentException.class, () -> client.unsubscribe(List.of()));
    }

    @Test
    void aNewConnectionGetsNoEvents() {
        server.receiveSms(ANNA, "nobody listens");
        assertEquals(List.of(), list(client.events(SHORT)));
    }

    @Test
    @SuppressWarnings("unchecked")
    void incomingSms() {
        client.subscribe("sms.received");
        server.receiveSms(ANNA, "STATUS");
        Map<String, Object> event = client.waitForEvent("sms.received");
        assertEquals("sms.received", event.get("event"));
        assertEquals(1L, event.get("seq"));
        Map<String, Object> message =
            (Map<String, Object>) ((Map<String, Object>) event.get("data")).get("message");
        assertEquals(ANNA, message.get("address"));
        assertEquals("Anna Example", message.get("contact"));
        assertEquals("STATUS", message.get("body"));
        assertEquals("in", message.get("direction"));
        assertEquals(false, message.get("read"));

        // React to it: requests work inside the loop over the events.
        server.receiveSms(BEN, "STATUS");
        for (Map<String, Object> next : client.events(Duration.ofSeconds(2))) {
            Map<String, Object> received =
                (Map<String, Object>) ((Map<String, Object>) next.get("data")).get("message");
            client.send((String) received.get("address"), "All systems running.");
            break;
        }
        assertEquals(BEN, server.sent().get(0).get("to"));
    }

    @Test
    void eventsBetweenRequestAndResponseAreQueued() {
        server.beforeNextResponse(eventFrame(1), eventFrame(2));
        assertEquals("hidden", client.status().get("deleteMode"));
        assertEquals(2, client.getPendingEvents());
        assertEquals(2L, client.getLatestSeq());
        server.beforeNextResponse(eventFrame(3));
        assertNotNull(client.ping().get("time"));
        // In the order of their arrival.
        assertEquals(List.of(1L, 2L, 3L), field(client.events(SHORT), "seq"));
        assertEquals(0, client.getPendingEvents());
        assertEquals(0, client.getDroppedEvents());
    }

    @Test
    void eventsEndAfterTheTimeout() {
        client.subscribe("sms.received");
        long started = System.nanoTime();
        Iterator<Map<String, Object>> events = client.events(Duration.ofMillis(200)).iterator();
        assertFalse(events.hasNext());
        long elapsed = (System.nanoTime() - started) / 1_000_000;
        assertTrue(elapsed >= 180 && elapsed < 3000, "waited " + elapsed + " ms");
        assertFalse(events.hasNext());
        assertTrue(client.isConnected());
    }

    @Test
    void waitForEventKeepsTheOthers() {
        client.subscribe("*");
        server.receiveSms(ANNA, "first");
        server.emit("conversations.changed", FakeSmsDock.object());
        server.receiveSms(BEN, "second");
        Map<String, Object> changed = client.waitForEvent("conversations.changed");
        assertEquals(2L, changed.get("seq"));
        assertEquals(1, client.getPendingEvents());
        Map<String, Object> second = client.waitForEvent(
            Set.of("sms.received"), Duration.ofSeconds(2),
            event -> ((Map<?, ?>) ((Map<?, ?>) event.get("data")).get("message"))
                .get("address").equals(BEN));
        assertEquals(3L, second.get("seq"));
        // From the queue, without reading the connection.
        assertEquals(1L, client.waitForEvent(List.of("*"), SHORT, null).get("seq"));
        assertEquals(0, client.getPendingEvents());
    }

    @Test
    void waitForEventTimesOut() {
        client.subscribe("sms.received");
        server.emit("conversations.changed", FakeSmsDock.object());
        SmsDockTimeoutException error = assertThrows(SmsDockTimeoutException.class,
            () -> client.waitForEvent("sms.received", Duration.ofMillis(200)));
        assertTrue(error.getMessage().contains("sms.received"));
        assertTrue(client.isConnected());
        assertThrows(IllegalArgumentException.class,
            () -> client.waitForEvent(List.of(), null, null));
    }

    @Test
    void addressFilter() {
        client.subscribe(List.of("sms.received"), "0699 12345678");
        assertEquals("0699 12345678", server.requests("subscribe").get(0).get("address"));
        server.receiveSms(BEN, "ignored");
        server.receiveSms(ANNA, "wanted");
        List<Map<String, Object>> events = list(client.events(Duration.ofMillis(300)));
        assertEquals(1, events.size());
        assertEquals(2L, events.get(0).get("seq"));
        // The phone counts all events, so the client sees a gap.
        assertEquals(2L, client.getLatestSeq());
    }

    @Test
    void eventsOfASentMessage() {
        client.subscribe("sms.queued", "sms.sent");
        String requestId = (String) client.send(BEN, "Hello").get("requestId");
        Map<String, Object> queued = client.waitForEvent("sms.queued");
        assertEquals(requestId, ((Map<?, ?>) queued.get("data")).get("requestId"));
        assertEquals("api", ((Map<?, ?>) queued.get("data")).get("source"));
        Map<String, Object> sent = client.waitForEvent("sms.sent");
        Map<?, ?> message = (Map<?, ?>) ((Map<?, ?>) sent.get("data")).get("message");
        assertEquals("out", message.get("direction"));
        assertEquals(requestId, message.get("requestId"));
    }

    @Test
    void deleteSendsAnEvent() {
        client.subscribe("sms.deleted");
        long id = (Long) items(client.messages(null, 1)).get(0).get("id");
        client.deleteMessage(id);
        Map<?, ?> data = (Map<?, ?>) client.waitForEvent("sms.deleted").get("data");
        assertEquals(id, data.get("id"));
        assertEquals("hidden", data.get("mode"));
    }

    @Test
    void fullEventQueueDropsTheOldest() {
        SmsDockClient small = track(options(server).eventQueueSize(2).build()).connect();
        server.beforeNextResponse(eventFrame(1), eventFrame(2), eventFrame(3), eventFrame(4));
        small.ping();
        assertEquals(2, small.getPendingEvents());
        assertEquals(2, small.getDroppedEvents());
        assertEquals(List.of(3L, 4L), field(small.events(SHORT), "seq"));
    }

    // ------------------------------------------------------------------
    // Several requests at once
    // ------------------------------------------------------------------

    @Test
    void responsesInAnotherOrder() {
        server.reverseNextResponses(3);
        String first = client.sendRequest("messages.list", Map.of("limit", 1));
        String second = client.sendRequest("status");
        String third = client.sendRequest("contacts.list");
        assertEquals(3, Set.of(first, second, third).size());
        Duration wait = Duration.ofSeconds(2);
        assertTrue(client.waitForResponse(second, wait).containsKey("deleteMode"));
        assertEquals("Anna 24", items(client.waitForResponse(first, wait)).get(0).get("body"));
        assertEquals(2, items(client.waitForResponse(third)).size());
        assertThrows(IllegalArgumentException.class, () -> client.waitForResponse(first));
        assertNotNull(client.ping().get("time"));
    }

    @Test
    void responseErrorOfAPipelinedRequest() {
        server.reverseNextResponses(2);
        String missing = client.sendRequest("messages.get", Map.of("messageId", 1));
        String fine = client.sendRequest("ping");
        ApiException error = assertThrows(ApiException.class,
            () -> client.waitForResponse(missing, Duration.ofSeconds(2)));
        assertEquals("not_found", error.getCode());
        assertTrue((Long) client.waitForResponse(fine).get("time") > 0);
    }

    @Test
    void waitForResponseTimesOut() {
        server.ignoreNext("status", 1);
        String request = client.sendRequest("status");
        assertThrows(SmsDockTimeoutException.class,
            () -> client.waitForResponse(request, Duration.ofMillis(200)));
        // The connection is kept, the request stays open.
        assertTrue(client.isConnected());
        assertEquals(1, client.getConnectionCount());
        assertNotNull(client.ping().get("time"));
    }

    @Test
    void staleResponsesAreIgnored() {
        server.beforeNextResponse(
            FakeSmsDock.object("id", "java-0", "ok", true,
                "result", FakeSmsDock.object("stale", true)),
            FakeSmsDock.object("id", 77L, "ok", false,
                "error", FakeSmsDock.object("code", "internal_error", "message", "stale")));
        assertEquals("hidden", client.status().get("deleteMode"));
        assertTrue((Long) client.ping().get("time") > 0);
    }

    @Test
    void responseWithoutIdAnswersTheOpenRequest() {
        server.beforeNextResponse(FakeSmsDock.object("id", null, "ok", false, "error",
            FakeSmsDock.object("code", "invalid_json", "message", "Not a JSON object.")));
        ApiException error = assertThrows(ApiException.class, client::status);
        assertEquals("invalid_json", error.getCode());
        // The late real response is dropped; the next request works.
        assertTrue((Long) client.ping().get("time") > 0);
    }

    @Test
    void malformedFrames() {
        server.beforeNextResponse("this is not JSON");
        assertTrue(assertThrows(ProtocolException.class, client::status)
            .getMessage().contains("invalid JSON"));
        server.beforeNextResponse("[1, 2, 3]");
        assertTrue(assertThrows(ProtocolException.class, client::status)
            .getMessage().contains("not a JSON object"));
    }

    @Test
    void malformedResponse() {
        server.beforeNextResponse(FakeSmsDock.object("id", "java-2", "ok", false));
        ProtocolException error = assertThrows(ProtocolException.class, client::status);
        assertTrue(error.getMessage().contains("Malformed"));
    }
}
