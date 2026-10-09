using System.Text.Json.Nodes;

namespace SmsDock;

/// <summary>Base class of all errors raised by this library.</summary>
public class SmsDockException : Exception
{
    /// <summary>Creates the error.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The error that caused this one, if any.</param>
    public SmsDockException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The server answered a request with an error. <see cref="Exception.Message"/> is
/// <c>code: text</c>.
/// </summary>
public class ApiException : SmsDockException
{
    /// <summary>Creates the error.</summary>
    /// <param name="code">One of the error codes of <c>API.md</c>.</param>
    /// <param name="serverMessage">The text of the server.</param>
    /// <param name="details">The other fields of the error object.</param>
    public ApiException(string code, string serverMessage, JsonObject? details = null)
        : base($"{code}: {serverMessage}")
    {
        Code = code;
        ServerMessage = serverMessage;
        Details = details ?? [];
    }

    /// <summary>One of the error codes of <c>API.md</c>, for example <c>invalid_number</c>.</summary>
    public string Code { get; }

    /// <summary>The text of the server.</summary>
    public string ServerMessage { get; }

    /// <summary>
    /// The other fields of the error object, for example <c>{"retryAfter": 30}</c> or
    /// <c>{"permission": "sendSms"}</c>.
    /// </summary>
    public JsonObject Details { get; }

    /// <summary>Seconds to wait before the next attempt, if the server said so.</summary>
    public int? RetryAfter
    {
        get
        {
            var seconds = PyText.AsDouble(Details["retryAfter"]);
            if (seconds is null || double.IsNaN(seconds.Value))
            {
                return null;
            }
            return (int)Math.Clamp(Math.Truncate(seconds.Value), int.MinValue, int.MaxValue);
        }
    }

    /// <summary>The missing Android permission of a <c>permission_missing</c> error.</summary>
    public string? Permission => PyText.AsString(Details["permission"]);
}

/// <summary>
/// The login failed or the connection is no longer logged in. Codes: <c>invalid_password</c>,
/// <c>locked_out</c> (see <see cref="ApiException.RetryAfter"/>) and <c>not_authenticated</c>.
/// The client never retries after this error, because every wrong password counts towards
/// the lock of the phone.
/// </summary>
public class AuthenticationException : ApiException
{
    /// <summary>Creates the error.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="serverMessage">The text of the server.</param>
    /// <param name="details">The other fields of the error object.</param>
    public AuthenticationException(string code, string serverMessage, JsonObject? details = null)
        : base(code, serverMessage, details)
    {
    }
}

/// <summary>The API needs the full licence of the app (<c>licence_required</c>).</summary>
public class LicenceRequiredException : ApiException
{
    /// <summary>Creates the error.</summary>
    /// <param name="code">The error code.</param>
    /// <param name="serverMessage">The text of the server.</param>
    /// <param name="details">The other fields of the error object.</param>
    public LicenceRequiredException(string code, string serverMessage, JsonObject? details = null)
        : base(code, serverMessage, details)
    {
    }
}

/// <summary>The server sent something that protocol version 1 does not allow.</summary>
public class ProtocolException : SmsDockException
{
    /// <summary>Creates the error.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The error that caused this one, if any.</param>
    public ProtocolException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>The connection failed and could not be restored.</summary>
public class ConnectionInterruptedException : SmsDockException
{
    /// <summary>Creates the error.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="closeCode">The close code of the server, if it sent a close frame.</param>
    /// <param name="closeReason">The reason in the close frame, if any.</param>
    /// <param name="innerException">The error that caused this one, if any.</param>
    public ConnectionInterruptedException(
        string message, int? closeCode = null, string? closeReason = null, Exception? innerException = null)
        : base(message, innerException)
    {
        CloseCode = closeCode;
        CloseReason = closeReason;
    }

    /// <summary>The close code, set when the server closed the WebSocket with a close frame.</summary>
    public int? CloseCode { get; }

    /// <summary>The reason in the close frame of the server, if there was one.</summary>
    public string? CloseReason { get; }
}

/// <summary>
/// The server presented a certificate other than the pinned one. Nothing was sent to the
/// server.
/// </summary>
public class CertificateMismatchException : SmsDockException
{
    /// <summary>Creates the error.</summary>
    /// <param name="expected">The pinned fingerprint as <c>AB:CD:…</c>.</param>
    /// <param name="actual">The fingerprint of the certificate the server presented.</param>
    public CertificateMismatchException(string expected, string actual)
        : base(
            "The certificate of the server does not match the pinned fingerprint.\n"
            + $"  expected: {expected}\n  received: {actual}\n"
            + "Compare the fingerprint with the Server screen of the app. It changes when the "
            + "certificate is regenerated or the app is reinstalled.")
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>The pinned SHA-256 fingerprint: upper-case hex pairs separated by colons.</summary>
    public string Expected { get; }

    /// <summary>The fingerprint of the certificate of the server, or <c>no certificate</c>.</summary>
    public string Actual { get; }
}

/// <summary>A failure of the connection that a new connection may cure.</summary>
internal class TransportException : Exception
{
    public TransportException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public TransportException(int closeCode, string? closeReason)
        : base($"closed by the server with code {closeCode}")
    {
        CloseCode = closeCode;
        CloseReason = string.IsNullOrEmpty(closeReason) ? null : closeReason;
    }

    /// <summary>The close code of the server, if it sent a close frame.</summary>
    public int? CloseCode { get; }

    /// <summary>The reason in the close frame, if any.</summary>
    public string? CloseReason { get; }
}

/// <summary>Nothing arrived in the time given.</summary>
internal sealed class ReceiveTimeoutException : TransportException
{
    public ReceiveTimeoutException(string message = "timed out")
        : base(message)
    {
    }
}
