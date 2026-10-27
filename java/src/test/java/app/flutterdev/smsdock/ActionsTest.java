package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotEquals;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.time.Duration;
import java.time.Instant;
import java.util.ArrayList;
import java.util.Iterator;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;

/** Every action of the API, with its parameters and their validation. */
class ActionsTest extends ServerTestBase {

    private FakeSmsDock server;
    private SmsDockClient client;

    @BeforeEach
    void connect() {
        server = startServer();
        client = connect(server);
    }

    @Test
    void status() {
        Map<String, Object> status = client.status();
        assertEquals("hidden", status.get("deleteMode"));
        assertEquals(false, status.get("defaultSmsApp"));
        assertEquals(2, ((List<?>) status.get("sims")).size());
        Map<?, ?> licence = (Map<?, ?>) status.get("licence");
        // Integers come back as Long, null stays null and is not left out.
        assertEquals(10L, licence.get("dailyLimit"));
        assertTrue(licence.containsKey("remaining"));
        assertNull(licence.get("remaining"));
    }

    @Test
    void ping() {
        assertTrue((Long) client.ping().get("time") > 1_700_000_000_000L);
    }

    @Test
    void conversations() {
        Map<String, Object> page = client.conversations();
        assertNull(page.get("nextCursor"));
        assertEquals(List.of(ANNA, BEN, "A1"), field(items(page), "address"));
        Map<String, Object> first = items(page).get(0);
        assertEquals("Anna Example", first.get("contact"));
        assertEquals("Anna 24", first.get("snippet"));
        assertEquals(25L, first.get("messageCount"));
        assertEquals(3L, first.get("unreadCount"));
        assertEquals(Map.of("limit", 50L), parameters("conversations.list"));
    }

    @Test
    void conversationsFilters() {
        assertEquals(List.of(ANNA, "A1"), field(items(client.conversations(
            new ConversationFilter().unread(true), 50, null)), "address"));
        assertEquals(List.of(BEN), field(items(client.conversations(
            new ConversationFilter().query("invoice"), 50, null)), "address"));
        assertEquals(List.of(BEN), field(items(client.conversations(
            new ConversationFilter().sim(2), 50, null)), "address"));
        assertEquals(Map.of("sim", 2L, "limit", 50L), parameters("conversations.list"));
    }

    @Test
    void conversationsPaging() {
        Map<String, Object> first = client.conversations(null, 2, null);
        assertEquals(2, items(first).size());
        String cursor = (String) first.get("nextCursor");
        assertNotNull(cursor);
        Map<String, Object> second = client.conversations(null, 2, cursor);
        assertEquals(List.of("A1"), field(items(second), "address"));
        assertNull(second.get("nextCursor"));
        assertEquals(List.of(ANNA, BEN, "A1"),
            field(client.iterConversations(null, 1), "address"));
        assertEquals(List.of(ANNA, BEN),
            field(client.iterConversations(null, 50, 2), "address"));
    }

    @Test
    void lastTenIncomingMessages() {
        Map<String, Object> page = client.messages(new MessageFilter().direction("in"), 10);
        List<Map<String, Object>> messages = items(page);
        assertEquals(10, messages.size());
        assertTrue(messages.stream().allMatch(message -> "in".equals(message.get("direction"))));
        assertEquals("Anna 24", messages.get(0).get("body"));
        assertEquals("Anna 6", messages.get(9).get("body"));
        assertNotNull(page.get("nextCursor"));
        Map<String, Object> newest = messages.get(0);
        assertEquals(BASE_TIME + 24_000, newest.get("timestamp"));
        assertEquals(false, newest.get("read"));
        assertEquals("received", newest.get("status"));
        assertNull(newest.get("requestId"));
        Map<String, Object> request = server.requests("messages.list").get(0);
        assertEquals("in", request.get("direction"));
        assertEquals(10L, request.get("limit"));
    }

    @Test
    void messagesOfANumber() {
        // Numbers are compared as the phone does.
        for (String address : List.of(BEN, "0043 690 87654321", "0690 87654321")) {
            assertEquals(3, items(client.messages(
                new MessageFilter().address(address), 50)).size(), address);
        }
        assertEquals(1, items(client.messages(new MessageFilter().address("a1"), 50)).size());
    }

