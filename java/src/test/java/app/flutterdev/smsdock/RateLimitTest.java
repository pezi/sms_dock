package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.time.Duration;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Test;

/** The error rate_limited: wait and repeat, but only when the server names the seconds. */
class RateLimitTest extends ServerTestBase {

    private final List<Long> waits = new ArrayList<>();

    /** A connected client whose pauses are recorded, in seconds, instead of slept. */
    private SmsDockClient patient(SmsDockClient.Builder options) {
        SmsDockClient client = track(options.build()).connect();
        client.setSleeper(nanos -> waits.add(TimeUnit.NANOSECONDS.toSeconds(nanos)));
        return client;
    }

    private static void limit(FakeSmsDock server, String action, long retryAfter) {
        server.failNext(action, "rate_limited", "Too many requests.",
            Map.of("retryAfter", retryAfter));
    }

    @Test
    void waitsAndRepeatsWhenRateLimited() {
        FakeSmsDock server = startServer();
        SmsDockClient client = patient(options(server));
        limit(server, "status", 1);
        assertEquals("hidden", client.status().get("deleteMode"));
        assertEquals(List.of(1L), waits);
        limit(server, "status", 1);
        limit(server, "status", 3);
        assertEquals("hidden", client.status().get("deleteMode"));
        assertEquals(List.of(1L, 1L, 3L), waits);
        assertEquals(5, Collections.frequency(server.actions(), "status"));
        assertEquals(1, client.getConnectionCount());
    }

    @Test
    void givesUpWhenRateLimitedAgainAndAgain() {
        FakeSmsDock server = startServer();
        SmsDockClient client = patient(options(server));
        for (int count = 0; count < 3; count++) {
            limit(server, "status", 1);
        }
        ApiException error = assertThrows(ApiException.class, client::status);
        assertEquals("rate_limited", error.getCode());
        assertEquals(1, error.getRetryAfter());
        assertEquals(List.of(1L, 1L), waits);
        assertEquals(3, Collections.frequency(server.actions(), "status"));
        assertNotNull(client.ping().get("time"));
    }

    @Test
    void limitOfMessagesPerMinuteIsReportedAtOnce() {
        FakeSmsDock server = startServer();
        SmsDockClient client = patient(options(server));
        // The limit of 20 SMS parts per minute names no seconds to wait.
        server.failNext("messages.send", "rate_limited",
            "Too many messages per minute. Try again shortly.", null);
        ApiException error = assertThrows(ApiException.class, () -> client.send(BEN, "text"));
        assertEquals("rate_limited", error.getCode());
        assertNull(error.getRetryAfter());
        assertEquals(List.of(), waits);
        assertEquals(List.of(), server.sent());
    }

    @Test
    void rateLimitWithoutARepeat() {
        FakeSmsDock server = startServer();
        SmsDockClient none = patient(options(server).rateLimitRetries(0));
        limit(server, "status", 1);
        assertEquals("rate_limited",
            assertThrows(ApiException.class, none::status).getCode());

        // Waiting longer than the timeout makes no sense.
        SmsDockClient impatient = patient(options(server).timeout(Duration.ofSeconds(5)));
        limit(server, "status", 6);
        assertEquals("rate_limited",
            assertThrows(ApiException.class, impatient::status).getCode());

        SmsDockClient once = patient(options(server));
        limit(server, "status", 1);
        assertEquals("rate_limited", assertThrows(ApiException.class,
            () -> once.request("status", null, false, null)).getCode());

        limit(server, "status", 1);
        String request = once.sendRequest("status");
        assertEquals("rate_limited", assertThrows(ApiException.class,
            () -> once.waitForResponse(request)).getCode());
        assertEquals(List.of(), waits);
    }

    @Test
    void sendIsNotRepeatedWhenRateLimited() {
        FakeSmsDock server = startServer();
        SmsDockClient client = patient(options(server));
        limit(server, "messages.send", 1);
        Map<String, Object> result = client.send(BEN, "Only once");
        assertEquals(List.of(1L), waits);
        List<Map<String, Object>> requests = server.requests("messages.send");
        assertEquals(2, requests.size());
        assertEquals(result.get("requestId"), requests.get(0).get("requestId"));
        assertEquals(result.get("requestId"), requests.get(1).get("requestId"));
        assertEquals(List.of("Only once"), field(server.sent(), "body"));
    }

    @Test
    void staysWithinTheLimitOfTheServer() {
        FakeSmsDock server = newServer();
        server.requestsPerSecond = 5;
        server.begin();
        SmsDockClient client = connect(server);
        long started = System.nanoTime();
        for (int count = 0; count < 9; count++) {
            assertTrue((Long) client.ping().get("time") > 0);
        }
        // The login and 4 requests fit into the first second, 5 into the next.
        assertTrue(server.refusedCount() >= 1);
        assertTrue(System.nanoTime() - started >= TimeUnit.MILLISECONDS.toNanos(900));
        assertEquals(9, Collections.frequency(server.actions(), "ping"));
        assertEquals(1, client.getConnectionCount());
    }
}
