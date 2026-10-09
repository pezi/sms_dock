package app.flutterdev.smsdock;

/**
 * The connection failed and could not be restored.
 *
 * <p>{@link #getCloseCode()} and {@link #getCloseReason()} are set when the server closed the
 * WebSocket with a close frame.
 */
public class ConnectionInterruptedException extends SmsDockException {

    private final Integer closeCode;
    private final String closeReason;

    /**
     * Creates the error.
     *
     * @param message what went wrong
     * @param closeCode close code of the server, or {@code null}
     * @param closeReason close reason of the server, or {@code null}
     * @param cause the error behind it, or {@code null}
     */
    public ConnectionInterruptedException(
            String message, Integer closeCode, String closeReason, Throwable cause) {
        super(message, cause);
        this.closeCode = closeCode;
        this.closeReason = closeReason;
    }

    /**
     * Creates the error without a close frame.
     *
     * @param message what went wrong
     */
    public ConnectionInterruptedException(String message) {
        this(message, null, null, null);
    }

    /**
     * Returns the close code of the server.
     *
     * @return the code, or {@code null} without a close frame
     */
    public Integer getCloseCode() {
        return closeCode;
    }

    /**
     * Returns the close reason of the server.
     *
     * @return the reason, or {@code null} without one
     */
    public String getCloseReason() {
        return closeReason;
    }
}