    @Test
    void conversationWinsOverAddress() {
        long conversation = (Long) items(client.messages(
            new MessageFilter().address(BEN), 1)).get(0).get("conversationId");
        List<Map<String, Object>> messages = items(client.messages(
            new MessageFilter().address(ANNA).conversationId(conversation), 50));
        assertEquals(3, messages.size());
        assertTrue(messages.stream().allMatch(message -> BEN.equals(message.get("address"))));
    }

    @Test
    void messagesFilters() {
        assertEquals(List.of("Thanks for the invoice", "Your invoice is attached"),
            field(items(client.messages(new MessageFilter().query("invoice"), 50)), "body"));
        assertEquals(List.of("Your invoice is attached"), field(items(client.messages(
            new MessageFilter().query("invoice").direction("out").sim(2).address(BEN), 50)),
            "body"));
        Map<String, Object> expected = new LinkedHashMap<>();
        expected.put("address", BEN);
        expected.put("direction", "out");
        expected.put("q", "invoice");
        expected.put("sim", 2L);
        expected.put("limit", 50L);
        assertEquals(expected, parameters("messages.list"));
        assertEquals(new ArrayList<>(expected.keySet()),
            new ArrayList<>(parameters("messages.list").keySet()));
        assertEquals(4, items(client.messages(new MessageFilter().unread(true), 50)).size());
        assertEquals(true, parameters("messages.list").get("unread"));
        assertEquals(12, items(client.messages(new MessageFilter().direction("out"), 50))
            .stream().filter(message -> ANNA.equals(message.get("address"))).count());
    }

    @Test
    void messagesTimeRange() {
        MessageFilter filter = new MessageFilter()
            .since(BASE_TIME + 22_000).until(Instant.ofEpochMilli(BASE_TIME + 23_000));
        assertEquals(List.of("Anna 23", "Anna 22"),
            field(items(client.messages(filter, 50)), "body"));
        Map<String, Object> request = parameters("messages.list");
        assertEquals(BASE_TIME + 22_000, request.get("from"));
        assertEquals(BASE_TIME + 23_000, request.get("to"));
        assertEquals(List.of("Anna 24", "Anna 23", "Anna 22"), field(items(client.messages(
            new MessageFilter().since(Instant.ofEpochMilli(BASE_TIME + 22_000)), 50)), "body"));
    }

    @Test
    void messagesPaging() {
        Map<String, Object> first = client.messages(null, 20);
        assertEquals(20, items(first).size());
        Map<String, Object> second = client.messages(null, 20, (String) first.get("nextCursor"));
        assertEquals(9, items(second).size());
        assertNull(second.get("nextCursor"));
        List<Object> ids = field(items(first), "id");
        ids.addAll(field(items(second), "id"));
        assertEquals(29, ids.stream().distinct().count());
    }

    @Test
    void iterMessagesFollowsTheCursor() {
        List<Map<String, Object>> messages = list(client.iterMessages(null, 10));
        assertEquals(29, messages.size());
        assertEquals("Anna 24", messages.get(0).get("body"));
        assertEquals("Your bill is ready", messages.get(28).get("body"));
        List<Map<String, Object>> requests = server.requests("messages.list");
        assertEquals(3, requests.size());
        assertFalse(requests.get(0).containsKey("cursor"));
        assertTrue(requests.get(1).containsKey("cursor"));
        assertEquals(12, list(client.iterMessages(new MessageFilter().direction("out")
            .address(ANNA))).size());
    }

    @Test
    void iterMessagesIsLazyAndBounded() {
        Iterable<Map<String, Object>> messages = client.iterMessages(null, 10, 15);
        assertEquals(0, server.requests("messages.list").size());
        Iterator<Map<String, Object>> iterator = messages.iterator();
        assertEquals("Anna 24", iterator.next().get("body"));
        assertEquals(1, server.requests("messages.list").size());
        int count = 1;
        while (iterator.hasNext()) {
            iterator.next();
            count++;
        }
        assertEquals(15, count);
        assertEquals(List.of(10L, 5L), field(server.requests("messages.list"), "limit"));
        assertEquals(0, list(client.iterMessages(null, 10, 0)).size());
        assertEquals(2, server.requests("messages.list").size());
    }

