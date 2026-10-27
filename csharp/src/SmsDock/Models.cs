using System.Text.Json.Nodes;

namespace SmsDock;

/// <summary>
/// A typed view of a JSON object of the API. The object is kept exactly as the server sent
/// it in <see cref="Json"/>; the properties read its fields and cost nothing until used. All
/// timestamps are milliseconds since the Unix epoch. Fields that do not apply are
/// <c>null</c>.
/// </summary>
public abstract class JsonModel
{
    /// <summary>Creates a view of a JSON object.</summary>
    /// <param name="json">The object as the server sent it.</param>
    protected JsonModel(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        Json = json;
    }

    /// <summary>The JSON object exactly as the server sent it.</summary>
    public JsonObject Json { get; }

    /// <summary>Returns the object as compact JSON.</summary>
    public override string ToString() => PyText.Compact(Json);

    /// <summary>Reads a string field; <c>null</c> when it is missing or no string.</summary>
    /// <param name="name">The name of the field.</param>
    protected string? Text(string name) => PyText.AsString(Json[name]);

    /// <summary>Reads an integer field; <c>null</c> when it is missing or no integer.</summary>
    /// <param name="name">The name of the field.</param>
    protected long? Integer(string name) => PyText.AsInteger(Json[name]);

    /// <summary>Reads a boolean field; <c>false</c> when it is missing or not <c>true</c>.</summary>
    /// <param name="name">The name of the field.</param>
    protected bool Flag(string name) =>
        Json[name] is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.True;

    /// <summary>Reads an object field; <c>null</c> when it is missing or no object.</summary>
    /// <param name="name">The name of the field.</param>
    protected JsonObject? Object(string name) => Json[name] as JsonObject;

    /// <summary>Reads the objects of an array field; empty when it is missing.</summary>
    /// <param name="name">The name of the field.</param>
    /// <param name="create">Creates the view of one object.</param>
    /// <typeparam name="T">The type of the views.</typeparam>
    protected IReadOnlyList<T> Objects<T>(string name, Func<JsonObject, T> create)
    {
        var items = new List<T>();
        if (Json[name] is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is JsonObject map)
                {
                    items.Add(create(map));
                }
            }
        }
        return items;
    }
}

/// <summary>One SMS.</summary>
public sealed class Message : JsonModel
{
    /// <summary>Creates a view of a Message object.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public Message(JsonObject json)
        : base(json)
    {
    }

    /// <summary>
    /// Id on the phone. <c>null</c> only inside an <c>sms.received</c> event whose stored copy
    /// was not found in time.
    /// </summary>
    public long? Id => Integer("id");

    /// <summary>Id of the conversation.</summary>
    public long? ConversationId => Integer("conversationId");

    /// <summary>Peer number or sender name as stored.</summary>
    public string Address => Text("address") ?? "";

    /// <summary>Name from the contacts of the phone.</summary>
    public string? Contact => Text("contact");

    /// <summary><c>in</c> or <c>out</c>.</summary>
    public string Direction => Text("direction") ?? "";

    /// <summary>The text.</summary>
    public string Body => Text("body") ?? "";

    /// <summary>Time of the message in milliseconds since the epoch.</summary>
    public long Timestamp => Integer("timestamp") ?? 0;

    /// <summary><see cref="Timestamp"/> as a point in time.</summary>
    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp);

    /// <summary>Incoming only: time from the network of the sender.</summary>
    public long? SentTimestamp => Integer("sentTimestamp");

    /// <summary>Whether the message was read.</summary>
    public bool Read => Flag("read");

    /// <summary>
    /// One of <c>received</c>, <c>queued</c>, <c>sending</c>, <c>sent</c>, <c>delivered</c>
    /// and <c>failed</c>.
    /// </summary>
    public string Status => Text("status") ?? "";

    /// <summary>Subscription id of the SIM.</summary>
    public long? Sim => Integer("sim");

    /// <summary>Set for messages sent through SMS Dock.</summary>
    public string? RequestId => Text("requestId");
}

/// <summary>All messages exchanged with one phone number.</summary>
public sealed class Conversation : JsonModel
{
    /// <summary>Creates a view of a Conversation object.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public Conversation(JsonObject json)
        : base(json)
    {
    }

    /// <summary>Id of the conversation.</summary>
    public long Id => Integer("id") ?? 0;

