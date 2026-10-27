using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SmsDock;

/// <summary>
/// One authenticated connection to SMS Dock.
/// </summary>
/// <remarks>
/// <para>
/// The client pins the certificate of the phone, reconnects after a lost connection, logs in
/// again, restores the subscription and tells what it missed meanwhile
/// (<see cref="MissedEvents"/>, <see cref="ServerRestarted"/>).
/// </para>
/// <para>
/// The client is not thread-safe: use its methods one after the other, and one client per
/// task that works on its own.
/// </para>
/// </remarks>
public sealed partial class SmsDockClient : IAsyncDisposable
{
    private const int MaxBackoffExponent = 16;
    private static readonly TimeSpan s_firstPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_closeTimeout = TimeSpan.FromSeconds(5);

    private readonly Uri _uri;
    private readonly string _password;
    private readonly byte[]? _fingerprint;
    private readonly int _eventQueueSize;
    private readonly TextWriter? _error;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _keepAliveInterval;
    private readonly TimeSpan _keepAliveTimeout;
    private readonly List<JsonObject> _events = [];
    private readonly HashSet<string> _outstanding = [];
    private readonly Dictionary<string, JsonObject> _responses = [];

    private Connection? _connection;
    private long _requestNumber;
    private List<string>? _subscribedEvents;
    private string? _subscribedAddress;
    private bool _warned;
    private JsonObject? _serverInfo;
    private string? _keepAliveKey;
    private TimeSpan _keepAliveSent;
    private TimeSpan _lastActivity;

    /// <summary>Creates a client. Nothing is sent before <see cref="ConnectAsync(CancellationToken)"/> or the first request.</summary>
    /// <param name="url">
    /// The <c>wss://</c> address the app shows, for example <c>wss://192.168.1.50:9443/ws</c>.
    /// Any other scheme is refused.
    /// </param>
    /// <param name="password">
    /// The password set in the app. Without it the environment variable
    /// <c>SMSDOCK_PASSWORD</c> is read.
    /// </param>
    /// <param name="options">Fingerprint, timeouts and reconnect behaviour.</param>
    /// <exception cref="ArgumentException">An argument or option cannot be used.</exception>
    public SmsDockClient(string url, string? password = null, SmsDockClientOptions? options = null)
    {
        options ??= new SmsDockClientOptions();
        if (url is null
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new ArgumentException(
                "SMS Dock needs a wss:// URL, for example wss://192.168.1.50:9443/ws");
        }
        password ??= options.Environment(SmsDockApi.PasswordEnv);
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException(
                $"No password: pass one or set the environment variable {SmsDockApi.PasswordEnv}");
        }
        var fingerprint = options.Fingerprint;
        if (fingerprint is null)
        {
            fingerprint = options.Environment(SmsDockApi.FingerprintEnv);
            if (string.IsNullOrEmpty(fingerprint))
            {
                fingerprint = null;
            }
        }
        if (fingerprint is null && !options.Insecure)
        {
            throw new ArgumentException(
                "No certificate fingerprint: pass the fingerprint shown on the Server screen of "
                + $"the app or set the environment variable {SmsDockApi.FingerprintEnv}");
        }
        if (!(options.Timeout > TimeSpan.Zero) || !(options.ConnectTimeout > TimeSpan.Zero))
        {
            throw new ArgumentException("Timeouts must be positive");
        }
        if (options.ReconnectAttempts is < 0)
        {
            throw new ArgumentException("ReconnectAttempts cannot be negative");
        }
        if (options.ReconnectDelay < TimeSpan.Zero || options.MaxReconnectDelay < TimeSpan.Zero)
        {
            throw new ArgumentException("Reconnect delays cannot be negative");
        }
        if (options.EventQueueSize < 1)
        {
            throw new ArgumentException("EventQueueSize must be at least 1");
        }
        if (options.RateLimitRetries < 0)
        {
            throw new ArgumentException("RateLimitRetries cannot be negative");
        }

