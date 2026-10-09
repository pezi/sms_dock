import app.flutterdev.smsdock.ApiException;
import app.flutterdev.smsdock.SmsDockClient;
import app.flutterdev.smsdock.SmsDockException;
import app.flutterdev.smsdock.SmsDockTimeoutException;
import java.time.Duration;
import java.util.Map;

/**
 * Send one SMS and wait for the result.
 *
 * <pre>
 * export SMSDOCK_PASSWORD='your password'
 * export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 * java -cp target/smsdock.jar examples/SendSms.java wss://192.168.1.50:9443/ws \
 *     +4369912345678 "Hello"
 * </pre>
 */
public final class SendSms {

    private static final String USAGE = String.join("\n",
        "Send one SMS and wait for the result.",
        "",
        "Usage:",
        "",
        "    export SMSDOCK_PASSWORD='your password'",
        "    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app",
        "    java -cp target/smsdock.jar examples/SendSms.java wss://192.168.1.50:9443/ws \\",
        "        +4369912345678 \"Hello\"");

    private SendSms() {
    }

    public static void main(String[] args) {
        try {
            System.exit(run(args));
        } catch (SmsDockException | SmsDockTimeoutException | IllegalArgumentException error) {
            System.err.println("SendSms: " + error.getMessage());
            System.exit(1);
        }
    }

    static int run(String[] args) {
        if (args.length != 3) {
            System.out.println(USAGE);
            return 2;
        }
        String url = args[0];
        String number = args[1];
        String text = args[2];

        // Password and fingerprint come from the environment.
        Map<String, Object> outcome;
        try (SmsDockClient dock = new SmsDockClient(url).connect()) {
            Map<String, Object> result;
            try {
                // send() returns as soon as the phone has queued the message. The client
                // adds a random requestId, so a repeat after a lost connection cannot send
                // the message twice. Pass your own requestId if the whole program may run
                // again: dock.send(number, text, null, "order-4711").
                result = dock.send(number, text);
            } catch (ApiException error) {
                // For example invalid_number, message_too_long, quota_exceeded.
                System.out.println("Not sent: " + error.getCode() + ": "
                    + error.getServerMessage());
                return 1;
            }
            System.out.println(result.get("parts") + " part(s) for " + result.get("to"));

            // Wait until the network accepted the message or refused it.
            outcome = dock.waitForResult(
                (String) result.get("requestId"), Duration.ofSeconds(60));
        }

        if ("failed".equals(outcome.get("status"))) {
            System.out.println("Failed, error code " + outcome.get("errorCode"));
            return 1;
        }
        System.out.println("Sent, message id " + outcome.get("messageId"));
        return 0;
    }
}
