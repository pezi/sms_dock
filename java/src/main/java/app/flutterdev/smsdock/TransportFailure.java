package app.flutterdev.smsdock;

/**
 * The connection failed: a lost socket, a close frame of the server or a response that did
 * not arrive. The client answers it with a reconnect or turns it into a public exception.
 */
class TransportFailure extends RuntimeException {

    private final Integer closeCode;
    private final String closeReason;

    TransportFailure(String message, Throwable cause) {
        this(message, null, null, cause);
    }

    TransportFailure(String message, Integer closeCode, String closeReason, Throwable cause) {
        super(message, cause);
        this.closeCode = closeCode;
        this.closeReason = closeReason == null || closeReason.isEmpty() ? null : closeReason;
    }

    /** Close code of the server, or {@code null} without a close frame. */
    Integer closeCode() {
        return closeCode;
    }

    /** Close reason of the server, or {@code null} without one. */
    String closeReason() {
        return closeReason;
    }

    /** Nothing arrived before the deadline; the connection itself may be fine. */
    static final class ReceiveTimeout extends TransportFailure {

        ReceiveTimeout(String message) {
            super(message, null);
        }
    }
}