        Url = url;
        _uri = uri;
        _password = password;
        Timeout = options.Timeout;
        ConnectTimeout = options.ConnectTimeout;
        ReconnectAttempts = options.ReconnectAttempts;
        ReconnectDelay = options.ReconnectDelay;
        MaxReconnectDelay = options.MaxReconnectDelay;
        Insecure = options.Insecure;
        RateLimitRetries = options.RateLimitRetries;
        OnReconnect = options.OnReconnect;
        if (!options.Insecure && fingerprint is not null)
        {
            _fingerprint = Convert.FromHexString(CertificateFingerprint.Normalize(fingerprint));
        }
        _eventQueueSize = options.EventQueueSize;
        _error = options.Error;
        _delay = options.Delay ?? Task.Delay;
        _keepAliveInterval = options.KeepAliveInterval;
        _keepAliveTimeout = options.KeepAliveTimeout;
    }

    /// <summary>The address of the server.</summary>
    public string Url { get; }

    /// <summary>Time to wait for a response.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Time to open the connection.</summary>
    public TimeSpan ConnectTimeout { get; }

    /// <summary>Reconnect attempts after a lost connection; <c>null</c> means without limit.</summary>
    public int? ReconnectAttempts { get; }

    /// <summary>Time before the first reconnect attempt.</summary>
    public TimeSpan ReconnectDelay { get; }

    /// <summary>Upper limit of the time between reconnect attempts.</summary>
    public TimeSpan MaxReconnectDelay { get; }

    /// <summary>Whether the check of the certificate is skipped.</summary>
    public bool Insecure { get; }

    /// <summary>How often a request is repeated after the error <c>rate_limited</c>.</summary>
    public int RateLimitRetries { get; }

    /// <summary>Called with the client after every reconnect, or <c>null</c>.</summary>
    public Func<SmsDockClient, CancellationToken, Task>? OnReconnect { get; set; }

    /// <summary>Name and version of the app, known after the login.</summary>
    public AppInfo? ServerInfo => _serverInfo is null ? null : new AppInfo(_serverInfo);

    /// <summary>Number of events dropped because the event queue was full.</summary>
    public long DroppedEvents { get; private set; }

    /// <summary>Number of connections opened so far, reconnects included.</summary>
    public int ConnectionCount { get; private set; }

    /// <summary>Whether a connection is open as far as the client knows.</summary>
    public bool Connected => _connection is not null;

    /// <summary>The subscription in force, or <c>null</c> without one.</summary>
    public Subscription? Subscription
    {
        get
        {
            if (_subscribedEvents is null)
            {
                return null;
            }
            var names = new JsonArray();
            foreach (var name in _subscribedEvents)
            {
                names.Add(name);
            }
            return new Subscription(new JsonObject { ["events"] = names, ["address"] = _subscribedAddress });
        }
    }

    /// <summary>Number of events that wait in the queue.</summary>
    public int PendingEvents => _events.Count;

    /// <summary>
    /// The <c>epoch</c> of the last login; <c>null</c> before the first. It identifies one run
    /// of the app. Sequence numbers begin at 1 again when it changes.
    /// </summary>
    public long? Epoch { get; private set; }

    /// <summary>
    /// Sequence number of the newest event the client knows of: the <c>latestSeq</c> of the
    /// last login or the <c>seq</c> of an event received since, whichever is higher. The phone
    /// counts all events, so a client that subscribed to some of them sees gaps.
    /// </summary>
    public long? LatestSeq { get; private set; }

    /// <summary>
    /// Whether the app was restarted before the last reconnect: the <c>epoch</c> of the
    /// current connection differs from the one before. Events were lost then, and their
    /// number is not known.
    /// </summary>
    public bool ServerRestarted { get; private set; }

    /// <summary>
    /// Number of events of the phone during the last interruption, counted over all events,
    /// subscribed or not. <c>0</c> after the first connect, <c>null</c> when the app was
    /// restarted and the number is not known.
    /// </summary>
    public long? MissedEvents { get; private set; } = 0;

    /// <summary>Largest frame the client sends; changed in tests only.</summary>
    internal int MaxFrameBytes { get; set; } = SmsDockApi.MaxFrameBytes;

    /// <summary>Creates a client, opens the connection and logs in.</summary>
    /// <param name="url">The <c>wss://</c> address the app shows.</param>
    /// <param name="password">The password; default is <c>SMSDOCK_PASSWORD</c>.</param>
    /// <param name="options">Fingerprint, timeouts and reconnect behaviour.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The connected client. Dispose it with <c>await using</c>.</returns>
    public static async Task<SmsDockClient> ConnectAsync(
        string url,
        string? password = null,
        SmsDockClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = new SmsDockClient(url, password, options);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return client;
    }

    /// <summary>Returns the address and whether the client is connected, never the password.</summary>
    public override string ToString() => $"<SmsDockClient {Url} {(Connected ? "connected" : "closed")}>";

    // ------------------------------------------------------------------
    // Connection
    // ------------------------------------------------------------------

    /// <summary>Opens the connection, checks the certificate and logs in. Makes one attempt.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="CertificateMismatchException">The server has another certificate.</exception>
    /// <exception cref="AuthenticationException">The login failed.</exception>
    /// <exception cref="LicenceRequiredException">The app has no full licence.</exception>
    /// <exception cref="ProtocolException">The server does not speak protocol version 1.</exception>
    /// <exception cref="ConnectionInterruptedException">The server cannot be reached.</exception>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await CloseAsync().ConfigureAwait(false);
        try
        {
            await OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException error)
        {
            await CloseAsync().ConfigureAwait(false);
            throw FatalError(error) ?? Interrupted($"Cannot connect to {Url}", error);
        }
    }

    /// <summary>Closes the connection. Queued events are kept.</summary>
    public async Task CloseAsync()
    {
        var connection = _connection;
        _connection = null;
        _outstanding.Clear();
        _responses.Clear();
        _keepAliveKey = null;
        if (connection is not null)
        {
            var timeout = Timeout < s_closeTimeout ? Timeout : s_closeTimeout;
            await connection.CloseAsync(timeout).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the connection.</summary>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    // ------------------------------------------------------------------
    // Requests
    // ------------------------------------------------------------------

    /// <summary>Sends one request and returns the <c>result</c> of its response.</summary>
    /// <param name="action">The action as named in <c>API.md</c>.</param>
    /// <param name="fields">The fields of the action as named in <c>API.md</c>.</param>
    /// <param name="retry">
    /// <c>true</c> sends the request again after a lost connection, and waits and repeats it
    /// after the error <c>rate_limited</c>. Pass <c>false</c> for anything that must not
    /// happen twice.
    /// </param>
    /// <param name="timeout">Time to wait for the response; default is <see cref="Timeout"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ApiException">The server answered with an error.</exception>
    /// <exception cref="ConnectionInterruptedException">The connection could not be restored.</exception>
    public async Task<JsonObject> RequestAsync(
        string action,
        JsonObject? fields = null,
        bool retry = true,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        CheckFields(fields);
        var wait = Effective(timeout);
        var attempt = 0;
        while (true)
        {
            try
            {
                if (_connection is null)
                {
                    await OpenAsync(cancellationToken).ConfigureAwait(false);
                }
                return await RequestPatientlyAsync(
                    action, fields, wait, retry ? RateLimitRetries : 0, cancellationToken).ConfigureAwait(false);
            }
            catch (TransportException error)
            {
                var what = $"performing {PyText.Repr(action)}";
                if (!retry)
                {
                    await CloseAsync().ConfigureAwait(false);
                    throw FatalError(error) ?? Interrupted($"Connection interrupted while {what}", error);
                }
                await RecoverAsync(what, error, attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
            }
        }
    }

    /// <summary>
    /// Sends a request without waiting and returns its id. Several requests can be on their
    /// way at once; the server may answer them in any order. Fetch each response with
    /// <see cref="WaitForResponseAsync"/>. These two methods do not reconnect and do not
    /// repeat a request that was answered with <c>rate_limited</c>.
    /// </summary>
    /// <param name="action">The action as named in <c>API.md</c>.</param>
    /// <param name="fields">The fields of the action.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<string> SendRequestAsync(
        string action, JsonObject? fields = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        CheckFields(fields);
        try
        {
            if (_connection is null)
            {
                await OpenAsync(cancellationToken).ConfigureAwait(false);
            }
            return await SendFrameAsync(action, fields, cancellationToken).ConfigureAwait(false);
        }
        catch (TransportException error)
        {
            await CloseAsync().ConfigureAwait(false);
            throw FatalError(error)
                ?? Interrupted($"Connection interrupted while sending {PyText.Repr(action)}", error);
        }
    }

    /// <summary>Returns the result of a request sent with <see cref="SendRequestAsync"/>.</summary>
    /// <param name="requestId">The id <see cref="SendRequestAsync"/> returned.</param>
    /// <param name="timeout">Time to wait; default is <see cref="Timeout"/>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ApiException">The response is an error.</exception>
    /// <exception cref="TimeoutException">No response arrived in time.</exception>
    /// <exception cref="ConnectionInterruptedException">The connection was lost.</exception>
    public async Task<JsonObject> WaitForResponseAsync(
        string requestId, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        if (!_outstanding.Contains(requestId) && !_responses.ContainsKey(requestId))
        {
            throw new ArgumentException($"No request with the id {PyText.Repr(requestId)} is open");
        }
        var deadline = Timing.After(Effective(timeout));
        try
        {
            return await AwaitResponseAsync(requestId, $"request {requestId}", deadline, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ReceiveTimeoutException error)
        {
            throw new TimeoutException(error.Message);
        }
        catch (TransportException error)
        {
            await CloseAsync().ConfigureAwait(false);
            throw FatalError(error)
                ?? Interrupted($"Connection interrupted while waiting for {PyText.Repr(requestId)}", error);
        }
    }

    /// <summary>Returns phone, SIMs, permissions, licence and limits.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<Status> StatusAsync(CancellationToken cancellationToken = default) =>
        new(await RequestAsync("status", cancellationToken: cancellationToken).ConfigureAwait(false));

    /// <summary>Checks the connection and returns the time of the phone.</summary>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<PingResult> PingAsync(CancellationToken cancellationToken = default) =>
        new(await RequestAsync("ping", cancellationToken: cancellationToken).ConfigureAwait(false));

    /// <summary>Returns one page of conversations, newest first.</summary>
    /// <param name="filter">Which conversations; <c>null</c> for all.</param>
    /// <param name="limit">Page size, 1 to 200.</param>
    /// <param name="cursor">The <see cref="Page{T}.NextCursor"/> of the previous page.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<Page<Conversation>> ConversationsAsync(
        ConversationFilter? filter = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var fields = (filter ?? new ConversationFilter()).ToFields();
        AddPaging(fields, limit, cursor);
        var page = await RequestAsync("conversations.list", fields, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new Page<Conversation>(page, json => new Conversation(json));
    }

    /// <summary>Yields conversations, newest first, page after page.</summary>
    /// <param name="filter">Which conversations; <c>null</c> for all.</param>
    /// <param name="pageSize">The <c>limit</c> of each request, 1 to 200.</param>
    /// <param name="maxItems">Ends after that many items; <c>null</c> yields all of them.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public IAsyncEnumerable<Conversation> EnumerateConversationsAsync(
        ConversationFilter? filter = null,
        int pageSize = 50,
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        var fields = (filter ?? new ConversationFilter()).ToFields();
        return Iterate(
            "conversations.list", fields, pageSize, maxItems, json => new Conversation(json), cancellationToken);
    }

    /// <summary>Returns one page of messages, newest first.</summary>
    /// <param name="filter">Which messages; <c>null</c> for all.</param>
    /// <param name="limit">Page size, 1 to 200.</param>
    /// <param name="cursor">The <see cref="Page{T}.NextCursor"/> of the previous page.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<Page<Message>> MessagesAsync(
        MessageFilter? filter = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var fields = (filter ?? new MessageFilter()).ToFields();
        AddPaging(fields, limit, cursor);
        var page = await RequestAsync("messages.list", fields, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new Page<Message>(page, json => new Message(json));
    }

    /// <summary>Yields messages, newest first, following <c>nextCursor</c>.</summary>
    /// <param name="filter">Which messages; <c>null</c> for all.</param>
    /// <param name="pageSize">The <c>limit</c> of each request, 1 to 200.</param>
    /// <param name="maxItems">Ends after that many messages; <c>null</c> yields all of them.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public IAsyncEnumerable<Message> EnumerateMessagesAsync(
        MessageFilter? filter = null,
        int pageSize = 50,
        int? maxItems = null,
        CancellationToken cancellationToken = default)
    {
        var fields = (filter ?? new MessageFilter()).ToFields();
        return Iterate("messages.list", fields, pageSize, maxItems, json => new Message(json), cancellationToken);
    }

    /// <summary>Returns one message.</summary>
    /// <param name="messageId">The id of the message.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ApiException"><c>not_found</c> if it does not exist.</exception>
    public async Task<Message> GetMessageAsync(long messageId, CancellationToken cancellationToken = default) =>
        new(await RequestAsync(
            "messages.get", new JsonObject { ["messageId"] = messageId }, cancellationToken: cancellationToken)
            .ConfigureAwait(false));

    /// <summary>
    /// Sends an SMS. The message is on its way when this method returns; use
    /// <see cref="WaitForResultAsync"/> for the outcome.
    /// </summary>
    /// <remarks>
    /// <paramref name="requestId"/> makes the request safe to repeat: the phone sends a message
    /// with the same id only once and remembers the id for 30 days. A repeat returns the result
    /// of the first request with the current status. It never sends again, also not after the
    /// first attempt failed; a new attempt needs a new id. A repeat with another recipient or
    /// text fails with <c>invalid_request</c>. Without <paramref name="requestId"/> the client
    /// creates a random one; if the connection drops before the response arrives, the request
    /// is repeated with the same id.
    /// </remarks>
    /// <param name="to">The phone number. Spaces, dashes, dots, slashes and brackets are ignored.</param>
    /// <param name="body">The text, at most 10 SMS parts.</param>
    /// <param name="sim">Subscription id of the SIM; default is the SMS SIM of the phone.</param>
    /// <param name="requestId">Your own id: 1 to 64 characters of <c>A-Z a-z 0-9 . _ -</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<SendResult> SendAsync(
        string to,
        string body,
        long? sim = null,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(to))
        {
            throw new ArgumentException("'to' must be a phone number");
        }
        if (string.IsNullOrEmpty(body))
        {
            throw new ArgumentException("'body' must be a text");
        }
        if (requestId is null)
        {
            requestId = Guid.NewGuid().ToString("N");
        }
        else if (!RequestIdPattern().IsMatch(requestId))
        {
            throw new ArgumentException("request_id must be 1 to 64 characters of A-Z a-z 0-9 . _ -");
        }
        var fields = new JsonObject { ["to"] = to, ["body"] = body, ["requestId"] = requestId };
        if (sim is { } subscription)
        {
            fields["sim"] = subscription;
        }
        return new SendResult(
            await RequestAsync("messages.send", fields, cancellationToken: cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Sends one text to several numbers, one SMS each. Every recipient gets a separate SMS and
    /// does not see the others. The messages are on their way when this method returns; use
    /// <see cref="WaitForResultAsync"/> with the request id of each member for the outcome.
    /// </summary>
    /// <remarks>
    /// The phone checks the whole group before it sends anything: a number that is not a phone
    /// number (<c>invalid_number</c>), a text that is too long, fewer messages left in the daily
    /// limit of the free version than the group has recipients (<c>quota_exceeded</c>) or more
    /// SMS parts than the 20 of one minute, parts times recipients (<c>invalid_request</c>),
    /// refuse the whole group. A refusal by the phone for one recipient does not stop the
    /// others; that member has the status <c>failed</c> in <see cref="GroupSendResult.Messages"/>.
    /// <paramref name="requestId"/> works as for <see cref="SendAsync"/>, for the whole group:
    /// the same id with the same text and numbers is not sent again but answered with the
    /// stored members and their current status.
    /// </remarks>
    /// <param name="to">1 to 20 phone numbers; repeated numbers are sent once.</param>
    /// <param name="body">The text, at most 10 SMS parts.</param>
    /// <param name="sim">Subscription id of the SIM for every message; default is the SMS SIM of the phone.</param>
    /// <param name="requestId">
    /// Your own id of the group: 1 to 61 characters of <c>A-Z a-z 0-9 . _ -</c>. The members get
    /// <c>requestId.1</c>, <c>requestId.2</c> and so on.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<GroupSendResult> SendGroupAsync(
        IReadOnlyList<string> to,
        string body,
        long? sim = null,
        string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        if (to is null || to.Count == 0 || to.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("'to' must be a list of phone numbers");
        }
        if (to.Count > SmsDockApi.MaxGroupRecipients)
        {
            throw new ArgumentException($"'to' holds more than {SmsDockApi.MaxGroupRecipients} numbers");
        }
        if (string.IsNullOrEmpty(body))
        {
            throw new ArgumentException("'body' must be a text");
        }
        if (requestId is null)
        {
            requestId = Guid.NewGuid().ToString("N");
        }
        else if (!GroupRequestIdPattern().IsMatch(requestId))
        {
            throw new ArgumentException(
                "request_id of a group must be 1 to 61 characters of A-Z a-z 0-9 . _ -");
        }
        var numbers = new JsonArray();
        foreach (var number in to)
        {
            numbers.Add(number);
        }
        var fields = new JsonObject { ["to"] = numbers, ["body"] = body, ["requestId"] = requestId };
        if (sim is { } subscription)
        {
            fields["sim"] = subscription;
        }
        return new GroupSendResult(
            await RequestAsync("messages.sendGroup", fields, cancellationToken: cancellationToken)
                .ConfigureAwait(false));
    }

    /// <summary>Returns the outbox entry of a message sent through SMS Dock.</summary>
    /// <param name="requestId">The id of the request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<OutboxEntry> MessageStatusAsync(
        string requestId, CancellationToken cancellationToken = default) =>
        new(await RequestAsync(
            "messages.status", new JsonObject { ["requestId"] = requestId }, cancellationToken: cancellationToken)
            .ConfigureAwait(false));

    /// <summary>
    /// Waits until a sent message succeeded or failed and returns its outbox entry. Check its
    /// <see cref="OutboxEntry.Status"/>: a failed message raises no exception.
    /// </summary>
    /// <remarks>
    /// The method asks the server with <c>messages.status</c>: soon after the start, then less
    /// often, at last every <paramref name="pollInterval"/>. If the connection is subscribed to
    /// <c>sms.sent</c>, <c>sms.delivered</c> and <c>sms.failed</c> it asks as soon as such an
    /// event arrives. The events stay in the queue.
    /// </remarks>
    /// <param name="requestId">The id of the request.</param>
    /// <param name="timeout">Time to wait; default is <see cref="Timeout"/>.</param>
    /// <param name="delivered">
    /// <c>true</c> waits for the delivery report, which some networks never send; the status
    /// <c>sent</c> is not enough then.
    /// </param>
    /// <param name="pollInterval">Longest time between two questions; default is 1 second.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="TimeoutException">No result in time.</exception>
    public async Task<OutboxEntry> WaitForResultAsync(
        string requestId,
        TimeSpan? timeout = null,
        bool delivered = false,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromSeconds(1);
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentException("pollInterval must be positive");
        }
        var deadline = Timing.After(Effective(timeout));
        var pause = s_firstPoll < interval ? s_firstPoll : interval;
        while (true)
        {
            var entry = await MessageStatusAsync(requestId, cancellationToken).ConfigureAwait(false);
            var status = PyText.AsString(entry.Json["status"]);
            if (status is "delivered" or "failed" || (status == "sent" && !delivered))
            {
                return entry;
            }
            var remaining = deadline - Timing.Now;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException(
                    $"No result for the message {PyText.Repr(requestId)} in time; "
                    + $"its status is {PyText.Repr(entry.Json["status"])}");
            }
            await WaitForOutcomeEventAsync(
                requestId, Timing.Now + (pause < remaining ? pause : remaining), cancellationToken)
                .ConfigureAwait(false);
            pause = pause + pause < interval ? pause + pause : interval;
        }
    }

    /// <summary>
    /// Deletes one message. With mode <c>hidden</c> the message disappears from SMS Dock but
    /// stays on the phone. If the connection drops after the server deleted the message, the
    /// repeated request fails with <c>not_found</c>.
    /// </summary>
    /// <param name="messageId">The id of the message.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<DeleteResult> DeleteMessageAsync(
        long messageId, CancellationToken cancellationToken = default) =>
        new(await RequestAsync(
            "messages.delete", new JsonObject { ["messageId"] = messageId }, cancellationToken: cancellationToken)
            .ConfigureAwait(false));

    /// <summary>Deletes a conversation.</summary>
    /// <param name="conversationId">The id of the conversation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<DeleteResult> DeleteConversationAsync(
        long conversationId, CancellationToken cancellationToken = default) =>
        new(await RequestAsync(
            "conversations.delete",
            new JsonObject { ["conversationId"] = conversationId },
            cancellationToken: cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Marks all messages of a conversation as read. Android lets only the default SMS app
    /// change messages; otherwise the error is <c>not_default_app</c>.
    /// </summary>
    /// <param name="conversationId">The id of the conversation.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<MarkReadResult> MarkReadAsync(
        long conversationId, CancellationToken cancellationToken = default) =>
        new(await RequestAsync(
            "conversations.markRead",
            new JsonObject { ["conversationId"] = conversationId },
            cancellationToken: cancellationToken).ConfigureAwait(false));

    /// <summary>Returns one page of contacts, sorted by name.</summary>
    /// <param name="query">Search in names and numbers.</param>
    /// <param name="limit">Page size, 1 to 200.</param>
    /// <param name="cursor">The <see cref="Page{T}.NextCursor"/> of the previous page.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<Page<Contact>> ContactsAsync(
        string? query = null,
        int limit = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var fields = ContactFields(query);
        AddPaging(fields, limit, cursor);
        var page = await RequestAsync("contacts.list", fields, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new Page<Contact>(page, json => new Contact(json));
    }

    /// <summary>Yields contacts, sorted by name, page after page.</summary>
    /// <param name="query">Search in names and numbers.</param>
    /// <param name="pageSize">The <c>limit</c> of each request, 1 to 200.</param>
    /// <param name="maxItems">Ends after that many items; <c>null</c> yields all of them.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public IAsyncEnumerable<Contact> EnumerateContactsAsync(
        string? query = null,
        int pageSize = 50,
        int? maxItems = null,
        CancellationToken cancellationToken = default) =>
        Iterate(
            "contacts.list", ContactFields(query), pageSize, maxItems, json => new Contact(json), cancellationToken);

    // ------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------

    /// <summary>Asks for one event, or <c>*</c> for all, and returns the subscription now in force.</summary>
    /// <param name="eventName">The name of the event, or <c>*</c>.</param>
    /// <param name="address">Limits the <c>sms.*</c> events to one number; see the other overload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task<Subscription> SubscribeAsync(
        string eventName, string? address = null, CancellationToken cancellationToken = default) =>
        SubscribeAsync([eventName], address, cancellationToken);

    /// <summary>
    /// Asks for events and returns the subscription now in force. The names are added to the
    /// subscription. The client remembers the subscription and restores it after a reconnect.
    /// </summary>
    /// <param name="eventNames">Event names, or <c>*</c> for all.</param>
    /// <param name="address">
    /// Limits the <c>sms.*</c> events to one number and replaces the limit set before; without
    /// it the limit is removed. Numbers are compared as the phone does: <c>+43 699 1234567</c>
    /// and <c>0699 1234567</c> are the same.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<Subscription> SubscribeAsync(
        IEnumerable<string> eventNames, string? address = null, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["events"] = EventNames(eventNames) };
        if (address is not null)
        {
            fields["address"] = address;
        }
        var result = await RequestAsync("subscribe", fields, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        RememberSubscription(result);
        return new Subscription(result);
    }

    /// <summary>Stops one event, or all with <c>*</c>, and returns the subscription now in force.</summary>
    /// <param name="eventName">The name of the event, or <c>*</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public Task<Subscription> UnsubscribeAsync(string eventName, CancellationToken cancellationToken = default) =>
        UnsubscribeAsync([eventName], cancellationToken);

    /// <summary>
    /// Stops events and returns the subscription now in force. When no name is left, the limit
    /// to one number is removed too.
    /// </summary>
    /// <param name="eventNames">Event names, or <c>*</c> for all.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public async Task<Subscription> UnsubscribeAsync(
        IEnumerable<string> eventNames, CancellationToken cancellationToken = default)
    {
        var fields = new JsonObject { ["events"] = EventNames(eventNames) };
        var result = await RequestAsync("unsubscribe", fields, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        RememberSubscription(result);
        return new Subscription(result);
    }

    /// <summary>
    /// Yields events as they arrive. Events that were queued while a request waited for its
    /// response come first. Subscribe first, a new connection gets no events. After a lost
    /// connection the client reconnects and goes on.
    /// </summary>
    /// <param name="timeout">
    /// The longest time to wait for the next event; the enumeration ends when it passes.
    /// <c>null</c> waits without limit.
    /// </param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    /// <exception cref="ConnectionInterruptedException">The connection could not be restored.</exception>
    public async IAsyncEnumerable<SmsEvent> EventsAsync(
        TimeSpan? timeout = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_events.Count > 0)
            {
                var queued = _events[0];
                _events.RemoveAt(0);
                yield return new SmsEvent(queued);
                continue;
            }
            TimeSpan? deadline = timeout is { } wait ? Timing.After(wait) : null;
            var next = await ReadEventAsync(deadline, "waiting for events", cancellationToken)
                .ConfigureAwait(false);
            if (next is null)
            {
                yield break;
            }
            yield return new SmsEvent(next);
        }
    }

    /// <summary>Returns the next event with this name. Other events stay in the queue.</summary>
    /// <param name="eventName">The name of the event, or <c>*</c> for any.</param>
    /// <param name="timeout">Time to wait; default is <see cref="Timeout"/>.</param>
    /// <param name="predicate">Called with each candidate; returns <c>true</c> for the wanted one.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="TimeoutException">No such event in time.</exception>
    public Task<SmsEvent> WaitForEventAsync(
        string eventName,
        TimeSpan? timeout = null,
        Func<SmsEvent, bool>? predicate = null,
        CancellationToken cancellationToken = default) =>
        WaitForEventAsync([eventName], timeout, predicate, cancellationToken);

    /// <summary>Returns the next event with one of these names. Other events stay in the queue.</summary>
    /// <param name="eventNames">The names of the events.</param>
    /// <param name="timeout">Time to wait; default is <see cref="Timeout"/>.</param>
    /// <param name="predicate">Called with each candidate; returns <c>true</c> for the wanted one.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="TimeoutException">No such event in time.</exception>
    public async Task<SmsEvent> WaitForEventAsync(
        IEnumerable<string> eventNames,
        TimeSpan? timeout = null,
        Func<SmsEvent, bool>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(CheckedNames(eventNames), StringComparer.Ordinal);

        bool Wanted(JsonObject candidate)
        {
            if (!names.Contains("*")
                && (PyText.AsString(candidate["event"]) is not { } name || !names.Contains(name)))
            {
                return false;
            }
            return predicate is null || predicate(new SmsEvent(candidate));
        }

        for (var index = 0; index < _events.Count; index++)
        {
            if (Wanted(_events[index]))
            {
                var found = _events[index];
                _events.RemoveAt(index);
                return new SmsEvent(found);
            }
        }
        var deadline = Timing.After(Effective(timeout));
        var label = string.Join(", ", names.Order(StringComparer.Ordinal));
        while (true)
        {
            var next = await ReadEventAsync(deadline, $"waiting for {label}", cancellationToken)
                .ConfigureAwait(false);
            if (next is null)
            {
                throw new TimeoutException($"No event {label} in time");
            }
            if (Wanted(next))
            {
                return new SmsEvent(next);
            }
            QueueEvent(next);
        }
    }

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------

    private static void CheckFields(JsonObject? fields)
    {
        if (fields is not null && fields.ContainsKey("id"))
        {
            throw new ArgumentException("'id' is the id of the request; the client sets it");
        }
    }

    private static JsonObject ContactFields(string? query) =>
        query is null ? [] : new JsonObject { ["q"] = query };

    private static void AddPaging(JsonObject fields, int limit, string? cursor)
    {
        if (limit is < 1 or > SmsDockApi.MaxPageSize)
        {
            throw new ArgumentException($"limit must be between 1 and {SmsDockApi.MaxPageSize}");
        }
        fields["limit"] = limit;
        if (cursor is not null)
        {
            fields["cursor"] = cursor;
        }
    }

    private static List<string> CheckedNames(IEnumerable<string> eventNames)
    {
        ArgumentNullException.ThrowIfNull(eventNames);
        var names = eventNames.ToList();
        if (names.Count == 0 || names.Any(string.IsNullOrEmpty))
        {
            throw new ArgumentException("events must be one event name or several");
        }
        return names;
    }

    private static JsonArray EventNames(IEnumerable<string> eventNames)
    {
        var array = new JsonArray();
        foreach (var name in CheckedNames(eventNames))
        {
            array.Add(name);
        }
        return array;
    }

    private static JsonObject AsObject(JsonNode? value, string label) =>
        value as JsonObject ?? throw new ProtocolException($"The {label} is not a JSON object");

    private static ApiException ApiError(JsonObject error)
    {
        var code = error.TryGetPropertyValue("code", out var codeNode) ? PyText.Str(codeNode) : "unknown";
        var message = PyText.Truthy(error["message"]) ? PyText.Str(error["message"]) : "no message";
        var details = new JsonObject();
        foreach (var pair in error)
        {
            if (pair.Key is not ("code" or "message"))
            {
                details[pair.Key] = pair.Value?.DeepClone();
            }
        }
        return code switch
        {
            "invalid_password" or "locked_out" or "not_authenticated" =>
                new AuthenticationException(code, message, details),
            "licence_required" => new LicenceRequiredException(code, message, details),
            _ => new ApiException(code, message, details),
        };
    }

    /// <summary>Returns the error to raise for a close code that forbids a retry.</summary>
    private static SmsDockException? FatalError(TransportException error)
    {
        var reason = error.CloseReason;
        JsonObject Details() => new() { ["closeCode"] = error.CloseCode };
        return error.CloseCode switch
        {
            4401 => new AuthenticationException(
                "not_authenticated", reason ?? "The server closed the connection: not logged in", Details()),
            4410 => new AuthenticationException(
                "not_authenticated", reason ?? "The password was changed in the app", Details()),
            4403 => new LicenceRequiredException(
                "licence_required", reason ?? "The API needs the full licence of the app", Details()),
            1009 => new ProtocolException("The server closed the connection: frame larger than 64 KiB"),
            4400 => new ProtocolException(
                "The server closed the connection: protocol error" + (reason is null ? "" : $" ({reason})")),
            _ => null,
        };
    }

    private static ConnectionInterruptedException Interrupted(string message, TransportException error)
    {
        string cause;
        if (error.CloseCode is { } code)
        {
            cause = $"closed by the server with code {code}";
            if (error.CloseReason is { } reason)
            {
                cause += $" ({reason})";
            }
        }
        else
        {
            cause = error.Message;
        }
        return new ConnectionInterruptedException($"{message}: {cause}", error.CloseCode, error.CloseReason, error);
    }

    private TimeSpan Effective(TimeSpan? timeout) =>
        timeout is { } wait && wait != TimeSpan.Zero ? wait : Timeout;

    /// <summary>Connects, pins the certificate, logs in, restores the subscription.</summary>
    private async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (Insecure && !_warned)
        {
            _warned = true;
            (_error ?? Console.Error).Write(
                "smsdock: WARNING: the certificate of the server is not checked (insecure). "
                + "Anyone on the network can read the password and the messages.\n");
        }
        _connection = await Connection.OpenAsync(_uri, _fingerprint, ConnectTimeout, cancellationToken)
            .ConfigureAwait(false);
        ConnectionCount++;
        _lastActivity = Timing.Now;
        var reconnected = Epoch is not null;
        try
        {
            await HandshakeAsync(cancellationToken).ConfigureAwait(false);
            if (reconnected && OnReconnect is { } hook)
            {
                await hook(this, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var hello = await ReceiveAsync(Timeout, cancellationToken).ConfigureAwait(false);
        if (PyText.AsString(hello["event"]) != "hello")
        {
            throw new ProtocolException($"Expected 'hello', received {PyText.Repr(hello)}");
        }
        if (PyText.AsDouble(hello["protocolVersion"]) != SmsDockApi.ProtocolVersion)
        {
            throw new ProtocolException(
                $"The server speaks protocol version {PyText.Repr(hello["protocolVersion"])}, "
                + $"this client version {SmsDockApi.ProtocolVersion}");
        }
        var result = await RequestPatientlyAsync(
            "authenticate", new JsonObject { ["password"] = _password }, Timeout, RateLimitRetries,
            cancellationToken).ConfigureAwait(false);
        if (PyText.AsDouble(result["protocolVersion"]) != SmsDockApi.ProtocolVersion)
        {
            throw new ProtocolException(
                $"The login answered with protocol version {PyText.Repr(result["protocolVersion"])}");
        }
        _serverInfo = result["app"] as JsonObject;
        NoteLogin(PyText.AsInteger(result["epoch"]), PyText.AsInteger(result["latestSeq"]));
        if (_subscribedEvents is { Count: > 0 } names)
        {
            var fields = new JsonObject { ["events"] = EventNames(names) };
            if (_subscribedAddress is not null)
            {
                fields["address"] = _subscribedAddress;
            }
            await RequestPatientlyAsync("subscribe", fields, Timeout, RateLimitRetries, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Compares epoch and sequence number with the connection before.</summary>
    private void NoteLogin(long? epoch, long? latest)
    {
        if (epoch is null || latest is null)
        {
            throw new ProtocolException("The login answered without 'epoch' and 'latestSeq'");
        }
        if (Epoch is null)
        {
            ServerRestarted = false;
            MissedEvents = 0;
        }
        else if (epoch != Epoch)
        {
            ServerRestarted = true;
            MissedEvents = null;
        }
        else
        {
            ServerRestarted = false;
            MissedEvents = Math.Max(0, latest.Value - (LatestSeq ?? 0));
        }
        Epoch = epoch;
        LatestSeq = latest;
    }

    /// <summary>
    /// Sends a request; waits and repeats it while it is rate limited. Only when the server
    /// names the seconds to wait. The limit of messages per minute comes without them and is
    /// reported at once.
    /// </summary>
    private async Task<JsonObject> RequestPatientlyAsync(
        string action, JsonObject? fields, TimeSpan timeout, int retries, CancellationToken cancellationToken)
    {
        var refused = 0;
        while (true)
        {
            try
            {
                var key = await SendFrameAsync(action, fields, cancellationToken).ConfigureAwait(false);
                return await AwaitResponseAsync(
                    key, PyText.Repr(action), Timing.After(timeout), cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException error) when (
                error.Code == "rate_limited"
                && error.RetryAfter is { } wait
                && wait <= timeout.TotalSeconds
                && refused < retries)
            {
                refused++;
                await _delay(TimeSpan.FromSeconds(Math.Max(0, wait)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Closes after a failure and waits before the next attempt. Raises instead when the
    /// failure is final or no attempt is left.
    /// </summary>
    private async Task RecoverAsync(
        string what, TransportException error, int attempt, CancellationToken cancellationToken)
    {
        await CloseAsync().ConfigureAwait(false);
        if (FatalError(error) is { } fatal)
        {
            throw fatal;
        }
        if (ReconnectAttempts is { } attempts && attempt >= attempts)
        {
            throw Interrupted($"Connection interrupted while {what}", error);
        }
        var exponent = Math.Min(attempt, MaxBackoffExponent);
        var seconds = Math.Min(
            ReconnectDelay.TotalSeconds * Math.Pow(2, exponent), MaxReconnectDelay.TotalSeconds);
        await _delay(TimeSpan.FromSeconds(seconds), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> SendFrameAsync(
        string action, JsonObject? fields, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new ConnectionInterruptedException("No connection to SMS Dock");
        _requestNumber++;
        var key = $"cs-{_requestNumber}";
        var payload = new JsonObject { ["id"] = key, ["action"] = action };
        if (fields is not null)
        {
            foreach (var pair in fields)
            {
                payload[pair.Key] = pair.Value?.DeepClone();
            }
        }
        var frame = PyText.Compact(payload);
        if (Encoding.UTF8.GetByteCount(frame) > MaxFrameBytes)
        {
            throw new ArgumentException($"The request is larger than {MaxFrameBytes / 1024} KiB");
        }
        await connection.SendAsync(frame, Timeout, cancellationToken).ConfigureAwait(false);
        _lastActivity = Timing.Now;
        _outstanding.Add(key);
        return key;
    }

    private async Task<JsonObject> AwaitResponseAsync(
        string key, string label, TimeSpan deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (!_responses.Remove(key, out var response))
            {
                var remaining = deadline - Timing.Now;
                JsonObject message;
                try
                {
                    if (remaining <= TimeSpan.Zero)
                    {
                        throw new ReceiveTimeoutException();
                    }
                    message = await ReceiveAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (ReceiveTimeoutException)
                {
                    throw new ReceiveTimeoutException($"No response to {label} in time");
                }
                catch (OperationCanceledException)
                {
                    // Nobody waits for this response any more.
                    _outstanding.Remove(key);
                    throw;
                }
                if (message.ContainsKey("event"))
                {
                    QueueEvent(message);
                }
                else
                {
                    FileResponse(message);
                }
                continue;
            }
            _outstanding.Remove(key);
            var ok = response["ok"];
            if (ok is JsonValue && ok.GetValueKind() == JsonValueKind.True)
            {
                var result = response["result"];
                response.Remove("result");
                return AsObject(result, $"result of {label}");
            }
            if (ok is not JsonValue || ok.GetValueKind() != JsonValueKind.False
                || response["error"] is not JsonObject error)
            {
                throw new ProtocolException($"Malformed response: {PyText.Repr(response)}");
            }
            throw ApiError(error);
        }
    }

    /// <summary>Keeps a response for the request that waits for it.</summary>
    private void FileResponse(JsonObject response)
    {
        var identifier = response["id"];
        var key = identifier is null ? null : PyText.Str(identifier);
        if (key is not null && key == _keepAliveKey)
        {
            _keepAliveKey = null;
            return;
        }
        if (key is null && _outstanding.Count == 1)
        {
            // The server could not read the id of the only open request.
            key = _outstanding.First();
        }
        if (key is not null && _outstanding.Contains(key))
        {
            _responses[key] = response;
        }
        // Anything else answers a request nobody waits for any more.
    }

    private async Task<JsonObject> ReceiveAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new ConnectionInterruptedException("No connection to SMS Dock");
        var frame = await connection.ReceiveAsync(timeout, cancellationToken).ConfigureAwait(false);
        _lastActivity = Timing.Now;
        JsonNode? decoded;
        try
        {
            decoded = PyText.Parse(frame);
        }
        catch (JsonException error)
        {
            throw new ProtocolException("The server sent invalid JSON", error);
        }
        var message = AsObject(decoded, "frame");
        if (message.ContainsKey("event")
            && PyText.AsInteger(message["seq"]) is { } sequence
            && (LatestSeq is null || sequence > LatestSeq))
        {
            LatestSeq = sequence;
        }
        return message;
    }

    private void QueueEvent(JsonObject message)
    {
        if (_events.Count >= _eventQueueSize)
        {
            _events.RemoveAt(0);
            DroppedEvents++;
        }
        _events.Add(message);
    }

    /// <summary>
    /// Reads from the connection until an event arrives. Returns the event without queueing
    /// it, or <c>null</c> when the deadline passed. Reconnects when needed.
    /// </summary>
    private async Task<JsonObject?> ReadEventAsync(
        TimeSpan? deadline, string what, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            JsonObject message;
            try
            {
                if (_connection is null)
                {
                    await OpenAsync(cancellationToken).ConfigureAwait(false);
                }
                TimeSpan? wait = null;
                if (deadline is { } end)
                {
                    wait = end - Timing.Now;
                    if (wait <= TimeSpan.Zero)
                    {
                        return null;
                    }
                }
                var slice = await KeepAliveAsync(wait, cancellationToken).ConfigureAwait(false);
                try
                {
                    message = await ReceiveAsync(slice ?? wait, cancellationToken).ConfigureAwait(false);
                }
                catch (ReceiveTimeoutException)
                {
                    if (slice is null)
                    {
                        return null;
                    }
                    continue;
                }
            }
            catch (TransportException error)
            {
                await RecoverAsync(what, error, attempt, cancellationToken).ConfigureAwait(false);
                attempt++;
                continue;
            }
            attempt = 0;
            if (message.ContainsKey("event"))
            {
                return message;
            }
            FileResponse(message);
        }
    }

    /// <summary>
    /// Asks a silent server whether it is still there. Returns the time to wait before the
    /// next look at it, or <c>null</c> when the wait of the caller ends first.
    /// </summary>
    /// <exception cref="TransportException">The server did not answer in time.</exception>
    private async Task<TimeSpan?> KeepAliveAsync(TimeSpan? wait, CancellationToken cancellationToken)
    {
        if (_keepAliveInterval <= TimeSpan.Zero)
        {
            return null;
        }
        TimeSpan due;
        if (_keepAliveKey is not null)
        {
            due = _keepAliveSent + _keepAliveTimeout;
            if (Timing.Now >= due)
            {
                throw new TransportException("The server does not answer");
            }
        }
        else
        {
            due = _lastActivity + _keepAliveInterval;
            if (Timing.Now >= due)
            {
                var connection = _connection
                    ?? throw new ConnectionInterruptedException("No connection to SMS Dock");
                _requestNumber++;
                var key = $"cs-{_requestNumber}";
                var frame = PyText.Compact(new JsonObject { ["id"] = key, ["action"] = "ping" });
                await connection.SendAsync(frame, Timeout, cancellationToken).ConfigureAwait(false);
                _keepAliveKey = key;
                _keepAliveSent = Timing.Now;
                due = _keepAliveSent + _keepAliveTimeout;
            }
        }
        var slice = due - Timing.Now;
        if (slice < TimeSpan.FromMilliseconds(1))
        {
            slice = TimeSpan.FromMilliseconds(1);
        }
        return wait is { } limit && limit <= slice ? null : slice;
    }

    /// <summary>Returns at the deadline, or earlier when an outcome arrives.</summary>
    private async Task WaitForOutcomeEventAsync(
        string requestId, TimeSpan deadline, CancellationToken cancellationToken)
    {
        while (true)
        {
            var next = await ReadEventAsync(deadline, "waiting for the result", cancellationToken)
                .ConfigureAwait(false);
            if (next is null)
            {
                return;
            }
            QueueEvent(next);
            if (PyText.AsString(next["event"]) is "sms.sent" or "sms.delivered" or "sms.failed"
                && next["data"] is JsonObject data
                && PyText.AsString(data["requestId"]) == requestId)
            {
                return;
            }
        }
    }

    private IAsyncEnumerable<T> Iterate<T>(
        string action,
        JsonObject filters,
        int pageSize,
        int? maxItems,
        Func<JsonObject, T> create,
        CancellationToken cancellationToken)
    {
        if (maxItems is < 0)
        {
            throw new ArgumentException("maxItems cannot be negative");
        }
        AddPaging([], pageSize, null);
        return PagesAsync(action, filters, pageSize, maxItems, create, cancellationToken);
    }

    private async IAsyncEnumerable<T> PagesAsync<T>(
        string action,
        JsonObject filters,
        int pageSize,
        int? maxItems,
        Func<JsonObject, T> create,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        var count = 0;
        while (maxItems is null || count < maxItems)
        {
            var fields = (JsonObject)filters.DeepClone();
            AddPaging(fields, maxItems is { } most ? Math.Min(pageSize, most - count) : pageSize, cursor);
            var page = await RequestAsync(action, fields, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (page["items"] is not JsonArray array)
            {
                throw new ProtocolException($"{action} returned no list of items");
            }
            var items = array.ToList();
            // Detach the items, so that each can be put into another JSON value.
            array.Clear();
            foreach (var item in items)
            {
                yield return create(AsObject(item, $"item of {action}"));
                count++;
                if (maxItems is not null && count >= maxItems)
                {
                    yield break;
                }
            }
            var next = page["nextCursor"];
            if (next is null)
            {
                yield break;
            }
            if (PyText.AsString(next) is not { } nextCursor || nextCursor == cursor)
            {
                throw new ProtocolException($"{action} returned the cursor {PyText.Repr(next)}");
            }
            cursor = nextCursor;
        }
    }

    private void RememberSubscription(JsonObject result)
    {
        if (result["events"] is not JsonArray events)
        {
            throw new ProtocolException($"Malformed subscription: {PyText.Repr(result)}");
        }
        _subscribedEvents = events.Select(PyText.Str).ToList();
        _subscribedAddress = PyText.AsString(result["address"]);
    }

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex RequestIdPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9._-]{1,61}\z")]
    private static partial Regex GroupRequestIdPattern();
}
