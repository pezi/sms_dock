package app.flutterdev.smsdock;

import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;

/**
 * Filters of {@link SmsDockClient#messages} and {@link SmsDockClient#iterMessages}.
 *
 * <p>A new filter selects all messages. Each method narrows it and returns the filter:
 *
 * <pre>{@code
 * new MessageFilter().direction("in").address("+4369912345678").query("invoice")
 * }</pre>
 */
public final class MessageFilter {

    private static final Set<String> DIRECTIONS = Set.of("in", "out", "all");

    private String address;
    private Long conversationId;
    private String direction = "all";
    private String query;
    private Long since;
    private Long until;
    private Long sim;
    private boolean unread;

    /** Creates a filter that selects all messages. */
    public MessageFilter() {
    }

    /**
     * Keeps messages from or to one number. Numbers are compared as the phone does:
     * {@code +43 699 1234567} and {@code 0699 1234567} are the same.
     *
     * @param address phone number or sender name
     * @return this filter
     */
    public MessageFilter address(String address) {
        this.address = address;
        return this;
    }

    /**
     * Keeps the messages of one conversation; wins if an address is given too.
     *
     * @param conversationId {@code id} of the conversation
     * @return this filter
     */
    public MessageFilter conversationId(long conversationId) {
        this.conversationId = conversationId;
        return this;
    }

    /**
     * Keeps incoming or outgoing messages.
     *
     * @param direction {@code "in"}, {@code "out"} or {@code "all"} (default)
     * @return this filter
     */
    public MessageFilter direction(String direction) {
        this.direction = direction;
        return this;
    }

    /**
     * Searches the message text; sent as the field {@code q}.
     *
     * @param query text to search
     * @return this filter
     */
    public MessageFilter query(String query) {
        this.query = query;
        return this;
    }

    /**
     * Keeps messages not older than this; sent as the field {@code from}.
     *
     * @param milliseconds milliseconds since the epoch, inclusive
     * @return this filter
     */
    public MessageFilter since(long milliseconds) {
        this.since = milliseconds;
        return this;
    }

    /**
     * Keeps messages not older than this; sent as the field {@code from}.
     *
     * @param moment the time, inclusive
     * @return this filter
     */
    public MessageFilter since(Instant moment) {
        return since(moment.toEpochMilli());
    }

    /**
     * Keeps messages not newer than this; sent as the field {@code to}.
     *
     * @param milliseconds milliseconds since the epoch, inclusive
     * @return this filter
     */
    public MessageFilter until(long milliseconds) {
        this.until = milliseconds;
        return this;
    }

    /**
     * Keeps messages not newer than this; sent as the field {@code to}.
     *
     * @param moment the time, inclusive
     * @return this filter
     */
    public MessageFilter until(Instant moment) {
        return until(moment.toEpochMilli());
    }

    /**
     * Keeps the messages of one SIM.
     *
     * @param subscriptionId subscription id of the SIM
     * @return this filter
     */
    public MessageFilter sim(long subscriptionId) {
        this.sim = subscriptionId;
        return this;
    }

    /**
     * Keeps only unread messages.
     *
     * @param unread {@code true} for unread messages only
     * @return this filter
     */
    public MessageFilter unread(boolean unread) {
        this.unread = unread;
        return this;
    }

    /** Returns the fields of the request; checks the direction. */
    Map<String, Object> toParameters() {
        if (direction == null || !DIRECTIONS.contains(direction)) {
            throw new IllegalArgumentException("direction must be 'in', 'out' or 'all'");
        }
        Map<String, Object> parameters = new LinkedHashMap<>();
        if (address != null) {
            parameters.put("address", address);
        }
        if (conversationId != null) {
            parameters.put("conversationId", conversationId);
        }
        if (!direction.equals("all")) {
            parameters.put("direction", direction);
        }
        if (query != null) {
            parameters.put("q", query);
        }
        if (since != null) {
            parameters.put("from", since);
        }
        if (until != null) {
            parameters.put("to", until);
        }
        if (sim != null) {
            parameters.put("sim", sim);
        }
        if (unread) {
            parameters.put("unread", Boolean.TRUE);
        }
        return parameters;
    }
}