    @Test
    void iterMessagesRefusesACursorThatDoesNotMove() {
        Map<String, Object> stuck = FakeSmsDock.object(
            "items", List.of(FakeSmsDock.object("id", 1L)), "nextCursor", "same");
        server.answerNext("messages.list", stuck);
        server.answerNext("messages.list", stuck);
        ProtocolException error = assertThrows(ProtocolException.class,
            () -> list(client.iterMessages(null)));
        assertTrue(error.getMessage().contains("cursor"));
    }

    @Test
    void iterMessagesRefusesAPageWithoutItems() {
        server.answerNext("messages.list", FakeSmsDock.object("nextCursor", null));
        ProtocolException error = assertThrows(ProtocolException.class,
            () -> list(client.iterMessages(null)));
        assertTrue(error.getMessage().contains("no list of items"));
    }

    @Test
    void pagesMayBeShorterThanTheLimit() {
        server.answerNext("messages.list", FakeSmsDock.object(
            "items", List.of(FakeSmsDock.object("id", 1L)), "nextCursor", "next"));
        server.answerNext("messages.list", FakeSmsDock.object(
            "items", List.of(), "nextCursor", "further"));
        server.answerNext("messages.list", FakeSmsDock.object(
            "items", List.of(FakeSmsDock.object("id", 2L)), "nextCursor", null));
        assertEquals(List.of(1L, 2L), field(client.iterMessages(null), "id"));
    }

    @Test
    void refusesWrongArguments() {
        int before = server.requests().size();
        assertThrows(IllegalArgumentException.class, () -> client.messages(null, 0));
        assertThrows(IllegalArgumentException.class, () -> client.messages(null, 201));
        assertThrows(IllegalArgumentException.class,
            () -> client.messages(new MessageFilter().direction("sideways"), 10));
        assertThrows(IllegalArgumentException.class, () -> client.conversations(null, 0, null));
        assertThrows(IllegalArgumentException.class, () -> client.contacts(null, 500, null));
        // The iterators check their arguments at once, not at the first item.
        assertThrows(IllegalArgumentException.class, () -> client.iterMessages(null, 0));
        assertThrows(IllegalArgumentException.class, () -> client.iterMessages(null, 10, -1));
        assertThrows(IllegalArgumentException.class,
            () -> client.iterMessages(new MessageFilter().direction("up")));
        assertThrows(IllegalArgumentException.class, () -> client.iterContacts(null, 201));
        assertThrows(IllegalArgumentException.class, () -> client.iterConversations(null, 0));
        assertEquals(before, server.requests().size());
    }

    @Test
    void getMessage() {
        Map<String, Object> newest = items(client.messages(null, 1)).get(0);
        Map<String, Object> message = client.getMessage((Long) newest.get("id"));
        assertEquals(newest, message);
        ApiException error = assertThrows(ApiException.class, () -> client.getMessage(1));
        assertEquals("not_found", error.getCode());
        assertFalse(error instanceof AuthenticationException);
        assertTrue(client.isConnected());
    }

    // ------------------------------------------------------------------
    // Sending
    // ------------------------------------------------------------------

    @Test
    void send() {
        Map<String, Object> result = client.send("+43 690 8765-4321", "All systems running.");
        assertEquals(BEN, result.get("to"));
        assertEquals(1L, result.get("parts"));
        assertEquals("queued", result.get("status"));
        String requestId = (String) result.get("requestId");
        assertTrue(requestId.matches("[0-9a-f]{32}"));
        assertEquals(1, server.sent().size());
        assertEquals("All systems running.", server.sent().get(0).get("body"));
        assertEquals(1L, server.sent().get(0).get("sim"));
        assertNotEquals(requestId, client.send(BEN, "Again").get("requestId"));
    }

    @Test
    void framesAreCompactJsonInTheOrderOfTheProtocol() {
        client.send(BEN, "Grüße", null, "r1");
        Map<String, Object> request = server.requests("messages.send").get(0);
        assertEquals(List.of("id", "action", "to", "body", "requestId"),
            new ArrayList<>(request.keySet()));
        assertEquals("java-2", request.get("id"));
        assertEquals("Grüße", request.get("body"));
    }