    /// <summary>Peer number or sender name.</summary>
    public string Address => Text("address") ?? "";

    /// <summary>Name from the contacts of the phone.</summary>
    public string? Contact => Text("contact");

    /// <summary>The newest message, cut to 160 characters.</summary>
    public string Snippet => Text("snippet") ?? "";

    /// <summary>Time of the newest message in milliseconds since the epoch.</summary>
    public long Timestamp => Integer("timestamp") ?? 0;

    /// <summary><see cref="Timestamp"/> as a point in time.</summary>
    public DateTimeOffset Time => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp);

    /// <summary>Direction of the newest message: <c>in</c> or <c>out</c>.</summary>
    public string Direction => Text("direction") ?? "";

    /// <summary>Number of messages.</summary>
    public long MessageCount => Integer("messageCount") ?? 0;

    /// <summary>Number of unread messages.</summary>
    public long UnreadCount => Integer("unreadCount") ?? 0;

    /// <summary>Subscription id of the SIM of the newest message.</summary>
    public long? Sim => Integer("sim");
}

/// <summary>One phone number of a contact.</summary>
public sealed class ContactNumber : JsonModel
{
    /// <summary>Creates a view of a number of a contact.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public ContactNumber(JsonObject json)
        : base(json)
    {
    }

    /// <summary>The phone number.</summary>
    public string Number => Text("number") ?? "";

    /// <summary>The label, for example <c>Mobile</c>.</summary>
    public string? Label => Text("label");
}

/// <summary>One contact of the phone that has at least one number.</summary>
public sealed class Contact : JsonModel
{
    /// <summary>Creates a view of a Contact object.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public Contact(JsonObject json)
        : base(json)
    {
    }

    /// <summary>The name.</summary>
    public string Name => Text("name") ?? "";

    /// <summary>The phone numbers.</summary>
    public IReadOnlyList<ContactNumber> Numbers => Objects("numbers", json => new ContactNumber(json));
}

/// <summary>One SIM.</summary>
public sealed class Sim : JsonModel
{
    /// <summary>Creates a view of a Sim object.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public Sim(JsonObject json)
        : base(json)
    {
    }

    /// <summary>The value used as <c>sim</c> elsewhere.</summary>
    public long SubscriptionId => Integer("subscriptionId") ?? 0;

    /// <summary>The slot of the SIM.</summary>
    public long Slot => Integer("slot") ?? 0;

    /// <summary>The name of the SIM.</summary>
    public string Name => Text("name") ?? "";

    /// <summary>The phone number, if the phone knows it.</summary>
    public string? Number => Text("number");

    /// <summary>Whether this is the SMS SIM of the phone.</summary>
    public bool IsDefault => Flag("isDefault");
}

/// <summary>Name and version of the app and the protocol version it speaks.</summary>
public sealed class AppInfo : JsonModel
{
    /// <summary>Creates a view of the app information.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public AppInfo(JsonObject json)
        : base(json)
    {
    }

    /// <summary>Name of the app.</summary>
    public string Name => Text("name") ?? "";

    /// <summary>Version of the app.</summary>
    public string Version => Text("version") ?? "";

    /// <summary>Protocol version of the app.</summary>
    public long? ProtocolVersion => Integer("protocolVersion");
}

/// <summary>Licence state.</summary>
public sealed class Licence : JsonModel
{
    /// <summary>Creates a view of the licence state.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public Licence(JsonObject json)
        : base(json)
    {
    }

    /// <summary>Whether the app checks the licence.</summary>
    public bool Enforced => Flag("enforced");

    /// <summary>Whether the full licence is present.</summary>
    public bool Licensed => Flag("licensed");

    /// <summary>Daily sending limit of the free version.</summary>
    public long DailyLimit => Integer("dailyLimit") ?? 0;

    /// <summary>Messages sent today.</summary>
    public long SentToday => Integer("sentToday") ?? 0;

    /// <summary>Messages left today; <c>null</c> when sending is unlimited.</summary>
    public long? Remaining => Integer("remaining");
}

/// <summary>Result of <c>status</c>.</summary>
public sealed class Status : JsonModel
{
    /// <summary>Creates a view of a Status object.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public Status(JsonObject json)
        : base(json)
    {
    }

    /// <summary>Name and version of the app.</summary>
    public AppInfo? App => Object("app") is { } app ? new AppInfo(app) : null;

