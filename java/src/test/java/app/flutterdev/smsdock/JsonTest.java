package app.flutterdev.smsdock;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.IOException;
import java.util.ArrayList;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.params.ParameterizedTest;
import org.junit.jupiter.params.provider.ValueSource;

/** JSON as the protocol and the command line need it. */
class JsonTest {

    @Test
    void readsPlainValues() throws IOException {
        Map<?, ?> value = (Map<?, ?>) Json.parse(
            "{\"id\":2813,\"big\":1790672531000,\"ratio\":1.5,\"read\":false,"
                + "\"contact\":null,\"items\":[1,\"two\"],\"text\":\"Grüße \\u2026\"}");
        assertEquals(2813L, value.get("id"));
        assertEquals(1790672531000L, value.get("big"));
        assertEquals(1.5, value.get("ratio"));
        assertEquals(false, value.get("read"));
        assertTrue(value.containsKey("contact"));
        assertNull(value.get("contact"));
        assertEquals(List.of(1L, "two"), value.get("items"));
        assertEquals("Grüße …", value.get("text"));
        assertEquals(List.of("id", "big", "ratio", "read", "contact", "items", "text"),
            new ArrayList<>(value.keySet()));
    }

    @ParameterizedTest
    @ValueSource(strings = {"", "this is not JSON", "{\"a\":1} trailing", "{a:1}", "{'a':1}"})
    void refusesWhatIsNotJson(String text) {
        assertThrows(IOException.class, () -> Json.parse(text));
    }

    @Test
    void writesFramesCompactWithNulls() {
        Map<String, Object> frame = FakeSmsDock.object(
            "id", "java-1", "action", "subscribe", "events", List.of("sms.received"),
            "address", null, "text", "Grüße <&> 'x'", "limit", 10L);
        assertEquals("{\"id\":\"java-1\",\"action\":\"subscribe\",\"events\":[\"sms.received\"],"
            + "\"address\":null,\"text\":\"Grüße <&> 'x'\",\"limit\":10}", Json.compact(frame));
    }

    @Test
    void writesForPeople() {
        Map<String, Object> value = FakeSmsDock.object(
            "name", "A \"quoted\"\ttext\n", "count", 3L, "none", null, "yes", true,
            "empty", List.of(), "nothing", Map.of(),
            "numbers", List.of(FakeSmsDock.object("number", "+43", "label", "Mobile")));
        assertEquals(String.join("\n",
            "{",
            "  \"name\": \"A \\\"quoted\\\"\\ttext\\n\",",
            "  \"count\": 3,",
            "  \"none\": null,",
            "  \"yes\": true,",
            "  \"empty\": [],",
            "  \"nothing\": {},",
            "  \"numbers\": [",
            "    {",
            "      \"number\": \"+43\",",
            "      \"label\": \"Mobile\"",
            "    }",
            "  ]",
            "}"), Json.pretty(value));
        assertEquals("{\"name\": \"A \\\"quoted\\\"\\ttext\\n\", \"count\": 3, \"none\": null, "
            + "\"yes\": true, \"empty\": [], \"nothing\": {}, "
            + "\"numbers\": [{\"number\": \"+43\", \"label\": \"Mobile\"}]}", Json.line(value));
        assertEquals("[]", Json.pretty(List.of()));
        assertEquals("\"\\u0001\"", Json.line("\u0001"));
    }
}
