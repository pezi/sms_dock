package app.flutterdev.smsdock;

import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Filters of {@link SmsDockClient#conversations} and {@link SmsDockClient#iterConversations}.
 *
 * <p>A new filter selects all conversations. Each method narrows it and returns the filter.
 */
public final class ConversationFilter {

    private String query;
    private boolean unread;
    private Long sim;

    /** Creates a filter that selects all conversations. */
    public ConversationFilter() {
    }

    /**
     * Searches text, numbers and contact names; sent as the field {@code q}.
     *
     * @param query text to search
     * @return this filter
     */
    public ConversationFilter query(String query) {
        this.query = query;
        return this;
    }

    /**
     * Keeps only conversations with unread messages.
     *
     * @param unread {@code true} for conversations with unread messages only
     * @return this filter
     */
    public ConversationFilter unread(boolean unread) {
        this.unread = unread;
        return this;
    }

    /**
     * Keeps the conversations whose newest message belongs to one SIM.
     *
     * @param subscriptionId subscription id of the SIM
     * @return this filter
     */
    public ConversationFilter sim(long subscriptionId) {
        this.sim = subscriptionId;
        return this;
    }

    /** Returns the fields of the request. */
    Map<String, Object> toParameters() {
        Map<String, Object> parameters = new LinkedHashMap<>();
        if (query != null) {
            parameters.put("q", query);
        }
        if (unread) {
            parameters.put("unread", Boolean.TRUE);
        }
        if (sim != null) {
            parameters.put("sim", sim);
        }
        return parameters;
    }
}
