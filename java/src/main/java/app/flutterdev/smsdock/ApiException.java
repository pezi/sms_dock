package app.flutterdev.smsdock;

import java.util.Collections;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * The server answered a request with an error.
 *
 * <p>{@link #getCode()} is one of the error codes of {@code API.md},
 * {@link #getServerMessage()} the text of the server. {@link #getDetails()} holds the other
 * fields of the error object, for example {@code {"retryAfter": 30}} or
 * {@code {"permission": "sendSms"}}. {@link #getMessage()} is {@code "code: text"}.
 */
public class ApiException extends SmsDockException {

    private final String code;
    private final String serverMessage;
    private final Map<String, Object> details;

    /**
     * Creates the error.
     *
     * @param code error code of {@code API.md}
     * @param message text of the server
     * @param details other fields of the error object, or {@code null}
     */
    public ApiException(String code, String message, Map<String, Object> details) {
        super(code + ": " + message);
        this.code = code;
        this.serverMessage = message;
        this.details = Collections.unmodifiableMap(
            details == null ? new LinkedHashMap<>() : new LinkedHashMap<>(details));
    }

    /**
     * Returns the error code, for example {@code invalid_number}.
     *
     * @return the code
     */
    public String getCode() {
        return code;
    }

    /**
     * Returns the text of the server.
     *
     * @return the text
     */
    public String getServerMessage() {
        return serverMessage;
    }

    /**
     * Returns the other fields of the error object.
     *
     * @return an unmodifiable map, empty without such fields
     */
    public Map<String, Object> getDetails() {
        return details;
    }

    /**
     * Returns the seconds to wait before the next attempt, if the server said so.
     *
     * @return the seconds ({@code locked_out}, {@code rate_limited}), else {@code null}
     */
    public Integer getRetryAfter() {
        Object value = details.get("retryAfter");
        return value instanceof Number ? Integer.valueOf(((Number) value).intValue()) : null;
    }

    /**
     * Returns the missing Android permission of a {@code permission_missing} error.
     *
     * @return the name of the permission, else {@code null}
     */
    public String getPermission() {
        Object value = details.get("permission");
        return value instanceof String ? (String) value : null;
    }
}
