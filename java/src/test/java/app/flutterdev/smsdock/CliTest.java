package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNotNull;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.PrintStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.function.Function;
import org.junit.jupiter.api.BeforeEach;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.CsvSource;
import org.junit.jupiter.params.provider.ValueSource;

/** The command line {@code smsdock}: output and exit codes. */
class CliTest extends ServerTestBase {

    private static final String URL = "wss://192.168.1.50:9443/ws";
    private static final String FINGERPRINT = "AB:".repeat(31) + "AB";

    /** Exit code, standard output and standard error of one run. */
    private record Result(int code, String out, String err) {

        List<String> lines() {
            return out.lines().toList();
        }

        Object json() {
            try {
                return Json.parse(out);
            } catch (IOException error) {
                throw new AssertionError("Not JSON: " + out, error);
            }
        }
    }

    private final Map<String, String> environment = new HashMap<>();
    private String input = "";
    private Function<String, String> prompt;
    private FakeSmsDock server;

    @BeforeEach
    void start() {
        // Every test starts with the password set and no fingerprint.
        environment.put("SMSDOCK_PASSWORD", PASSWORD);
        server = startServer();
    }

    private Result run(String... args) {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        ByteArrayOutputStream err = new ByteArrayOutputStream();
        SmsDockCli tool = new SmsDockCli(
            environment::get,
            new ByteArrayInputStream(input.getBytes(StandardCharsets.UTF_8)),
            StandardCharsets.UTF_8,
            new PrintStream(out, true, StandardCharsets.UTF_8),
            new PrintStream(err, true, StandardCharsets.UTF_8),
            prompt);
        int code = tool.run(args);
        return new Result(code, out.toString(StandardCharsets.UTF_8),
            err.toString(StandardCharsets.UTF_8));
    }

    /** Runs a command against the fake server: adds URL, fingerprint and a timeout. */
    private Result dock(String command, String... options) {
        List<String> args = new ArrayList<>(List.of(
            command, server.url(), "--fingerprint", server.fingerprint(), "--timeout", "5"));
        args.addAll(Arrays.asList(options));
        return run(args.toArray(String[]::new));
    }

    @SuppressWarnings("unchecked")
    private static List<Map<String, Object>> objects(Object json) {
        return (List<Map<String, Object>>) json;
    }

    // ------------------------------------------------------------------
    // Arguments
    // ------------------------------------------------------------------

    @ParameterizedTest
    @ValueSource(strings = {
        "",
        "unknown",
        "status",
        "status URL --password secret123",
        "messages URL --direction sideways",
        "messages URL --limit 0",
        "messages URL --limit many",
        "messages URL --since yesterday",
        "messages URL --sim",
        "messages URL --unread=yes",
        "send URL --text no-recipient",
        "delete URL",
        "delete URL --message 1 --conversation 2",
        "mark-read URL",
        "mark-read URL --conversation first",
        "listen URL --events sms.unknown",
        "listen URL --events",
        "status URL --timeout soon",
        "status URL extra",
        "fingerprint",
    })
    void usageErrors(String line) {
        String[] args = line.isEmpty() ? new String[0] : line.replace("URL", URL).split(" ");
        Result result = run(args);
        assertEquals(2, result.code(), result.err());
        assertEquals("", result.out());
        assertTrue(result.err().startsWith("usage: smsdock"), result.err());
        assertTrue(result.err().contains(": error: "), result.err());
        assertEquals(0, server.connectionCount());
    }

    @Test
    void usageMessages() {
        assertTrue(run("messages", URL, "--limit", "0").err()
            .endsWith("smsdock messages: error: argument --limit: must be a number from 1\n"));
        assertTrue(run("messages", URL, "--direction", "up").err().contains(
            "argument --direction: invalid choice: 'up' (choose from in, out, all)"));
        assertTrue(run("send", URL, "--text", "x").err()
            .contains("the following arguments are required: --to"));
        assertTrue(run("status").err().contains("the following arguments are required: url"));
        assertTrue(run("delete", URL, "--message", "1", "--conversation", "2").err()
            .contains("argument --conversation: not allowed with argument --message"));
        assertTrue(run("delete", URL).err()
            .contains("one of the arguments --message --conversation is required"));
        assertTrue(run("mark-read", URL, "--conversation", "first").err()
            .contains("argument --conversation: invalid int value: 'first'"));
    }