    /// <summary>The SIMs of the phone.</summary>
    public IReadOnlyList<Sim> Sims => Objects("sims", json => new Sim(json));

    /// <summary>
    /// Maps the names <c>readSms</c>, <c>sendSms</c>, <c>receiveSms</c>, <c>contacts</c>,
    /// <c>phoneState</c> and <c>notifications</c> to whether the permission is granted.
    /// </summary>
    public IReadOnlyDictionary<string, bool> Permissions
    {
        get
        {
            var permissions = new Dictionary<string, bool>();
            if (Object("permissions") is { } map)
            {
                foreach (var pair in map)
                {
                    permissions[pair.Key] = PyText.Truthy(pair.Value);
                }
            }
            return permissions;
        }
    }

    /// <summary>Whether SMS Dock is the default SMS app of the phone.</summary>
    public bool DefaultSmsApp => Flag("defaultSmsApp");

    /// <summary><c>hidden</c> or <c>provider</c>.</summary>
    public string DeleteMode => Text("deleteMode") ?? "";

    /// <summary>Licence state.</summary>
    public Licence? Licence => Object("licence") is { } licence ? new Licence(licence) : null;

    /// <summary>Whether the phone shows incoming SMS in a popup.</summary>
    public bool Popup => Flag("popup");
}

/// <summary>Result of <c>messages.send</c>.</summary>
public sealed class SendResult : JsonModel
{
    /// <summary>Creates a view of a SendResult object.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public SendResult(JsonObject json)
        : base(json)
    {
    }

    /// <summary>The id of the request, yours or the one the client created.</summary>
    public string RequestId => Text("requestId") ?? "";

    /// <summary>The number as it is sent, without the ignored characters.</summary>
    public string To => Text("to") ?? "";

    /// <summary>Number of SMS parts.</summary>
    public long Parts => Integer("parts") ?? 0;

    /// <summary>
    /// <c>queued</c> for a new message; a repeated request returns the current status,
    /// <c>failed</c> included.
    /// </summary>
    public string Status => Text("status") ?? "";
}

/// <summary>Result of <c>messages.sendGroup</c>: one SMS per recipient.</summary>
public sealed class GroupSendResult : JsonModel
{
    /// <summary>Creates a view of a GroupSendResult object.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public GroupSendResult(JsonObject json)
        : base(json)
    {
    }

    /// <summary>
    /// The id of the group, yours or the one the client created. The members have the ids
    /// <c>requestId.1</c>, <c>requestId.2</c> and so on.
    /// </summary>
    public string RequestId => Text("requestId") ?? "";

    /// <summary>The numbers as they are sent, in the order of <see cref="Messages"/>.</summary>
    public IReadOnlyList<string> To
    {
        get
        {
            var numbers = new List<string>();
            if (Json["to"] is JsonArray array)
            {
                foreach (var item in array)
                {
                    if (PyText.AsString(item) is { } number)
                    {
                        numbers.Add(number);
                    }
                }
            }
            return numbers;
        }
    }

    /// <summary>Number of SMS parts of the text, the same for every recipient.</summary>
    public long Parts => Integer("parts") ?? 0;

    /// <summary>
    /// The least advanced status of the members (<c>queued</c>, <c>sending</c>, <c>sent</c>,
    /// <c>delivered</c>), or <c>failed</c> when every member failed.
    /// </summary>
    public string Status => Text("status") ?? "";

    /// <summary>One <see cref="SendResult"/> per recipient.</summary>
    public IReadOnlyList<SendResult> Messages => Objects("messages", json => new SendResult(json));

    /// <summary>Number of recipients the phone refused at once.</summary>
    public int FailedCount => Messages.Count(member => member.Status == "failed");
}

/// <summary>State of a message sent through SMS Dock.</summary>
public sealed class OutboxEntry : JsonModel
{
    /// <summary>Creates a view of an outbox entry.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public OutboxEntry(JsonObject json)
        : base(json)
    {
    }

    /// <summary>The id of the request.</summary>
    public string RequestId => Text("requestId") ?? "";

    /// <summary>The number as it is sent.</summary>
    public string To => Text("to") ?? "";

    /// <summary>Number of SMS parts.</summary>
    public long Parts => Integer("parts") ?? 0;

    /// <summary>Number of parts the network accepted so far.</summary>
    public long PartsSent => Integer("partsSent") ?? 0;