    @Test
    void sendWithOwnRequestIdAndSim() {
        Map<String, Object> result = client.send(BEN, "Order shipped.", 2L, "order-4711");
        assertEquals("order-4711", result.get("requestId"));
        assertEquals(2L, server.sent().get(0).get("sim"));
        assertTrue(waitUntil(() -> "sent".equals(
            server.outboxEntry("order-4711").get("status"))));
        // A repeat sends nothing and tells the status of now; the number may be written
        // differently.
        Map<String, Object> repeat = client.send("0043 690 87654321", "Order shipped.", 2L,
            "order-4711");
        assertEquals("sent", repeat.get("status"));
        assertEquals(1, server.sent().size());
        ApiException error = assertThrows(ApiException.class,
            () -> client.send(BEN, "Another text", null, "order-4711"));
        assertEquals("invalid_request", error.getCode());
        assertEquals(1, server.sent().size());
    }

    @Test
    void repeatAfterAFailureDoesNotSendAgain() {
        server.failNumbers.add("+43123456");
        client.send("+43123456", "Hello", null, "job-1");
        assertTrue(waitUntil(() -> "failed".equals(server.outboxEntry("job-1").get("status"))));
        server.failNumbers.clear();
        assertEquals("failed", client.send("+43123456", "Hello", null, "job-1").get("status"));
        assertEquals(1, server.sent().size());
    }

    @Test
    void sendRefusesWrongValuesBeforeAnythingIsSent() {
        assertThrows(IllegalArgumentException.class, () -> client.send("", "text"));
        assertThrows(IllegalArgumentException.class, () -> client.send("  ", "text"));
        assertThrows(IllegalArgumentException.class, () -> client.send(null, "text"));
        assertThrows(IllegalArgumentException.class, () -> client.send(BEN, ""));
        assertThrows(IllegalArgumentException.class, () -> client.send(BEN, null));
        for (String requestId : List.of("", "with space", "ümlaut", "a".repeat(65), "a/b")) {
            assertThrows(IllegalArgumentException.class,
                () -> client.send(BEN, "text", null, requestId), requestId);
        }
        assertEquals(0, server.requests("messages.send").size());
        assertEquals("queued",
            client.send(BEN, "text", null, "A-Z.a_z-0" + "9".repeat(55)).get("status"));
    }

    @Test
    void sendGroup() {
        Map<String, Object> result = client.sendGroup(
            List.of("+43 690 8765-4321", ANNA, BEN), "Hello all", 2L, "news-1");
        assertEquals("news-1", result.get("requestId"));
        assertEquals(List.of(BEN, ANNA), result.get("to"));
        assertEquals(1L, result.get("parts"));
        assertEquals("queued", result.get("status"));
        assertEquals(List.of(
            FakeSmsDock.object("requestId", "news-1.1", "to", BEN, "parts", 1L,
                "status", "queued"),
            FakeSmsDock.object("requestId", "news-1.2", "to", ANNA, "parts", 1L,
                "status", "queued")),
            result.get("messages"));
        Map<String, Object> request = server.requests("messages.sendGroup").get(0);
        assertEquals(List.of("id", "action", "to", "body", "requestId", "sim"),
            new ArrayList<>(request.keySet()));
        assertEquals(List.of("+43 690 8765-4321", ANNA, BEN), request.get("to"));
        assertEquals(2, server.sent().size());
        assertEquals("news-1", server.sent().get(0).get("group"));
        assertEquals(2L, server.sent().get(1).get("sim"));
        assertEquals("sent", client.waitForResult("news-1.2", Duration.ofSeconds(5))
            .get("status"));
        assertEquals(BEN, client.messageStatus("news-1.1").get("to"));
        assertEquals("not_found", assertThrows(ApiException.class,
            () -> client.messageStatus("news-1")).getCode());
        String random = (String) client.sendGroup(List.of(BEN), "one").get("requestId");
        assertTrue(random.matches("[0-9a-f]{32}"));
    }

