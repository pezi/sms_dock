package app.flutterdev.smsdock;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.Strictness;
import com.google.gson.ToNumberPolicy;
import com.google.gson.stream.JsonReader;
import com.google.gson.stream.JsonToken;
import java.io.IOException;
import java.io.StringReader;
import java.util.Iterator;
import java.util.Map;

/**
 * JSON as the protocol needs it.
 *
 * <p>Objects are read as {@code Map<String, Object>} in the order of the server, arrays as
 * {@code List<Object>}, integers as {@code Long}, other numbers as {@code Double}. Frames are
 * written compact, with {@code null} values kept and without escaping of non-ASCII text.
 */
final class Json {

    private static final Gson GSON = new GsonBuilder()
        .setObjectToNumberStrategy(ToNumberPolicy.LONG_OR_DOUBLE)
        .serializeNulls()
        .disableHtmlEscaping()
        .create();

    private Json() {
    }

    /**
     * Reads one strict JSON document.
     *
     * @throws IOException if the text is not valid JSON
     */
    static Object parse(String text) throws IOException {
        try {
            JsonReader reader = new JsonReader(new StringReader(text));
            reader.setStrictness(Strictness.STRICT);
            Object value = GSON.getAdapter(Object.class).read(reader);
            if (reader.peek() != JsonToken.END_DOCUMENT) {
                throw new IOException("Text after the JSON document");
            }
            return value;
        } catch (RuntimeException error) {
            throw new IOException(error);
        }
    }

    /** Writes a value as one compact line, the form of the frames. */
    static String compact(Object value) {
        return GSON.toJson(value);
    }

    /** Writes a value over several lines, indented by two spaces, for people to read. */
    static String pretty(Object value) {
        StringBuilder text = new StringBuilder();
        write(text, value, true, 0);
        return text.toString();
    }

    /** Writes a value as one line with a space after each comma and colon. */
    static String line(Object value) {
        StringBuilder text = new StringBuilder();
        write(text, value, false, 0);
        return text.toString();
    }

    private static void write(StringBuilder text, Object value, boolean pretty, int level) {
        if (value instanceof Map) {
            Map<?, ?> map = (Map<?, ?>) value;
            if (map.isEmpty()) {
                text.append("{}");
                return;
            }
            text.append('{');
            Iterator<? extends Map.Entry<?, ?>> entries = map.entrySet().iterator();
            while (entries.hasNext()) {
                Map.Entry<?, ?> entry = entries.next();
                lineBreak(text, pretty, level + 1);
                quote(text, String.valueOf(entry.getKey()));
                text.append(": ");
                write(text, entry.getValue(), pretty, level + 1);
                if (entries.hasNext()) {
                    text.append(pretty ? "," : ", ");
                }
            }
            lineBreak(text, pretty, level);
            text.append('}');
        } else if (value instanceof Iterable) {
            Iterator<?> items = ((Iterable<?>) value).iterator();
            if (!items.hasNext()) {
                text.append("[]");
                return;
            }
            text.append('[');
            while (items.hasNext()) {
                lineBreak(text, pretty, level + 1);
                write(text, items.next(), pretty, level + 1);
                if (items.hasNext()) {
                    text.append(pretty ? "," : ", ");
                }
            }
            lineBreak(text, pretty, level);
            text.append(']');
        } else if (value instanceof CharSequence) {
            quote(text, value.toString());
        } else if (value == null || value instanceof Number || value instanceof Boolean) {
            text.append(value);
        } else {
            quote(text, value.toString());
        }
    }

    private static void lineBreak(StringBuilder text, boolean pretty, int level) {
        if (pretty) {
            text.append('\n');
            for (int count = 0; count < level; count++) {
                text.append("  ");
            }
        }
    }

    private static void quote(StringBuilder text, String value) {
        text.append('"');
        for (int index = 0; index < value.length(); index++) {
            char character = value.charAt(index);
            switch (character) {
                case '"' -> text.append("\\\"");
                case '\\' -> text.append("\\\\");
                case '\n' -> text.append("\\n");
                case '\r' -> text.append("\\r");
                case '\t' -> text.append("\\t");
                case '\b' -> text.append("\\b");
                case '\f' -> text.append("\\f");
                default -> {
                    if (character < 0x20) {
                        text.append(String.format("\\u%04x", (int) character));
                    } else {
                        text.append(character);
                    }
                }
            }
        }
        text.append('"');
    }
}
