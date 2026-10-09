package app.flutterdev.smsdock;

import java.util.Map;

/**
 * The login failed or the connection is no longer logged in.
 *
 * <p>Codes: {@code invalid_password}, {@code locked_out} (see {@link #getRetryAfter()}) and
 * {@code not_authenticated}. The client never retries after this error, because every wrong
 * password counts towards the lock of the phone.
 */
public class AuthenticationException extends ApiException {

    /**
     * Creates the error.
     *
     * @param code error code of {@code API.md}
     * @param message text of the server
     * @param details other fields of the error object, or {@code null}
     */
    public AuthenticationException(String code, String message, Map<String, Object> details) {
        super(code, message, details);
    }
}