    @Test
    void sendGroupRepeatsOnlyOnce() {
        Map<String, Object> first = client.sendGroup(List.of(BEN, ANNA), "Same", null, "news-2");
        assertEquals(first,
            client.sendGroup(List.of("0690/876.54.321", ANNA), "Same", null, "news-2"));
        assertEquals(2, server.sent().size());
        assertTrue(waitUntil(() -> "sent".equals(server.outboxEntry("news-2.1").get("status"))));
        Map<String, Object> later = client.sendGroup(List.of(ANNA, BEN), "Same", null, "news-2");
        assertEquals("sent", ((Map<?, ?>) ((List<?>) later.get("messages")).get(0))
            .get("status"));
        for (Map.Entry<List<String>, String> other : Map.of(
                List.of(BEN, ANNA), "Other", List.of(BEN), "Same").entrySet()) {
            assertEquals("invalid_request", assertThrows(ApiException.class,
                () -> client.sendGroup(other.getKey(), other.getValue(), null, "news-2"))
                .getCode());
        }
        assertEquals(2, server.sent().size());
    }

    @Test
    void sendGroupRefusesTheWholeGroup() {
        assertEquals("invalid_number", assertThrows(ApiException.class,
            () -> client.sendGroup(List.of(BEN, "abc"), "Hello")).getCode());
        assertEquals("message_too_long", assertThrows(ApiException.class,
            () -> client.sendGroup(List.of(BEN, ANNA), "x".repeat(1531))).getCode());
        assertEquals("invalid_request", assertThrows(ApiException.class,
            () -> client.sendGroup(List.of(BEN, ANNA, "+43123456789"), "x".repeat(1000)))
            .getCode());
        assertEquals("sim_unavailable", assertThrows(ApiException.class,
            () -> client.sendGroup(List.of(BEN), "Hello", 9L, null)).getCode());
        assertEquals(0, server.sent().size());
    }

    @Test
    void sendGroupGoesOnAfterARefusedMember() {
        server.refuseNumbers.add(ANNA);
        Map<String, Object> result = client.sendGroup(List.of(BEN, ANNA), "Hello", null, "n-3");
        List<?> members = (List<?>) result.get("messages");
        assertEquals("queued", ((Map<?, ?>) members.get(0)).get("status"));
        assertEquals("failed", ((Map<?, ?>) members.get(1)).get("status"));
        assertEquals("queued", result.get("status"));
        assertEquals("sent", client.waitForResult("n-3.1", Duration.ofSeconds(5)).get("status"));
        assertEquals("failed", client.messageStatus("n-3.2").get("status"));
        server.refuseNumbers.add(BEN);
        assertEquals("failed",
            client.sendGroup(List.of(BEN, ANNA), "Hello", null, "n-4").get("status"));
    }

    @Test
    void sendGroupRefusesWrongValuesBeforeAnythingIsSent() {
        List<String> many = new ArrayList<>();
        for (int index = 0; index < 21; index++) {
            many.add("+43699000000" + index);
        }
        assertThrows(IllegalArgumentException.class, () -> client.sendGroup(null, "text"));
        assertThrows(IllegalArgumentException.class, () -> client.sendGroup(List.of(), "text"));
        assertThrows(IllegalArgumentException.class,
            () -> client.sendGroup(List.of(BEN, " "), "text"));
        assertThrows(IllegalArgumentException.class, () -> client.sendGroup(many, "text"));
        assertThrows(IllegalArgumentException.class, () -> client.sendGroup(List.of(BEN), ""));
        for (String requestId : List.of("", "with space", "a".repeat(62))) {
            assertThrows(IllegalArgumentException.class,
                () -> client.sendGroup(List.of(BEN), "text", null, requestId), requestId);
        }
        assertEquals(0, server.requests("messages.sendGroup").size());
        assertEquals("queued",
            client.sendGroup(List.of(BEN), "text", null, "a".repeat(61)).get("status"));
    }

    @Test
    void sendErrors() {
        assertEquals("invalid_number", assertThrows(ApiException.class,
            () -> client.send("abc", "text")).getCode());
        assertEquals("message_too_long", assertThrows(ApiException.class,
            () -> client.send(BEN, "x".repeat(1531))).getCode());
        assertEquals("sim_unavailable", assertThrows(ApiException.class,
            () -> client.send(BEN, "text", 9L, null)).getCode());
        assertEquals(10L, client.send(BEN, "x".repeat(1530)).get("parts"));
        assertEquals(1, server.sent().size());
    }

