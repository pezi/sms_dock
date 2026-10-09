import app.flutterdev.smsdock.ApiException;
import app.flutterdev.smsdock.SmsDockClient;
import app.flutterdev.smsdock.SmsDockException;
import java.util.ArrayDeque;
import java.util.HashMap;
import java.util.Locale;
import java.util.Map;
import java.util.regex.Pattern;

/**
 * Hook that answers incoming SMS.
 *
 * <p>An SMS with the text STATUS gets the answer "All systems running."; everything else
 * gets a short help text.
 *
 * <p>An answering machine can end up in a loop, for example with another answering machine
 * or with a service that answers every SMS. Three guards prevent that:
 *
 * <ol>
 *   <li>A number gets at most one answer in COOLDOWN_MINUTES.
 *   <li>Senders that are not ordinary phone numbers get no answer: names such as "A1" or
 *       "Post" and short codes such as 8810.
 *   <li>At most MAX_REPLIES_PER_HOUR answers are sent in total.
 * </ol>
 *
 * <pre>
 * export SMSDOCK_PASSWORD='your password'
 * export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 * java -cp target/smsdock.jar examples/AutoReply.java wss://192.168.1.50:9443/ws
 * </pre>
 *
 * <p>Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.
 */
public final class AutoReply {

    static final int COOLDOWN_MINUTES = 10;
    static final int MAX_REPLIES_PER_HOUR = 20;

    // A phone number: optional +, then 7 to 15 digits. Shorter ones are short codes;
    // anything with letters is a sender name that cannot be answered.
    static final Pattern PHONE_NUMBER = Pattern.compile("\\+?[0-9]{7,15}");

    private static final String USAGE = String.join("\n",
        "Hook that answers incoming SMS.",
        "",
        "An SMS with the text STATUS gets the answer \"All systems running.\";",
        "everything else gets a short help text.",
        "",
        "Usage:",
        "",
        "    export SMSDOCK_PASSWORD='your password'",
        "    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app",
        "    java -cp target/smsdock.jar examples/AutoReply.java wss://192.168.1.50:9443/ws",
        "",
        "Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.");

    private AutoReply() {
    }

    /** Decides whether a sender may get an answer now. */
    static final class ReplyGuard {

        private final double cooldownSeconds;
        private final int repliesPerHour;
        private final Map<String, Double> lastReply = new HashMap<>();
        private final ArrayDeque<Double> recent = new ArrayDeque<>();

        ReplyGuard(double cooldownSeconds, int repliesPerHour) {
            this.cooldownSeconds = cooldownSeconds;
            this.repliesPerHour = repliesPerHour;
        }

        /** Returns why this sender gets no answer now, or null if it gets one. */
        String refusal(String address) {
            return refusal(address, System.nanoTime() / 1e9);
        }

        /**
         * Returns why this sender gets no answer, or null if it gets one.
         *
         * @param now the time in seconds on a clock that never goes back
         */
        String refusal(String address, double now) {
            String number = address.replaceAll("[\\s\\-.()]", "");
            if (!PHONE_NUMBER.matcher(number).matches()) {
                return "not a phone number";
            }
            Double last = lastReply.get(number);
            if (last != null && now - last < cooldownSeconds) {
                return "answered a short time ago";
            }
            while (!recent.isEmpty() && now - recent.peekFirst() >= 3600) {
                recent.pollFirst();
            }
            if (recent.size() >= repliesPerHour) {
                return "too many answers in the last hour";
            }
            // Count the answer before it is sent: a failure must not open the door for a
            // loop.
            lastReply.put(number, now);
            recent.addLast(now);
            return null;
        }
    }

    /** Returns the answer to a message text. */
    static String answerFor(String text) {
        if (text.strip().toUpperCase(Locale.ROOT).equals("STATUS")) {
            return "All systems running.";
        }
        return "Commands: STATUS";
    }

    public static void main(String[] args) {
        try {
            System.exit(run(args));
        } catch (SmsDockException | IllegalArgumentException error) {
            System.err.println("AutoReply: " + error.getMessage());
            System.exit(1);
        }
    }

    static int run(String[] args) {
        if (args.length != 1) {
            System.out.println(USAGE);
            return 2;
        }

        ReplyGuard guard = new ReplyGuard(COOLDOWN_MINUTES * 60, MAX_REPLIES_PER_HOUR);
        try (SmsDockClient dock = SmsDockClient.builder(args[0])
                .reconnectWithoutLimit()
                .connect()) {
            dock.subscribe("sms.received");
            System.out.println("Answering SMS. Press Ctrl+C to stop.");

            // SMS that arrive while the connection is down get no answer: the phone does
            // not send events again, and a late answer helps nobody. ListenIncoming.java
            // shows how to read such messages.
            for (Map<String, Object> event : dock.events()) {
                Map<?, ?> message = (Map<?, ?>) ((Map<?, ?>) event.get("data")).get("message");
                String sender = (String) message.get("address");
                String refusal = guard.refusal(sender);
                if (refusal != null) {
                    System.out.println(sender + ": no answer (" + refusal + ")");
                    continue;
                }
                Map<String, Object> result;
                try {
                    // Answer on the SIM that received the message.
                    result = dock.send(sender, answerFor((String) message.get("body")),
                        (Long) message.get("sim"), null);
                } catch (ApiException error) {
                    System.out.println(sender + ": not answered, " + error.getCode() + ": "
                        + error.getServerMessage());
                    continue;
                }
                System.out.println(sender + ": answered, requestId " + result.get("requestId"));
            }
        }
        return 0;
    }
}
