namespace SmsDock;

/// <summary>Options of <see cref="SmsDockClient"/>.</summary>
public sealed class SmsDockClientOptions
{
    /// <summary>
    /// SHA-256 fingerprint of the certificate as shown on the Server screen of the app: hex,
    /// with or without colons, in any case. Without it the environment variable
    /// <c>SMSDOCK_FINGERPRINT</c> is read.
    /// </summary>
    public string? Fingerprint { get; set; }

    /// <summary>
    /// <c>true</c> skips the check of the certificate and prints a warning to standard error.
    /// For tests only.
    /// </summary>
    public bool Insecure { get; set; }

    /// <summary>Time to wait for a response. Default: 30 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Time to open the connection. Default: 10 seconds.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Reconnect attempts after a lost connection; <c>0</c> for none, <c>null</c> for no limit.
    /// Default: 5.
    /// </summary>
    public int? ReconnectAttempts { get; set; } = 5;

    /// <summary>Time before the first reconnect attempt; doubled for each further one. Default: 1 second.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper limit of the time between reconnect attempts. Default: 10 seconds.</summary>
    public TimeSpan MaxReconnectDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Events kept while nobody reads them; after that the oldest are dropped and counted in
    /// <see cref="SmsDockClient.DroppedEvents"/>. Default: 1000.
    /// </summary>
    public int EventQueueSize { get; set; } = 1000;

    /// <summary>How often a request is repeated after the error <c>rate_limited</c>. Default: 2.</summary>
    public int RateLimitRetries { get; set; } = 2;

    /// <summary>
    /// Called with the client after every reconnect, once the subscription is restored. Use it
    /// to read what was missed. It can also be set on the client.
    /// </summary>
    public Func<SmsDockClient, CancellationToken, Task>? OnReconnect { get; set; }

    /// <summary>Reads an environment variable; replaced in tests.</summary>
    internal Func<string, string?> Environment { get; set; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Where the warning of <see cref="Insecure"/> goes; standard error when <c>null</c>.</summary>
    internal TextWriter? Error { get; set; }

    /// <summary>Waits between attempts; replaced in tests.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? Delay { get; set; }

    /// <summary>Idle time after which the client asks the server whether it is still there.</summary>
    internal TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Time the server has to answer that question.</summary>
    internal TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(20);
}
