package app.flutterdev.smsdock;

/**
 * Nothing arrived in time.
 *
 * <p>Raised by {@link SmsDockClient#waitForEvent}, {@link SmsDockClient#waitForResult} and
 * {@link SmsDockClient#waitForResponse} when the time is over. The connection is still
 * usable, so this is not a {@link SmsDockException}.
 */
public class SmsDockTimeoutException extends RuntimeException {

    /**
     * Creates the error.
     *
     * @param message what did not arrive
     */
    public SmsDockTimeoutException(String message) {
        super(message);
    }
}
