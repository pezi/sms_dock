import app.flutterdev.smsdock.ApiException;
import app.flutterdev.smsdock.SmsDockClient;
import app.flutterdev.smsdock.SmsDockException;
import app.flutterdev.smsdock.SmsDockTimeoutException;
import java.time.Duration;
import java.util.Arrays;
import java.util.List;
import java.util.Map;

/**
 * Send one text to several numbers, one SMS each, and wait for the results.
 *
 * <pre>
 * export SMSDOCK_PASSWORD='your password'
 * export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
 * java -cp target/smsdock.jar examples/SendGroupSms.java wss://192.168.1.50:9443/ws \
 *     "Hello all" +4369912345678 +436641112223
 * </pre>
 */
public final class SendGroupSms {

    private static final String USAGE = String.join("\n",
        "Send one text to several numbers, one SMS each, and wait for the results.",
        "",
        "Usage:",
        "",
        "    export SMSDOCK_PASSWORD='your password'",
        "    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app",
        "    java -cp target/smsdock.jar examples/SendGroupSms.java wss://192.168.1.50:9443/ws \\",
        "        \"Hello all\" +4369912345678 +436641112223");

    private SendGroupSms() {
    }

    public static void main(String[] args) {
        try {
            System.exit(run(args));
        } catch (SmsDockException | SmsDockTimeoutException | IllegalArgumentException error) {
            System.err.println("SendGroupSms: " + error.getMessage());
            System.exit(1);
        }
    }

    static int run(String[] args) {
        if (args.length < 3) {
            System.out.println(USAGE);
            return 2;
        }
        String url = args[0];
        String text = args[1];
        List<String> numbers = Arrays.asList(args).subList(2, args.length);

        // Password and fingerprint come from the environment.
        int failed = 0;
        try (SmsDockClient dock = new SmsDockClient(url).connect()) {
            Map<String, Object> result;
            try {
                // sendGroup() returns as soon as the phone has queued one SMS per
                // recipient. The phone checks the whole group first: one bad number
                // refuses all of them, nothing is sent then. The client adds a random
                // requestId; the members get the ids requestId.1, requestId.2 and so on.
                result = dock.sendGroup(numbers, text);
            } catch (ApiException error) {
                // For example invalid_number, message_too_long, quota_exceeded.
                System.out.println("Not sent: " + error.getCode() + ": "
                    + error.getServerMessage());
                return 1;
            }
            System.out.println(((List<?>) result.get("to")).size() + " recipient(s), "
                + result.get("parts") + " part(s) each");

            // Wait for every member; a member the phone refused at once is already
            // failed and needs no waiting.
            for (Object item : (List<?>) result.get("messages")) {
                Map<?, ?> member = (Map<?, ?>) item;
                if ("failed".equals(member.get("status"))) {
                    System.out.println(member.get("to") + ": refused by the phone");
                    failed++;
                    continue;
                }
                Map<String, Object> outcome = dock.waitForResult(
                    (String) member.get("requestId"), Duration.ofSeconds(60));
                if ("failed".equals(outcome.get("status"))) {
                    System.out.println(member.get("to") + ": failed, error code "
                        + outcome.get("errorCode"));
                    failed++;
                } else {
                    System.out.println(member.get("to") + ": " + outcome.get("status")
                        + ", message id " + outcome.get("messageId"));
                }
            }
        }
        return failed > 0 ? 1 : 0;
    }
}
