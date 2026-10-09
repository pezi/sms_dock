import app.flutterdev.smsdock.MessageFilter;
import app.flutterdev.smsdock.SmsDockClient;
import app.flutterdev.smsdock.SmsDockException;
import java.time.Instant;
import java.time.ZoneId;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Hook for incoming SMS: call a method for every SMS that arrives.
 *
 * <pre>
 * export SMSDOCK_PASSWORD='your password'
 * export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 * java -cp target/smsdock.jar examples/ListenIncoming.java wss://192.168.1.50:9443/ws
 * </pre>
 *
 * <p>Stop with Ctrl+C.
 */
public final class ListenIncoming {

    private static final String USAGE = String.join("\n",
        "Hook for incoming SMS: call a method for every SMS that arrives.",
        "",
        "Usage:",
        "",
        "    export SMSDOCK_PASSWORD='your password'",
        "    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app",
        "    java -cp target/smsdock.jar examples/ListenIncoming.java wss://192.168.1.50:9443/ws",
        "",
        "Stop with Ctrl+C.");

    private static final DateTimeFormatter TIME =
        DateTimeFormatter.ofPattern("HH:mm:ss").withZone(ZoneId.systemDefault());

    /** Ids of the messages the hook has seen. */
    private final Set<Object> seen = new HashSet<>();

    /** Time of the newest message the hook has seen. */
    private long newest;

    private ListenIncoming() {
    }

    public static void main(String[] args) {
        try {
            System.exit(new ListenIncoming().run(args));
        } catch (SmsDockException | IllegalArgumentException error) {
            System.err.println("ListenIncoming: " + error.getMessage());
            System.exit(1);
        }
    }

    /** Your hook. {@code message} is a Message object of API.md. */
    static void onSms(Map<String, Object> message) {
        // Timestamps are milliseconds since the epoch; show local time.
        String received = TIME.format(Instant.ofEpochMilli((Long) message.get("timestamp")));
        Object sender =
            message.get("contact") != null ? message.get("contact") : message.get("address");
        System.out.println(received + "  " + sender + ": " + message.get("body"));
        System.out.flush();
    }

    /** Calls the hook once per message. */
    void handle(Map<String, Object> message) {
        Object id = message.get("id");
        if (seen.contains(id)) {
            return;
        }
        if (id != null) {
            seen.add(id);
        }
        newest = Math.max(newest, (Long) message.get("timestamp"));
        onSms(message);
    }

    /**
     * Reads what arrived while the connection was down.
     *
     * <p>The phone does not send events again. The client calls this method after every
     * reconnect, when the subscription is in force again.
     */
    void catchUp(SmsDockClient dock) {
        Long missedEvents = dock.getMissedEvents();
        if (dock.isServerRestarted() || missedEvents == null || missedEvents > 0) {
            // Messages of the same millisecond come again; handle() knows them.
            List<Map<String, Object>> missed = new ArrayList<>();
            dock.iterMessages(new MessageFilter().direction("in").since(newest))
                .forEach(missed::add);
            Collections.reverse(missed);  // oldest first
            missed.forEach(this::handle);
        }
    }

    @SuppressWarnings("unchecked")
    int run(String[] args) {
        if (args.length != 1) {
            System.out.println(USAGE);
            return 2;
        }

        // Password and fingerprint come from the environment.
        try (SmsDockClient dock = SmsDockClient.builder(args[0])
                // Keep trying when the phone is out of reach for a while.
                .reconnectWithoutLimit()
                .onReconnect(this::catchUp)
                .connect()) {
            // Only messages from now on: remember the time of the phone.
            newest = (Long) dock.ping().get("time");
            // A new connection gets no events. Ask for the ones you need.
            dock.subscribe("sms.received");
            System.out.println("Waiting for SMS. Press Ctrl+C to stop.");

            // events() yields forever. After a lost connection it reconnects, logs in and
            // subscribes again, then calls catchUp().
            for (Map<String, Object> event : dock.events()) {
                Map<String, Object> data = (Map<String, Object>) event.get("data");
                handle((Map<String, Object>) data.get("message"));
            }
        }
        return 0;
    }
}