    @Test
    void help() {
        Result result = run("--help");
        assertEquals(0, result.code());
        assertTrue(result.out().startsWith("usage: smsdock"));
        for (String command : List.of("fingerprint", "status", "conversations", "messages",
                "send", "delete", "mark-read", "contacts", "listen")) {
            assertTrue(result.out().contains(command), command);
        }
        assertTrue(result.out().contains("SMSDOCK_PASSWORD"));
        result = run("send", "--help");
        assertEquals(0, result.code());
        assertTrue(result.out().contains("--wait-timeout SECONDS"));
        assertFalse(result.out().contains("--password "));
        assertEquals(0, run("listen", URL, "-h").code());
    }

    @Test
    void thePasswordIsNoArgument() {
        Result result = run("status", URL, "--fingerprint", FINGERPRINT, "--password", PASSWORD);
        assertEquals(2, result.code());
        assertTrue(result.err().contains("unrecognized arguments: --password " + PASSWORD));
    }

    @ParameterizedTest
    @ValueSource(strings = {
        "ws://192.168.1.50:9443/ws", "https://192.168.1.50:8443/", "192.168.1.50"})
    void refusesOtherUrls(String url) {
        Result result = run("status", url, "--fingerprint", FINGERPRINT);
        assertEquals(2, result.code());
        assertTrue(result.err().contains("wss://"));
        assertEquals("", result.out());
    }

    @Test
    void needsAFingerprint() {
        Result result = run("status", URL);
        assertEquals(2, result.code());
        assertTrue(result.err().contains("--fingerprint"));
        assertTrue(result.err().contains("SMSDOCK_FINGERPRINT"));
        result = run("status", URL, "--fingerprint", "AB:CD");
        assertEquals(2, result.code());
        assertTrue(result.err().contains("SHA-256"));
        assertEquals(2, run("status", URL, "--fingerprint", FINGERPRINT,
            "--reconnect-attempts", "-2").code());
        assertEquals(2, run("status", URL, "--fingerprint", FINGERPRINT,
            "--timeout", "0").code());
    }

    @Test
    void needsAPassword() {
        environment.remove("SMSDOCK_PASSWORD");
        Result result = run("status", URL, "--fingerprint", FINGERPRINT);
        assertEquals(2, result.code());
        assertEquals("smsdock: No password. Set the environment variable SMSDOCK_PASSWORD.\n",
            result.err());
    }

    @Test
    void asksForThePasswordOnATerminal() {
        environment.remove("SMSDOCK_PASSWORD");
        List<String> prompts = new ArrayList<>();
        prompt = text -> {
            prompts.add(text);
            return PASSWORD;
        };
        Result result = dock("status", "--json");
        assertEquals(0, result.code(), result.err());
        assertEquals("hidden", ((Map<?, ?>) result.json()).get("deleteMode"));
        assertEquals(List.of("SMS Dock password: "), prompts);
        assertFalse((result.out() + result.err()).contains(PASSWORD));
    }

    @Test
    void otherPasswordVariable() {
        environment.put("SMSDOCK_PASSWORD", "wrong-password");
        environment.put("OFFICE_PHONE", PASSWORD);
        assertEquals(0, dock("status", "--password-env", "OFFICE_PHONE").code());
        assertEquals(1, server.loginCount());
    }

    @ParameterizedTest
    @CsvSource({
        "192.168.1.50, 192.168.1.50, 9443",
        "192.168.1.50:9555, 192.168.1.50, 9555",
        "phone.local:9443, phone.local, 9443",
        "wss://192.168.1.50:9555/ws, 192.168.1.50, 9555",
        "wss://192.168.1.50/ws, 192.168.1.50, 9443",
        "[fe80::1]:9555, fe80::1, 9555",
        "fe80::1, fe80::1, 9443",
    })
    void hostAndPort(String text, String host, int port) {
        assertArrayEquals(new Object[] {host, port}, SmsDockCli.hostAndPort(text));
    }

    @ParameterizedTest
    @ValueSource(strings = {"", ":9443", "192.168.1.50:port", "[fe80::1"})
    void hostAndPortErrors(String text) {
        assertThrows(SmsDockCli.UsageError.class, () -> SmsDockCli.hostAndPort(text));
    }