    @Test
    void errorDetails() {
        server.failNext("messages.send", "permission_missing", "SEND_SMS is not granted.",
            Map.of("permission", "sendSms"));
        ApiException error = assertThrows(ApiException.class, () -> client.send(BEN, "text"));
        assertEquals("permission_missing", error.getCode());
        assertEquals("SEND_SMS is not granted.", error.getServerMessage());
        assertEquals("sendSms", error.getPermission());
        assertEquals(Map.of("permission", "sendSms"), error.getDetails());
        assertNull(error.getRetryAfter());
        assertEquals("permission_missing: SEND_SMS is not granted.", error.getMessage());
    }

    @Test
    void messageStatus() {
        String requestId = (String) client.send(BEN, "Hello").get("requestId");
        assertTrue(waitUntil(() -> "sent".equals(server.outboxEntry(requestId).get("status"))));
        Map<String, Object> entry = client.messageStatus(requestId);
        assertEquals("sent", entry.get("status"));
        assertEquals(1L, entry.get("partsSent"));
        assertEquals("api", entry.get("source"));
        assertNull(entry.get("errorCode"));
        assertNotNull(entry.get("messageId"));
        assertEquals("not_found", assertThrows(ApiException.class,
            () -> client.messageStatus("unknown")).getCode());
    }

    @Test
    void waitForResultByPolling() {
        String requestId = (String) client.send(BEN, "Hello").get("requestId");
        Map<String, Object> entry = client.waitForResult(requestId);
        assertEquals("sent", entry.get("status"));
        assertTrue(server.requests("messages.status").size() >= 1);
        assertEquals(0, client.getPendingEvents());
    }

    @Test
    void waitForResultWakesUpOnTheEvent() {
        FakeSmsDock slow = newServer();
        slow.sendDelayMillis = 300;
        slow.begin();
        SmsDockClient dock = connect(slow);
        dock.subscribe("sms.sent", "sms.failed");
        String requestId = (String) dock.send(BEN, "Hello").get("requestId");
        Map<String, Object> entry = dock.waitForResult(
            requestId, Duration.ofSeconds(5), false, Duration.ofSeconds(4));
        assertEquals("sent", entry.get("status"));
        // Without the event the next question would come after seconds.
        assertTrue(slow.requests("messages.status").size() <= 4);
        // The events stay in the queue.
        assertEquals("sms.sent", dock.waitForEvent("sms.sent").get("event"));
    }

    @Test
    void waitForResultFailed() {
        server.failNumbers.add("+43123456");
        String requestId = (String) client.send("+43123456", "Hello").get("requestId");
        Map<String, Object> entry = client.waitForResult(requestId, Duration.ofSeconds(3));
        assertEquals("failed", entry.get("status"));
        assertEquals(1L, entry.get("errorCode"));
    }

    @Test
    void waitForResultDelivered() {
        FakeSmsDock reporting = newServer();
        reporting.deliveryReports = true;
        reporting.sendDelayMillis = 100;
        reporting.begin();
        SmsDockClient dock = connect(reporting);
        String requestId = (String) dock.send(BEN, "Hello").get("requestId");
        Map<String, Object> entry = dock.waitForResult(
            requestId, Duration.ofSeconds(5), true, Duration.ofMillis(100));
        assertEquals("delivered", entry.get("status"));
    }

    @Test
    void waitForResultTimesOut() {
        FakeSmsDock slow = newServer();
        slow.sendDelayMillis = 30_000;
        slow.begin();
        SmsDockClient dock = connect(slow);
        String requestId = (String) dock.send(BEN, "Hello").get("requestId");
        SmsDockTimeoutException error = assertThrows(SmsDockTimeoutException.class,
            () -> dock.waitForResult(requestId, Duration.ofMillis(300)));
        assertTrue(error.getMessage().contains("No result"));
        assertTrue(error.getMessage().contains("queued"));
        assertTrue(dock.isConnected());
        assertThrows(IllegalArgumentException.class,
            () -> dock.waitForResult(requestId, null, false, Duration.ZERO));
    }

    // ------------------------------------------------------------------
    // Deleting, marking, contacts
    // ------------------------------------------------------------------

