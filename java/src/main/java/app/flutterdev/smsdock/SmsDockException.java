package app.flutterdev.smsdock;

/** Base class of all errors raised by this package. */
public class SmsDockException extends RuntimeException {

    /**
     * Creates the error.
     *
     * @param message what went wrong
     */
    public SmsDockException(String message) {
        super(message);
    }

    /**
     * Creates the error with its cause.
     *
     * @param message what went wrong
     * @param cause the error behind it, or {@code null}
     */
    public SmsDockException(String message, Throwable cause) {
        super(message, cause);
    }
}