    /// <summary>
    /// <c>queued</c> (handed to the phone), <c>sending</c> (some parts are left), <c>sent</c>,
    /// <c>delivered</c> or <c>failed</c>.
    /// </summary>
    public string Status => Text("status") ?? "";

    /// <summary>Result code of Android when sending failed.</summary>
    public long? ErrorCode => Integer("errorCode");

    /// <summary>Id of the stored message, once the phone has stored it.</summary>
    public long? MessageId => Integer("messageId");

    /// <summary>Subscription id of the SIM.</summary>
    public long? Sim => Integer("sim");

    /// <summary>Who sent: <c>web</c>, <c>api</c> or <c>app</c>.</summary>
    public string Source => Text("source") ?? "";

    /// <summary>Time of the request in milliseconds since the epoch.</summary>
    public long CreatedAt => Integer("createdAt") ?? 0;
}

/// <summary>Result of a delete.</summary>
public sealed class DeleteResult : JsonModel
{
    /// <summary>Creates a view of the result of a delete.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public DeleteResult(JsonObject json)
        : base(json)
    {
    }

    /// <summary>Number of messages deleted.</summary>
    public long Deleted => Integer("deleted") ?? 0;

    /// <summary><c>hidden</c> or <c>provider</c>.</summary>
    public string Mode => Text("mode") ?? "";
}

/// <summary>The subscription in force after <c>subscribe</c> or <c>unsubscribe</c>.</summary>
public sealed class Subscription : JsonModel
{
    /// <summary>Creates a view of a subscription.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public Subscription(JsonObject json)
        : base(json)
    {
    }

    /// <summary>Event names; <c>*</c> is replaced by the names it stands for.</summary>
    public IReadOnlyList<string> Events
    {
        get
        {
            var names = new List<string>();
            if (Json["events"] is JsonArray array)
            {
                foreach (var item in array)
                {
                    names.Add(PyText.Str(item));
                }
            }
            return names;
        }
    }

    /// <summary>The number the <c>sms.*</c> events are limited to, or <c>null</c>.</summary>
    public string? Address => Text("address");
}

/// <summary>Result of <c>ping</c>: the time of the phone.</summary>
public sealed class PingResult : JsonModel
{
    /// <summary>Creates a view of the result of a ping.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public PingResult(JsonObject json)
        : base(json)
    {
    }

    /// <summary>Time of the phone in milliseconds since the epoch.</summary>
    public long Time => Integer("time") ?? 0;
}

/// <summary>Result of <c>conversations.markRead</c>: <c>{"ok": true}</c>.</summary>
public sealed class MarkReadResult : JsonModel
{
    /// <summary>Creates a view of the result.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public MarkReadResult(JsonObject json)
        : base(json)
    {
    }

    /// <summary>Whether the conversation was marked.</summary>
    public bool Ok => Flag("ok");
}

/// <summary>One page of a list. <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
/// <typeparam name="T">The type of the items.</typeparam>
public sealed class Page<T> : JsonModel
    where T : JsonModel
{
    private readonly Func<JsonObject, T> _create;

    internal Page(JsonObject json, Func<JsonObject, T> create)
        : base(json)
    {
        _create = create;
    }

    /// <summary>The items of this page.</summary>
    public IReadOnlyList<T> Items => Objects("items", _create);

    /// <summary>Pass it as <c>cursor</c> to get the next page; <c>null</c> on the last page.</summary>
    public string? NextCursor => Text("nextCursor");
}

/// <summary>An event: <c>{"event": name, "seq": number, "data": {...}}</c>.</summary>
public sealed class SmsEvent : JsonModel
{
    /// <summary>Creates a view of an event.</summary>
    /// <param name="json">The object as the server sent it.</param>
    public SmsEvent(JsonObject json)
        : base(json)
    {
    }

    /// <summary>The name of the event, for example <c>sms.received</c>.</summary>
    public string Name => Text("event") ?? "";

    /// <summary>
    /// Counts all events of the phone and restarts at 1 when the app is restarted.
    /// </summary>
    public long? Seq => Integer("seq");

    /// <summary>The data of the event; its keys are listed under "Events" in <c>API.md</c>.</summary>
    public JsonObject Data => Object("data") ?? [];

    /// <summary>The message of an <c>sms.received</c> or <c>sms.sent</c> event, else <c>null</c>.</summary>
    public Message? Message => Data["message"] is JsonObject message ? new Message(message) : null;
}
