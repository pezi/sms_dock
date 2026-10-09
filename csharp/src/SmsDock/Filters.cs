using System.Text.Json.Nodes;

namespace SmsDock;

/// <summary>Filters of <c>messages.list</c>. Properties left at their default do not filter.</summary>
public sealed class MessageFilter
{
    /// <summary>
    /// Only messages from or to this number. Numbers are compared as the phone does:
    /// <c>+43 699 1234567</c> and <c>0699 1234567</c> are the same.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>Only this conversation; wins if <see cref="Address"/> is given too.</summary>
    public long? ConversationId { get; init; }

    /// <summary><c>in</c>, <c>out</c> or <c>all</c> (the default).</summary>
    public string Direction { get; init; } = "all";

    /// <summary>Search in the message text.</summary>
    public string? Query { get; init; }

    /// <summary>Not older than this, in milliseconds since the epoch, inclusive.</summary>
    public long? Since { get; init; }

    /// <summary>Not newer than this, in milliseconds since the epoch, inclusive.</summary>
    public long? Until { get; init; }

    /// <summary><see cref="Since"/> as a point in time.</summary>
    public DateTimeOffset? SinceTime
    {
        get => Since is { } since ? DateTimeOffset.FromUnixTimeMilliseconds(since) : null;
        init => Since = value?.ToUnixTimeMilliseconds();
    }

    /// <summary><see cref="Until"/> as a point in time.</summary>
    public DateTimeOffset? UntilTime
    {
        get => Until is { } until ? DateTimeOffset.FromUnixTimeMilliseconds(until) : null;
        init => Until = value?.ToUnixTimeMilliseconds();
    }

    /// <summary>Only this SIM (subscription id).</summary>
    public long? Sim { get; init; }

    /// <summary>Only unread messages.</summary>
    public bool Unread { get; init; }

    internal JsonObject ToFields()
    {
        if (Direction is not ("in" or "out" or "all"))
        {
            throw new ArgumentException("direction must be 'in', 'out' or 'all'");
        }
        var fields = new JsonObject();
        if (Address is not null)
        {
            fields["address"] = Address;
        }
        if (ConversationId is { } conversation)
        {
            fields["conversationId"] = conversation;
        }
        if (Direction != "all")
        {
            fields["direction"] = Direction;
        }
        if (Query is not null)
        {
            fields["q"] = Query;
        }
        if (Since is { } since)
        {
            fields["from"] = since;
        }
        if (Until is { } until)
        {
            fields["to"] = until;
        }
        if (Sim is { } sim)
        {
            fields["sim"] = sim;
        }
        if (Unread)
        {
            fields["unread"] = true;
        }
        return fields;
    }
}

/// <summary>Filters of <c>conversations.list</c>.</summary>
public sealed class ConversationFilter
{
    /// <summary>Search in text, numbers and contact names.</summary>
    public string? Query { get; init; }

    /// <summary>Only conversations with unread messages.</summary>
    public bool Unread { get; init; }

    /// <summary>Only this SIM (subscription id).</summary>
    public long? Sim { get; init; }

    internal JsonObject ToFields()
    {
        var fields = new JsonObject();
        if (Query is not null)
        {
            fields["q"] = Query;
        }
        if (Unread)
        {
            fields["unread"] = true;
        }
        if (Sim is { } sim)
        {
            fields["sim"] = sim;
        }
        return fields;
    }
}
