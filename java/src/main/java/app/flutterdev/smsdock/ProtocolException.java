package app.flutterdev.smsdock;

/** The server sent something that protocol version 1 does not allow. */
public class ProtocolException extends SmsDockException {

    /**
     * Creates the error.
     *
     * @param message what went wrong
     */
    public ProtocolException(String message) {
        super(message);
    }

    /**
     * Creates the error with its cause.
     *
     * @param message what went wrong
     * @param cause the error behind it, or {@code null}
     */
    public ProtocolException(String message, Throwable cause) {
        super(message, cause);
    }
}
