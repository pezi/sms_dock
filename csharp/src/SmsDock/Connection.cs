using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace SmsDock;

/// <summary>
/// One WebSocket connection. A task reads frames all the time, so that pings of the server
/// are answered also while the caller does something else, and hands them over in the order
/// of their arrival.
/// </summary>
internal sealed class Connection
{
    private const int BufferedFrames = 256;

    private readonly ClientWebSocket _socket;
    private readonly Channel<string?> _frames = Channel.CreateBounded<string?>(
        new BoundedChannelOptions(BufferedFrames) { SingleReader = true, SingleWriter = true });

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _pump;
    private volatile TransportException? _failure;
    private volatile bool _closing;

    private Connection(ClientWebSocket socket)
    {
        _socket = socket;
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>
    /// Opens the WebSocket. With a pin, the certificate of the server is compared inside the
    /// TLS handshake, before anything is sent; without one every certificate is accepted.
    /// </summary>
    public static async Task<Connection> OpenAsync(
        Uri uri, byte[]? pin, TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        var check = new PinCheck(pin);
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.Proxy = null;
            socket.Options.RemoteCertificateValidationCallback = check.Validate;
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Timing.Clamp(connectTimeout));
            try
            {
                await socket.ConnectAsync(uri, limit.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException || IsTransportError(error))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (check.Mismatch is { } mismatch)
                {
                    throw mismatch;
                }
                if (limit.IsCancellationRequested)
                {
                    throw new TransportException("timed out while connecting", error);
                }
                throw new TransportException(Describe(error), error);
            }
            if (pin is not null && !check.Matched)
            {
                // Never go on with a certificate that was not compared.
                throw new ProtocolException(
                    "Cannot read the certificate of the server from the connection; refusing to go on");
            }
            return new Connection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Whether an error comes from the network, TLS or the WebSocket.</summary>
    public static bool IsTransportError(Exception error) =>
        error is WebSocketException or IOException or SocketException or HttpRequestException
            or System.Security.Authentication.AuthenticationException or ObjectDisposedException;

    /// <summary>Returns the text of the innermost error, which names the cause.</summary>
    public static string Describe(Exception error)
    {
        var innermost = error;
        while (innermost.InnerException is { } inner)
        {
            innermost = inner;
        }
        return string.IsNullOrEmpty(innermost.Message) ? innermost.GetType().Name : innermost.Message;
    }

    /// <summary>Sends one text frame.</summary>
    /// <exception cref="TransportException">The connection failed.</exception>
    public async Task SendAsync(string frame, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timing.Clamp(timeout));
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is OperationCanceledException or InvalidOperationException || IsTransportError(error))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The server may have closed the connection; its close frame tells why.
            await WaitForPumpAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            if (_failure is { CloseCode: not null } closed)
            {
                throw new TransportException(closed.CloseCode.Value, closed.CloseReason);
            }
            if (error is OperationCanceledException)
            {
                throw new TransportException("timed out while sending", error);
            }
            throw new TransportException(Describe(error), error);
        }
    }

    /// <summary>Returns the next text frame.</summary>
    /// <param name="timeout">The longest time to wait; <c>null</c> waits without limit.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="ReceiveTimeoutException">Nothing arrived in time.</exception>
    /// <exception cref="TransportException">The connection failed or was closed.</exception>
    /// <exception cref="ProtocolException">The server sent a binary frame.</exception>
    public async ValueTask<string> ReceiveAsync(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (!_frames.Reader.TryRead(out var frame))
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is { } wait)
            {
                limit.CancelAfter(Timing.Clamp(wait));
            }
            try
            {
                while (!_frames.Reader.TryRead(out frame))
                {
                    if (!await _frames.Reader.WaitToReadAsync(limit.Token).ConfigureAwait(false))
                    {
                        throw Failure();
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ReceiveTimeoutException();
            }
        }
        return frame ?? throw new ProtocolException("The server sent a binary frame");
    }

    /// <summary>Closes the connection: sends a close frame and waits for the one of the server.</summary>
    public async Task CloseAsync(TimeSpan timeout)
    {
        _closing = true;
        while (_frames.Reader.TryRead(out _))
        {
            // Frames nobody will read; make room so that the reader sees the close frame.
        }
        if (!_pump.IsCompleted && _socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var limit = new CancellationTokenSource(Timing.Clamp(timeout));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token)
                    .ConfigureAwait(false);
                await WaitForPumpAsync(timeout).ConfigureAwait(false);
            }
            catch (Exception error) when (
                error is OperationCanceledException or InvalidOperationException || IsTransportError(error))
            {
                // The connection is gone already.
            }
        }
        _stop.Cancel();
        _socket.Abort();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        finally
        {
            _socket.Dispose();
            _stop.Dispose();
        }
    }

    private TransportException Failure()
    {
        var failure = _failure;
        if (failure is null)
        {
            return new TransportException("The connection is closed");
        }
        return failure.CloseCode is { } code
            ? new TransportException(code, failure.CloseReason)
            : new TransportException(failure.Message, failure.InnerException);
    }

    private async Task WaitForPumpAsync(TimeSpan timeout)
    {
        using var limit = new CancellationTokenSource();
        var delay = Task.Delay(Timing.Clamp(timeout), limit.Token);
        await Task.WhenAny(_pump, delay).ConfigureAwait(false);
        limit.Cancel();
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _failure = new TransportException(
                        (int)(_socket.CloseStatus ?? WebSocketCloseStatus.Empty),
                        _socket.CloseStatusDescription);
                    await AnswerCloseAsync().ConfigureAwait(false);
                    return;
                }
                message.Write(buffer, 0, result.Count);
                if (message.Length > SmsDockApi.MaxIncomingBytes)
                {
                    _failure = new TransportException(
                        $"The server sent a frame larger than {SmsDockApi.MaxIncomingBytes / 1024} KiB");
                    _socket.Abort();
                    return;
                }
                if (!result.EndOfMessage)
                {
                    continue;
                }
                string? frame = result.MessageType == WebSocketMessageType.Text
                    ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)
                    : null;
                message.SetLength(0);
                if (!_closing)
                {
                    await _frames.Writer.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _failure ??= new TransportException("The connection is closed");
        }
        catch (Exception error) when (error is InvalidOperationException || IsTransportError(error))
        {
            _failure ??= new TransportException(Describe(error), error);
        }
        finally
        {
            _frames.Writer.TryComplete();
        }
    }

    private async Task AnswerCloseAsync()
    {
        if (_socket.State != WebSocketState.CloseReceived)
        {
            return;
        }
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, limit.Token)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (
            error is OperationCanceledException or InvalidOperationException || IsTransportError(error))
        {
            // The server did not wait for the answer.
        }
    }

    /// <summary>Compares the certificate of the server with the pinned fingerprint.</summary>
    private sealed class PinCheck(byte[]? pin)
    {
        /// <summary>Whether the certificate of the server has the pinned fingerprint.</summary>
        public bool Matched { get; private set; }

        /// <summary>The error to raise when it has another one.</summary>
        public CertificateMismatchException? Mismatch { get; private set; }

        public bool Validate(
            object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (pin is null)
            {
                // Insecure mode: nothing is compared.
                return true;
            }
            var der = certificate?.GetRawCertData();
            if (der is null || der.Length == 0)
            {
                Mismatch = new CertificateMismatchException(
                    CertificateFingerprint.Format(pin), "no certificate");
                return false;
            }
            var actual = SHA256.HashData(der);
            if (CryptographicOperations.FixedTimeEquals(actual, pin))
            {
                Matched = true;
                return true;
            }
            Mismatch = new CertificateMismatchException(
                CertificateFingerprint.Format(pin), CertificateFingerprint.Format(actual));
            return false;
        }
    }
}
