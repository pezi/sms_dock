using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SmsDock;

/// <summary>Helpers for the SHA-256 fingerprint of the certificate of the phone.</summary>
public static partial class CertificateFingerprint
{
    /// <summary>
    /// Returns a SHA-256 fingerprint as 64 lower-case hex digits. Accepts hex with or without
    /// colons, in any case, and the line printed by <c>openssl x509 -fingerprint</c>
    /// (<c>sha256 Fingerprint=AB:CD:…</c>).
    /// </summary>
    /// <param name="fingerprint">The fingerprint in one of these forms.</param>
    /// <exception cref="ArgumentException">The text is no SHA-256 fingerprint.</exception>
    public static string Normalize(string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        var text = fingerprint.Trim();
        var equals = text.LastIndexOf('=');
        if (equals >= 0)
        {
            text = text[(equals + 1)..];
        }
        text = SeparatorPattern().Replace(text, "").ToLowerInvariant();
        if (!DigestPattern().IsMatch(text))
        {
            throw new ArgumentException(
                "The fingerprint must be the SHA-256 of the certificate: "
                + "64 hex digits, with or without colons");
        }
        return text;
    }

    /// <summary>Returns a fingerprint the way the app shows it: <c>AB:CD:…</c>.</summary>
    /// <param name="fingerprint">The fingerprint in one of the forms of <see cref="Normalize"/>.</param>
    /// <exception cref="ArgumentException">The text is no SHA-256 fingerprint.</exception>
    public static string Format(string fingerprint) => Format(Convert.FromHexString(Normalize(fingerprint)));

    /// <summary>Returns the SHA-256 fingerprint of a DER certificate as <c>AB:CD:…</c>.</summary>
    /// <param name="der">The certificate in DER form.</param>
    public static string FromDer(ReadOnlySpan<byte> der) => Format(SHA256.HashData(der));

    /// <summary>
    /// Connects to <paramref name="host"/> and returns the fingerprint of its certificate.
    /// This helper trusts nobody: it only reports what the server presented. Compare the
    /// result with the fingerprint on the Server screen of the app before you pin it. Nothing
    /// but the TLS handshake is exchanged.
    /// </summary>
    /// <param name="host">Name or address of the phone.</param>
    /// <param name="port">The port of the API.</param>
    /// <param name="timeout">Time to wait; 10 seconds when <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ConnectionInterruptedException">The server cannot be reached.</exception>
    /// <exception cref="ProtocolException">The server presented no certificate.</exception>
    public static async Task<string> FetchAsync(
        string host,
        int port = SmsDockApi.DefaultPort,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var wait = timeout ?? TimeSpan.FromSeconds(10);
        if (wait <= TimeSpan.Zero)
        {
            throw new ArgumentException("Timeouts must be positive");
        }
        byte[]? der = null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timing.Clamp(wait));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await using (tls.ConfigureAwait(false))
            {
                var options = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    // Accept whatever is presented: this call only reports it.
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        der = certificate?.GetRawCertData();
                        return true;
                    },
                };
                await tls.AuthenticateAsClientAsync(options, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ConnectionInterruptedException($"Cannot connect to {host}:{port}: timed out");
        }
        catch (Exception error) when (Connection.IsTransportError(error))
        {
            throw new ConnectionInterruptedException(
                $"Cannot connect to {host}:{port}: {Connection.Describe(error)}", innerException: error);
        }
        if (der is null || der.Length == 0)
        {
            throw new ProtocolException("The server presented no certificate");
        }
        return FromDer(der);
    }

    internal static string Format(ReadOnlySpan<byte> digest) =>
        string.Join(':', Convert.ToHexString(digest).Chunk(2).Select(pair => new string(pair)));

    [GeneratedRegex(@"[\s:]")]
    private static partial Regex SeparatorPattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex DigestPattern();
}
