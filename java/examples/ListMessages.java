import app.flutterdev.smsdock.MessageFilter;
import app.flutterdev.smsdock.SmsDockClient;
import app.flutterdev.smsdock.SmsDockException;
import java.time.Instant;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.List;
import java.util.Map;

/**
 * Print the last 10 incoming SMS.
 *
 * <pre>
 * export SMSDOCK_PASSWORD='your password'
 * export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 * java -cp target/smsdock.jar examples/ListMessages.java wss://192.168.1.50:9443/ws
 * </pre>
 */
public final class ListMessages {

    private static final String USAGE = String.join("\n",
        "Print the last 10 incoming SMS.",
        "",
        "Usage:",
        "",
        "    export SMSDOCK_PASSWORD='your password'",
        "    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app",
        "    java -cp target/smsdock.jar examples/ListMessages.java wss://192.168.1.50:9443/ws");

    private static final DateTimeFormatter TIME =
        DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm").withZone(ZoneId.systemDefault());

    private ListMessages() {
    }

    public static void main(String[] args) {
        try {
            System.exit(run(args));
        } catch (SmsDockException | IllegalArgumentException error) {
            System.err.println("ListMessages: " + error.getMessage());
            System.exit(1);
        }
    }

    static int run(String[] args) {
        if (args.length != 1) {
            System.out.println(USAGE);
            return 2;
        }

        // The password comes from SMSDOCK_PASSWORD, the fingerprint from
        // SMSDOCK_FINGERPRINT. It pins the self-signed certificate of the phone.
        Map<String, Object> page;
        try (SmsDockClient dock = new SmsDockClient(args[0]).connect()) {
            page = dock.messages(new MessageFilter().direction("in"), 10);
        }

        for (Object item : (List<?>) page.get("items")) {
            Map<?, ?> message = (Map<?, ?>) item;
            // Timestamps are milliseconds since the epoch; show local time.
            String received = TIME.format(Instant.ofEpochMilli((Long) message.get("timestamp")));
            Object sender =
                message.get("contact") != null ? message.get("contact") : message.get("address");
            System.out.println(received + "  " + sender + ": " + message.get("body"));
        }

        // page.get("nextCursor") leads to the next 10; see iterMessages() for a loop that
        // follows it.
        return 0;
    }
}
