package app.flutterdev.smsdock;

import java.util.Map;

/** The API needs the full licence of the app ({@code licence_required}). */
public class LicenceRequiredException extends ApiException {

    /**
     * Creates the error.
     *
     * @param code error code of {@code API.md}
     * @param message text of the server
     * @param details other fields of the error object, or {@code null}
     */
    public LicenceRequiredException(String code, String message, Map<String, Object> details) {
        super(code, message, details);
    }
}