    @Test
    void deleteMessage() {
        long id = (Long) items(client.messages(null, 1)).get(0).get("id");
        assertEquals(Map.of("deleted", 1L, "mode", "hidden"), client.deleteMessage(id));
        assertEquals(28, server.messages().size());
        assertEquals("not_found", assertThrows(ApiException.class,
            () -> client.deleteMessage(id)).getCode());
    }

    @Test
    void deleteConversation() {
        long conversation = (Long) items(client.conversations()).get(1).get("id");
        assertEquals(Map.of("deleted", 3L, "mode", "hidden"),
            client.deleteConversation(conversation));
        assertEquals(26, server.messages().size());
        assertEquals("not_found", assertThrows(ApiException.class,
            () -> client.deleteConversation(conversation)).getCode());
        server.deleteMode = "provider";
        long next = (Long) items(client.conversations()).get(0).get("id");
        assertEquals("provider", client.deleteConversation(next).get("mode"));
    }

    @Test
    void markRead() {
        long conversation = (Long) items(client.conversations()).get(0).get("id");
        ApiException error = assertThrows(ApiException.class,
            () -> client.markRead(conversation));
        assertEquals("not_default_app", error.getCode());
        server.deleteMode = "provider";
        assertEquals(Map.of("ok", true), client.markRead(conversation));
        assertEquals(0L, items(client.conversations()).get(0).get("unreadCount"));
        assertEquals(Map.of("conversationId", conversation),
            parameters("conversations.markRead"));
    }

    @Test
    void contacts() {
        Map<String, Object> page = client.contacts();
        assertEquals(List.of("Anna Example", "Ben Example"), field(items(page), "name"));
        assertEquals(List.of(Map.of("number", ANNA, "label", "Mobile")),
            items(page).get(0).get("numbers"));
        assertEquals(List.of("Ben Example"),
            field(items(client.contacts("ben", 50, null)), "name"));
        assertEquals("ben", parameters("contacts.list").get("q"));
        Map<String, Object> first = client.contacts(null, 1, null);
        assertEquals("1", first.get("nextCursor"));
        assertEquals(List.of("Anna Example", "Ben Example"),
            field(client.iterContacts(null, 1), "name"));
        assertEquals(List.of("Anna Example"), field(client.iterContacts(null, 50, 1), "name"));
        assertEquals(List.of("Anna Example"), field(client.iterContacts("4369912"), "name"));
    }

    // ------------------------------------------------------------------
    // Generic requests
    // ------------------------------------------------------------------

    @Test
    void genericRequest() {
        Map<String, Object> page = client.request(
            "messages.list", Map.of("direction", "in", "limit", 3));
        assertEquals(3, items(page).size());
        assertEquals("unknown_action", assertThrows(ApiException.class,
            () -> client.request("messages.forward")).getCode());
        assertEquals("invalid_request", assertThrows(ApiException.class,
            () -> client.request("messages.list", Map.of("limit", 500))).getCode());
    }

    @Test
    void theRequestIdBelongsToTheClient() {
        assertThrows(IllegalArgumentException.class,
            () -> client.request("status", Map.of("id", "mine")));
        assertThrows(IllegalArgumentException.class,
            () -> client.sendRequest("status", Map.of("id", "mine")));
    }

    @Test
    void answersMayBeLargerThan64Kib() {
        for (int index = 0; index < 150; index++) {
            server.addMessage(ANNA, "y".repeat(1000));
        }
        Map<String, Object> page = client.messages(null, 200);
        assertEquals(179, items(page).size());
        assertTrue(Json.compact(page).length() > SmsDock.MAX_FRAME_BYTES);
    }

    @Test
    void refusesRequestsOver64Kib() {
        IllegalArgumentException error = assertThrows(IllegalArgumentException.class,
            () -> client.request("messages.list", Map.of("q", "x".repeat(70_000))));
        assertTrue(error.getMessage().contains("64 KiB"));
        assertEquals(0, server.requests("messages.list").size());
        assertNotNull(client.ping().get("time"));
    }

    /** The fields of the newest request with this action, without id and action. */
    private Map<String, Object> parameters(String action) {
        List<Map<String, Object>> requests = server.requests(action);
        Map<String, Object> fields = new LinkedHashMap<>(requests.get(requests.size() - 1));
        fields.remove("id");
        fields.remove("action");
        return fields;
    }
}