    @Test
    void timestamps() {
        assertEquals(1790672531000L, SmsDockCli.timestamp("1790672531000"));
        long day = SmsDockCli.timestamp("2026-09-29");
        assertEquals(day + 8 * 3_600_000L, SmsDockCli.timestamp("2026-09-29T08:00"));
        assertEquals(day + 8 * 3_600_000L + 30_000, SmsDockCli.timestamp("2026-09-29 08:00:30"));
        assertEquals(1790668800000L, SmsDockCli.timestamp("2026-09-29T08:00:00+00:00"));
        assertNull(SmsDockCli.timestamp("yesterday"));
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    @Test
    void fingerprint() {
        Result result = run("fingerprint", "127.0.0.1:" + server.getPort());
        assertEquals(0, result.code(), result.err());
        assertEquals(server.fingerprint() + "\n", result.out());
        assertTrue(result.err().contains("Compare"));
        result = run("fingerprint", server.url(), "--timeout", "5");
        assertEquals(0, result.code());
        assertEquals(server.fingerprint() + "\n", result.out());
        assertEquals(List.of(), server.requests());

        int port = server.getPort();
        server.end();
        result = run("fingerprint", "127.0.0.1:" + port, "--timeout", "2");
        assertEquals(3, result.code());
        assertEquals("", result.out());
        assertTrue(result.err().startsWith("smsdock: "));
    }

    @Test
    void status() {
        Result result = dock("status");
        assertEquals(0, result.code(), result.err());
        assertEquals(String.join("\n",
            "App:             SMS Dock 1.0.0 (protocol 1)",
            "Default SMS app: no",
            "Delete mode:     hidden",
            "Licence:         not enforced",
            "Permissions:     all granted",
            "",
            "SIM  SLOT  NAME  NUMBER          DEFAULT",
            "1    0     Play                  yes",
            "2    1     Work  +4369087654321  no",
            ""), result.out());
        assertEquals("", result.err());
        assertFalse(result.out().contains(PASSWORD));

        Map<?, ?> status = (Map<?, ?>) dock("status", "--json").json();
        assertEquals(1L, ((Map<?, ?>) status.get("app")).get("protocolVersion"));
        assertEquals(2, ((List<?>) status.get("sims")).size());
    }

    @Test
    void conversations() {
        Result result = dock("conversations");
        assertEquals(0, result.code(), result.err());
        List<String> lines = result.lines();
        assertEquals(List.of("ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT"),
            List.of(lines.get(0).split("\\s+")));
        assertEquals(4, lines.size());
        assertTrue(lines.get(1).contains(ANNA) && lines.get(1).contains("Anna Example"));
        assertTrue(lines.get(1).endsWith("  Anna Example  25    3       in   Anna 24"),
            lines.get(1));
        assertTrue(lines.get(3).contains("A1"));

        result = dock("conversations", "--json", "--unread", "--search", "bill", "--limit", "5");
        assertEquals(List.of("A1"), field(objects(result.json()), "address"));
        result = dock("conversations", "--sim", "2", "--json");
        assertEquals(List.of(BEN), field(objects(result.json()), "address"));
    }

    @Test
    void lastTenIncomingMessages() {
        Result result = dock("messages", "--direction", "in", "--limit", "10");
        assertEquals(0, result.code(), result.err());
        List<String> lines = result.lines();
        assertEquals(List.of("ID", "TIME", "DIR", "NUMBER", "NAME", "STATUS", "TEXT"),
            List.of(lines.get(0).split("\\s+")));
        assertEquals(11, lines.size());
        assertTrue(lines.subList(1, 11).stream().allMatch(line -> line.contains("  in ")));
        assertTrue(lines.get(1).endsWith("received  Anna 24"));
        assertTrue(lines.get(10).endsWith("received  Anna 6"));
        assertTrue(lines.get(1).matches(
            "\\d+  \\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}  in   \\+4369912345678  Anna Example  .*"),
            lines.get(1));
    }

    @Test
    void messagesJsonAndFilters() {
        Result result = dock("messages", "--json", "--address", BEN, "--search", "invoice",
            "--direction", "out", "--sim", "2");
        assertEquals(0, result.code(), result.err());
        assertEquals(List.of("Your invoice is attached"),
            field(objects(result.json()), "body"));
        assertTrue(result.out().startsWith("[\n  {\n    \"id\": "));
        Map<String, Object> request = server.requests("messages.list").get(0);
        assertEquals(BEN, request.get("address"));
        assertEquals("invoice", request.get("q"));
        assertEquals("out", request.get("direction"));
        assertEquals(2L, request.get("sim"));

        assertEquals(4, objects(dock("messages", "--json", "--unread").json()).size());
        assertEquals(List.of("Anna 24", "Anna 23", "Anna 22"), field(objects(
            dock("messages", "--json", "--since", "1790672553000").json()), "body"));
        assertEquals(3, objects(dock("messages", "--json", "--conversation", "171").json())
            .size());
    }

    @Test
    void messagesLimitOverOnePage() {
        for (int index = 0; index < 230; index++) {
            server.addMessage(ANNA, "More " + index);
        }
        Result result = dock("messages", "--json", "--limit", "220");
        assertEquals(0, result.code(), result.err());
        assertEquals(220, objects(result.json()).size());
        assertEquals(List.of(200L, 20L), field(server.requests("messages.list"), "limit"));
    }

    @Test
    void nothingFound() {
        Result result = dock("messages", "--search", "no such text");
        assertEquals(0, result.code());
        assertEquals("Nothing found.\n", result.out());
        result = dock("messages", "--search", "no such text", "--json");
        assertEquals("[]\n", result.out());
    }

    @Test
    void longTextIsCutInTables() {
        server.addMessage(ANNA, "First line\nsecond line " + "x".repeat(100));
        Result result = dock("messages", "--limit", "1");
        String line = result.lines().get(1);
        assertTrue(line.contains("First line second line xxx"));
        assertTrue(line.endsWith("…"));
        String text = line.substring(line.indexOf("First line"));
        assertEquals(60, text.length());
        result = dock("messages", "--limit", "1", "--json");
        assertTrue(((String) objects(result.json()).get(0).get("body"))
            .startsWith("First line\nsecond line"));
        assertTrue(result.out().contains("\"body\": \"First line\\nsecond line xxx"));
    }

    @Test
    void send() {
        Result result = dock("send", "--to", BEN, "--text", "All systems running.");
        assertEquals(0, result.code(), result.err());
        String requestId = (String) server.sent().get(0).get("requestId");
        assertEquals("Queued: " + BEN + ", 1 part(s), requestId " + requestId + "\n",
            result.out());
        assertEquals("All systems running.", server.sent().get(0).get("body"));
    }

    @Test
    void sendFromStandardInput() {
        input = "Line one\nLine two\n";
        Result result = dock("send", "--to", BEN, "--sim", "2", "--request-id", "cron-1",
            "--json");
        assertEquals(0, result.code(), result.err());
        assertEquals(FakeSmsDock.object("requestId", "cron-1", "to", BEN, "parts", 1L,
            "status", "queued"), result.json());
        assertEquals(List.of(FakeSmsDock.object("requestId", "cron-1", "to", BEN,
            "body", "Line one\nLine two", "sim", 2L)), server.sent());
    }

    @Test
    void sendNeedsAText() {
        Result result = dock("send", "--to", BEN);
        assertEquals(2, result.code());
        assertTrue(result.err().contains("empty"));
        prompt = text -> PASSWORD;
        result = dock("send", "--to", BEN);
        assertEquals(2, result.code());
        assertTrue(result.err().contains("No text"));
        assertEquals(List.of(), server.sent());
    }

    @Test
    void sendAndWait() {
        Result result = dock("send", "--to", BEN, "--text", "Hello", "--wait");
        assertEquals(0, result.code(), result.err());
        assertTrue(result.out().startsWith("Sent: " + BEN + ", 1 part(s), requestId "));
        result = dock("send", "--to", BEN, "--text", "Hello", "--wait", "--json");
        Map<?, ?> entry = (Map<?, ?>) result.json();
        assertEquals("sent", entry.get("status"));
        assertEquals(1L, entry.get("partsSent"));
    }

    @Test
    void sendAndWaitFailed() {
        server.failNumbers.add("+43123456");
        Result result = dock("send", "--to", "+43123456", "--text", "Hello", "--wait");
        assertEquals(1, result.code());
        assertEquals("", result.out());
        assertTrue(result.err().matches(
            "smsdock: Sending to \\+43123456 failed \\(error code 1, requestId \\w+\\)\n"),
            result.err());
        result = dock("send", "--to", "+43123456", "--text", "Hello", "--wait", "--json");
        assertEquals(1, result.code());
        assertEquals("failed", ((Map<?, ?>) result.json()).get("status"));
    }

    @Test
    void sendAgainTellsTheOldOutcome() {
        server.failNumbers.add("+43123456");
        String[] options = {"--to", "+43123456", "--text", "Hello", "--request-id", "job-1"};
        Result result = dock("send", options);
        assertEquals(0, result.code());
        assertEquals("Queued: +43123456, 1 part(s), requestId job-1\n", result.out());
        assertTrue(waitUntil(() -> "failed".equals(server.outboxEntry("job-1").get("status"))));
        server.failNumbers.clear();
        // The repeat sends nothing and reports the failure of the first attempt.
        result = dock("send", options);
        assertEquals(1, result.code());
        assertEquals("", result.out());
        assertEquals("smsdock: Sending to +43123456 failed (requestId job-1)\n", result.err());
        assertEquals(1, server.sent().size());

        assertEquals(0, dock("send", "--to", BEN, "--text", "Hello", "--request-id", "job-2",
            "--wait").code());
        result = dock("send", "--to", BEN, "--text", "Hello", "--request-id", "job-2");
        assertEquals(0, result.code());
        assertEquals("Sent: " + BEN + ", 1 part(s), requestId job-2\n", result.out());
        assertEquals(2, server.sent().size());
    }

    @Test
    void sendAndWaitTimesOut() {
        FakeSmsDock slow = newServer();
        slow.sendDelayMillis = 30_000;
        slow.begin();
        Result result = run("send", slow.url(), "--fingerprint", slow.fingerprint(),
            "--to", BEN, "--text", "Hello", "--wait", "--wait-timeout", "0.3");
        assertEquals(3, result.code());
        assertTrue(result.err().contains("No result"));
    }

    @Test
    void delete() {
        long newest = (Long) server.messages().get(24).get("id");
        Result result = dock("delete", "--message", String.valueOf(newest));
        assertEquals(0, result.code(), result.err());
        assertEquals("Deleted 1 message(s): hidden in SMS Dock, still on the phone\n",
            result.out());
        assertEquals(28, server.messages().size());

        server.deleteMode = "provider";
        result = dock("delete", "--conversation", "170");
        assertEquals("Deleted 24 message(s): removed from the phone\n", result.out());
        result = dock("delete", "--conversation=171", "--json");
        assertEquals(Map.of("deleted", 3L, "mode", "provider"), result.json());
        assertEquals("{\n  \"deleted\": 3,\n  \"mode\": \"provider\"\n}\n", result.out());
    }

    @Test
    void markRead() {
        Result result = dock("mark-read", "--conversation", "170");
        assertEquals(1, result.code());
        assertEquals("", result.out());
        assertTrue(result.err().startsWith("smsdock: not_default_app: "));

        server.deleteMode = "provider";
        result = dock("mark-read", "--conversation", "170");
        assertEquals(0, result.code());
        assertEquals("Marked conversation 170 as read\n", result.out());
        assertTrue(server.messages().stream()
            .filter(message -> message.get("conversationId").equals(170L))
            .allMatch(message -> Boolean.TRUE.equals(message.get("read"))));
        result = dock("mark-read", "--conversation", "170", "--json");
        assertEquals(Map.of("ok", true), result.json());
    }

    @Test
    void contacts() {
        Result result = dock("contacts");
        assertEquals(0, result.code(), result.err());
        assertEquals(String.join("\n",
            "NAME          NUMBER          LABEL",
            "Anna Example  " + ANNA + "  Mobile",
            "Ben Example   " + BEN + "  Work",
            ""), result.out());
        result = dock("contacts", "--search", "ben", "--json");
        assertEquals(List.of(FakeSmsDock.object("name", "Ben Example", "numbers",
            List.of(FakeSmsDock.object("number", BEN, "label", "Work")))), result.json());
    }

    /** Lets messages arrive as soon as a client has subscribed. */
    private Thread incomingLater(String... addressesAndBodies) {
        return later(() -> {
            if (waitUntil(() -> !server.requests("subscribe").isEmpty(), 5000)) {
                for (int index = 0; index < addressesAndBodies.length; index += 2) {
                    server.receiveSms(addressesAndBodies[index], addressesAndBodies[index + 1]);
                }
            }
        });
    }

    @Test
    void listen() throws Exception {
        Thread thread = incomingLater(ANNA, "STATUS", "A1", "Your bill");
        Result result = dock("listen", "--count", "2");
        thread.join();
        assertEquals(0, result.code(), result.err());
        List<String> lines = result.lines();
        assertEquals(2, lines.size());
        assertTrue(lines.get(0).matches(
            "\\d{4}-\\d{2}-\\d{2} \\d{2}:\\d{2}:\\d{2}  \\+4369912345678 \\(Anna Example\\): "
                + "STATUS"), lines.get(0));
        assertTrue(lines.get(1).endsWith("  A1: Your bill"));
        assertEquals("Listening for sms.received. Press Ctrl+C to stop.\n", result.err());
        Map<String, Object> request = server.requests("subscribe").get(0);
        assertEquals(List.of("sms.received"), request.get("events"));
        assertFalse(request.containsKey("address"));
    }

    @Test
    void listenJsonWithAddress() throws Exception {
        Thread thread = incomingLater(ANNA, "ignored", BEN, "wanted");
        Result result = dock("listen", "--json", "--address", BEN, "--count", "1");
        thread.join();
        assertEquals(0, result.code(), result.err());
        assertEquals("", result.err());
        Map<?, ?> event = (Map<?, ?>) result.json();
        assertEquals("sms.received", event.get("event"));
        assertEquals("wanted",
            ((Map<?, ?>) ((Map<?, ?>) event.get("data")).get("message")).get("body"));
        assertEquals(1, result.lines().size());
        // One line, written as Python's json.dumps does.
        assertTrue(result.out().startsWith(
            "{\"event\": \"sms.received\", \"seq\": 2, \"data\": {\"message\": {\"id\": "));
    }

    @Test
    void listenToOtherEvents() throws Exception {
        Thread thread = later(() -> {
            if (waitUntil(() -> !server.requests("subscribe").isEmpty(), 5000)) {
                server.emit("sms.delivered", FakeSmsDock.object(
                    "requestId", "r1", "id", 7L, "conversationId", 171L, "to", BEN));
            }
        });
        Result result = dock("listen", "--events", "sms.delivered", "sms.failed",
            "--count", "1");
        thread.join();
        assertEquals(0, result.code(), result.err());
        assertTrue(result.out().stripTrailing().endsWith(
            "  sms.delivered requestId=r1 id=7 conversationId=171 to=" + BEN), result.out());
        assertTrue(result.err().startsWith("Listening for sms.delivered, sms.failed."));
        assertEquals(List.of("sms.delivered", "sms.failed"),
            server.requests("subscribe").get(0).get("events"));
    }

    @Test
    void describesEvents() {
        assertTrue(SmsDockCli.describe(FakeSmsDock.object("event", "sms.failed", "data",
            FakeSmsDock.object("requestId", null, "id", null, "errorCode", 1L,
                "reason", "generic_failure")))
            .endsWith("  sms.failed requestId=None id=None errorCode=1 reason=generic_failure"));
        assertTrue(SmsDockCli.describe(FakeSmsDock.object("event", "conversations.changed",
            "data", FakeSmsDock.object())).endsWith("  conversations.changed"));
        assertTrue(SmsDockCli.describe(FakeSmsDock.object("event", "sms.sent", "data",
            FakeSmsDock.object("requestId", "r1", "message",
                FakeSmsDock.object("address", BEN, "body", "Hello"))))
            .endsWith("  sent to " + BEN + ": Hello"));
    }

    @Test
    void listenReportsAReconnect() throws Exception {
        Thread thread = later(() -> {
            if (!waitUntil(() -> server.requests().size() >= 2, 5000)) {
                return;
            }
            server.dropConnections(null, "");
            waitUntil(() -> server.clientCount() == 0);
            server.receiveSms(ANNA, "lost");
            server.receiveSms(ANNA, "lost too");
            if (waitUntil(() -> server.requests().size() >= 4, 5000)) {
                server.restart();
            }
            if (waitUntil(() -> server.requests().size() >= 6, 5000)) {
                server.receiveSms(ANNA, "received");
            }
        });
        Result result = dock("listen", "--count", "1");
        thread.join();
        assertEquals(0, result.code(), result.err());
        assertTrue(result.out().stripTrailing().endsWith(": received"));
        List<String> lines = result.err().lines().toList();
        assertEquals("smsdock: reconnected; 2 event(s) of the phone were not received",
            lines.get(1));
        assertEquals("smsdock: reconnected; the app was restarted, events may be missing",
            lines.get(2));
    }

    // ------------------------------------------------------------------
    // Exit codes
    // ------------------------------------------------------------------

    @Test
    void exitCode1ForApiErrors() {
        Result result = dock("delete", "--message", "1");
        assertEquals(1, result.code());
        assertEquals("", result.out());
        assertTrue(result.err().startsWith("smsdock: not_found: "));

        result = dock("send", "--to", "abc", "--text", "x");
        assertEquals(1, result.code());
        assertTrue(result.err().contains("invalid_number"));

        server.failNext("messages.send", "quota_exceeded", "Daily limit reached.", null);
        result = dock("send", "--to", BEN, "--text", "x");
        assertEquals(1, result.code());
        assertEquals("smsdock: quota_exceeded: Daily limit reached.\n", result.err());
    }

    @Test
    void exitCode1ForAWrongPassword() {
        environment.put("SMSDOCK_PASSWORD", "wrong-password");
        Result result = dock("status");
        assertEquals(1, result.code());
        assertEquals("", result.out());
        assertTrue(result.err().contains("invalid_password"));
        assertFalse(result.err().contains("wrong-password"));
        assertEquals(1, server.loginCount());
    }

    @Test
    void exitCode1WithoutLicence() {
        FakeSmsDock unlicensed = newServer();
        unlicensed.licenceEnforced = true;
        unlicensed.begin();
        Result result = run("status", unlicensed.url(), "--fingerprint",
            unlicensed.fingerprint());
        assertEquals(1, result.code());
        assertTrue(result.err().contains("licence_required"));
    }

    @Test
    void exitCode3WhenNothingListens() {
        int port = server.getPort();
        server.end();
        Result result = run("status", "wss://127.0.0.1:" + port + "/ws",
            "--fingerprint", FINGERPRINT, "--reconnect-attempts", "0");
        assertEquals(3, result.code());
        assertEquals("", result.out());
        assertTrue(result.err().startsWith("smsdock: Cannot connect to wss://127.0.0.1:"));
    }

    @Test
    void exitCode3ForAnotherCertificate() {
        String other = TestCertificate.other().fingerprint();
        Result result = run("status", server.url(), "--fingerprint", other);
        assertEquals(3, result.code());
        assertEquals("", result.out());
        assertTrue(result.err().contains(server.fingerprint()));
        assertTrue(result.err().contains(other));
        assertEquals(List.of(), server.requests());
        assertEquals(0, server.connectionCount());
    }

    @Test
    void fingerprintFromTheEnvironmentIsUsed() {
        environment.put("SMSDOCK_FINGERPRINT", server.fingerprint().toLowerCase());
        assertEquals(0, run("status", server.url()).code());
        environment.put("SMSDOCK_FINGERPRINT", TestCertificate.other().fingerprint());
        assertEquals(3, run("status", server.url()).code());
        assertEquals(0, run("status", server.url(), "--fingerprint", server.fingerprint())
            .code());
    }

    @Test
    void insecureWarns() {
        PrintStream original = System.err;
        ByteArrayOutputStream captured = new ByteArrayOutputStream();
        System.setErr(new PrintStream(captured, true, StandardCharsets.UTF_8));
        Result result;
        try {
            result = run("status", server.url(), "--insecure", "--timeout", "5");
        } finally {
            System.setErr(original);
        }
        assertEquals(0, result.code(), result.err());
        String warning = captured.toString(StandardCharsets.UTF_8);
        assertTrue(warning.startsWith("smsdock: WARNING:"));
        assertTrue(warning.contains("not checked"));
        assertTrue(result.out().contains("App:"));
        assertNotNull(result.err());
    }
}
