package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import java.util.concurrent.TimeUnit;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;

/** Lost connections: reconnect, login, subscription, what was missed, and when not to. */
class ReconnectTest extends ServerTestBase {

    /** Drops all connections and waits until the server noticed. */
    private static void drop(FakeSmsDock server) {
        server.dropConnections(null, "");
        assertTrue(waitUntil(() -> server.clientCount() == 0));
    }

    @Test
    void reconnectsAndLogsInAgain() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        drop(server);
        assertEquals("hidden", client.status().get("deleteMode"));
        assertEquals(2, client.getConnectionCount());
        assertEquals(2, server.loginCount());
        assertEquals(0L, client.getMissedEvents());
        assertFalse(client.isServerRestarted());
    }

    @Test
    void reconnectsAfterACloseFrame() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        server.dropConnections(1001, "going away");
        assertTrue(waitUntil(() -> server.clientCount() == 0));
        assertNotNull(client.ping().get("time"));
        assertEquals(2, client.getConnectionCount());
    }

    @Test
    void requestLostBeforeItWasHandled() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        server.dropOn("status", "before");
        assertEquals("hidden", client.status().get("deleteMode"));
        assertEquals(2, server.requests("status").size());
        assertEquals(2, client.getConnectionCount());
    }

    @Test
    void restoresTheSubscription() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        client.subscribe(List.of("sms.received", "sms.sent"), ANNA);
        drop(server);
        assertNotNull(client.ping().get("time"));
        List<Map<String, Object>> subscribes = server.requests("subscribe");
        assertEquals(2, subscribes.size());
        assertEquals(List.of("sms.received", "sms.sent"), subscribes.get(1).get("events"));
        assertEquals(ANNA, subscribes.get(1).get("address"));
        // Login, subscription, then the repeated request.
        List<String> actions = server.actions();
        assertEquals(List.of("authenticate", "subscribe", "ping"),
            actions.subList(actions.size() - 3, actions.size()));
        server.receiveSms(BEN, "filtered");
        server.receiveSms(ANNA, "after the reconnect");
        Map<String, Object> event = client.waitForEvent("sms.received");
        assertEquals("after the reconnect",
            ((Map<?, ?>) ((Map<?, ?>) event.get("data")).get("message")).get("body"));
    }

    @Test
    void doesNotRestoreAnEmptySubscription() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        client.subscribe("sms.received");
        client.unsubscribe("sms.received");
        drop(server);
        assertNotNull(client.ping().get("time"));
        assertEquals(1, server.requests("subscribe").size());
    }

    @Test
    void waitingForEventsReconnects() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        client.subscribe("sms.received");
        Thread helper = later(() -> {
            sleep(200);
            drop(server);
            if (waitUntil(() -> server.requests("subscribe").size() == 2)) {
                server.receiveSms(ANNA, "after the drop");
            }
        });
        List<Map<String, Object>> events = new ArrayList<>();
        for (Map<String, Object> event : client.events(Duration.ofSeconds(5))) {
            events.add(event);
            break;
        }
        assertEquals(1, events.size());
        assertEquals(2, client.getConnectionCount());
        assertTrue(waitUntil(() -> !helper.isAlive()));
    }

    @Test
    void sendIsNotRepeatedAfterALostResponse() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        // The phone accepts the message, then the connection breaks before the response.
        server.dropOn("messages.send", "after");
        Map<String, Object> result = client.send(BEN, "Only once");
        List<Map<String, Object>> requests = server.requests("messages.send");
        assertEquals(2, requests.size());
        assertEquals(result.get("requestId"), requests.get(0).get("requestId"));
        assertEquals(result.get("requestId"), requests.get(1).get("requestId"));
        assertEquals(1, server.sent().size());
        assertEquals("Only once", server.sent().get(0).get("body"));
        assertEquals(2, client.getConnectionCount());
    }

    @Test
    void waitForResultSurvivesAReconnect() {
        FakeSmsDock server = newServer();
        server.sendDelayMillis = 400;
        server.begin();
        SmsDockClient client = connect(server);
        String requestId = (String) client.send(BEN, "Hello").get("requestId");
        drop(server);
        assertEquals("sent",
            client.waitForResult(requestId, Duration.ofSeconds(5)).get("status"));
        assertEquals(2, client.getConnectionCount());
    }

    @Test
    void givesUpAfterTheLastAttempt() {
        FakeSmsDock server = startServer();
        SmsDockClient client = track(options(server).reconnectAttempts(3)
            .connectTimeout(Duration.ofSeconds(2)).build()).connect();
        server.end();
        ConnectionInterruptedException error =
            assertThrows(ConnectionInterruptedException.class, client::status);
        assertTrue(error.getMessage().contains("'status'"));
        assertNotNull(error.getCause());
        assertFalse(client.isConnected());
    }

    @Test
    void backoffIsBounded() {
        FakeSmsDock server = startServer();
        SmsDockClient client = track(options(server).reconnectAttempts(6)
            .reconnectDelay(Duration.ofSeconds(1)).maxReconnectDelay(Duration.ofSeconds(10))
            .connectTimeout(Duration.ofSeconds(2)).build()).connect();
        server.end();
        List<Long> delays = new ArrayList<>();
        client.setSleeper(nanos -> delays.add(TimeUnit.NANOSECONDS.toMillis(nanos)));
        assertThrows(ConnectionInterruptedException.class, client::status);
        assertEquals(List.of(1000L, 2000L, 4000L, 8000L, 10000L, 10000L), delays);
    }

    @Test
    void noRetryOnRequest() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        server.dropOn("status", "before");
        assertThrows(ConnectionInterruptedException.class,
            () -> client.request("status", null, false, null));
        assertEquals(1, client.getConnectionCount());
        assertFalse(client.isConnected());
        assertNotNull(client.status());
        assertEquals(2, client.getConnectionCount());
    }

    @Test
    void zeroReconnectAttempts() {
        FakeSmsDock server = startServer();
        SmsDockClient client = track(options(server).reconnectAttempts(0).build()).connect();
        server.dropOn("status", "before");
        assertThrows(ConnectionInterruptedException.class, client::status);
        assertEquals(1, client.getConnectionCount());
    }

    @Test
    void pipelinedRequestsDoNotReconnect() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        server.dropOn("status", "after");
        String request = client.sendRequest("status");
        assertThrows(ConnectionInterruptedException.class,
            () -> client.waitForResponse(request, Duration.ofSeconds(2)));
        assertEquals(1, client.getConnectionCount());
    }

    @ParameterizedTest
    @CsvSource({
        "4410, not_authenticated",
        "4401, not_authenticated",
        "4403, licence_required",
    })
    void closeCodesThatForbidARetry(int code, String errorCode) {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        List<Long> pauses = new ArrayList<>();
        client.setSleeper(pauses::add);
        server.dropConnections(code, "");
        assertTrue(waitUntil(() -> server.clientCount() == 0));
        ApiException error = assertThrows(ApiException.class, client::status);
        assertEquals(code == 4403, error instanceof LicenceRequiredException);
        assertEquals(code != 4403, error instanceof AuthenticationException);
        assertEquals(errorCode, error.getCode());
        assertEquals((long) code, error.getDetails().get("closeCode"));
        // No new login with a password that may be wrong by now.
        assertEquals(1, server.loginCount());
        assertEquals(1, client.getConnectionCount());
        assertEquals(List.of(), pauses);
        assertFalse(client.isConnected());
    }

    @ParameterizedTest
    @CsvSource({"4400, binary frame, binary frame", "1009, too big, 64 KiB"})
    void protocolErrorCloseCodes(int code, String reason, String expected) {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        server.dropConnections(code, reason);
        assertTrue(waitUntil(() -> server.clientCount() == 0));
        ProtocolException error = assertThrows(ProtocolException.class, client::status);
        assertTrue(error.getMessage().contains(expected), error.getMessage());
        assertEquals(1, client.getConnectionCount());
        assertEquals(1, server.loginCount());
    }

    @Test
    void passwordChangedWhileListening() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        client.subscribe("sms.received");
        later(() -> {
            sleep(200);
            server.dropConnections(4410, "password changed");
        });
        AuthenticationException error = assertThrows(AuthenticationException.class,
            () -> list(client.events(Duration.ofSeconds(5))));
        assertTrue(error.getMessage().contains("password changed"));
        assertEquals(1, server.loginCount());
    }

    @Test
    void requestTimeoutReconnects() {
        FakeSmsDock server = startServer();
        SmsDockClient client = track(options(server).timeout(Duration.ofMillis(300))
            .reconnectAttempts(1).build()).connect();
        server.ignoreNext("status", 1);
        assertEquals("hidden", client.status().get("deleteMode"));
        assertEquals(2, client.getConnectionCount());
        server.ignoreNext("status", 2);
        ConnectionInterruptedException error =
            assertThrows(ConnectionInterruptedException.class, client::status);
        assertTrue(error.getMessage().contains("No response"));
        assertEquals(3, client.getConnectionCount());
    }

    // ------------------------------------------------------------------
    // Epoch and sequence numbers
    // ------------------------------------------------------------------

    @Test
    void loginTellsEpochAndSequenceNumber() {
        FakeSmsDock server = startServer();
        server.receiveSms(ANNA, "one");
        server.receiveSms(ANNA, "two");
        SmsDockClient client = connect(server);
        assertEquals(server.epoch(), client.getEpoch());
        assertEquals(2L, client.getLatestSeq());
        assertEquals(0L, client.getMissedEvents());
        assertFalse(client.isServerRestarted());
        client.subscribe("sms.received");
        server.receiveSms(ANNA, "three");
        client.waitForEvent("sms.received");
        assertEquals(3L, client.getLatestSeq());
    }

    @Test
    void countsTheEventsMissedWhileDisconnected() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        client.subscribe("sms.received");
        server.receiveSms(ANNA, "seen");
        client.waitForEvent("sms.received");
        drop(server);
        server.receiveSms(ANNA, "lost");
        server.receiveSms(BEN, "lost too");
        server.emit("conversations.changed", FakeSmsDock.object());
        assertNotNull(client.ping().get("time"));
        // All events of the phone count, subscribed or not.
        assertEquals(3L, client.getMissedEvents());
        assertFalse(client.isServerRestarted());
        assertEquals(4L, client.getLatestSeq());
        drop(server);
        assertNotNull(client.ping().get("time"));
        assertEquals(0L, client.getMissedEvents());
    }

    @Test
    void noticesARestartOfTheApp() {
        FakeSmsDock server = startServer();
        SmsDockClient client = connect(server);
        client.subscribe("sms.received");
        server.receiveSms(ANNA, "seen");
        client.waitForEvent("sms.received");
        long before = client.getEpoch();
        server.restart();
        assertTrue(waitUntil(() -> server.clientCount() == 0));
        assertNotNull(client.ping().get("time"));
        assertTrue(client.isServerRestarted());
        assertNull(client.getMissedEvents());
        assertTrue(client.getEpoch() != before);
        assertEquals(0L, client.getLatestSeq());
        drop(server);
        assertNotNull(client.ping().get("time"));
        assertFalse(client.isServerRestarted());
        assertEquals(0L, client.getMissedEvents());
    }

    @Test
    void hookReadsWhatWasMissed() {
        FakeSmsDock server = startServer();
        List<Object> caughtUp = new ArrayList<>();
        List<String> states = new ArrayList<>();
        SmsDockClient client = track(options(server).onReconnect(dock -> {
            states.add(dock.getMissedEvents() + "/" + dock.isServerRestarted() + "/"
                + dock.getSubscription().get("events"));
            // Requests work inside the hook; the subscription is in force again.
            caughtUp.addAll(field(dock.iterMessages(
                new MessageFilter().direction("in").since(BASE_TIME + 100_000)), "body"));
        }).build()).connect();
        assertEquals(List.of(), states);
        client.subscribe("sms.received");
        drop(server);
        server.receiveSms(ANNA, "lost");
        server.receiveSms(ANNA, "lost too");
        assertNotNull(client.ping().get("time"));
        assertEquals(List.of("2/false/[sms.received]"), states);
        assertEquals(List.of("lost too", "lost"), caughtUp);

        // The hook can be replaced later.
        List<Long> counts = new ArrayList<>();
        client.setOnReconnect(dock -> counts.add(dock.getMissedEvents()));
        assertNotNull(client.getOnReconnect());
        drop(server);
        assertNotNull(client.ping().get("time"));
        assertEquals(List.of(0L), counts);
        assertEquals(1, states.size());
    }

    @Test
    void loginWithoutEpochIsRefused() {
        FakeSmsDock server = startServer();
        SmsDockClient client = track(options(server).build());
        server.ignoreNext("authenticate", 1);
        later(() -> {
            if (waitUntil(() -> server.requests("authenticate").size() == 1)) {
                Map<String, Object> request = server.requests("authenticate").get(0);
                server.emitRaw(FakeSmsDock.object("id", request.get("id"), "ok", true,
                    "result", FakeSmsDock.object("protocolVersion", 1L)));
            }
        });
        ProtocolException error = assertThrows(ProtocolException.class, client::connect);
        assertTrue(error.getMessage().contains("epoch"));
        assertFalse(client.isConnected());
    }
}
