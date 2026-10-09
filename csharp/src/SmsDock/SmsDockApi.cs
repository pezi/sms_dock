namespace SmsDock;

/// <summary>Constants of protocol version 1 of the SMS Dock WebSocket API.</summary>
public static class SmsDockApi
{
    /// <summary>Protocol version this client speaks.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Default port of the WebSocket API.</summary>
    public const int DefaultPort = 9443;

    /// <summary>Largest frame the server accepts from a client, in bytes.</summary>
    public const int MaxFrameBytes = 64 * 1024;

    /// <summary>Largest frame the client accepts, in bytes. Answers may exceed 64 KiB.</summary>
    public const int MaxIncomingBytes = 4 * 1024 * 1024;

    /// <summary>Largest value of <c>limit</c>.</summary>
    public const int MaxPageSize = 200;

    /// <summary>Environment variable that holds the password.</summary>
    public const string PasswordEnv = "SMSDOCK_PASSWORD";

    /// <summary>Environment variable that holds the certificate fingerprint.</summary>
    public const string FingerprintEnv = "SMSDOCK_FINGERPRINT";

    /// <summary>All event names of protocol version 1.</summary>
    public static IReadOnlyList<string> EventNames { get; } = Array.AsReadOnly(
    [
        "sms.received",
        "sms.queued",
        "sms.sent",
        "sms.delivered",
        "sms.failed",
        "sms.deleted",
        "conversations.changed",
    ]);
}
