using System.Text.Json.Nodes;

namespace SmsDock.Tests;

/// <summary>Handshake and certificate pinning over real TLS.</summary>
public class ConnectionTests : ServerTestBase
{
    [Fact]
    public async Task LogsIn()
    {
        var client = await ConnectAsync();
        Assert.True(client.Connected);
        AssertJson("""{"name":"SMS Dock","version":"1.0.0","protocolVersion":1}""", client.ServerInfo!.Json);
        Assert.Equal("SMS Dock", client.ServerInfo.Name);
        Assert.Equal(1, client.ConnectionCount);
        Assert.Equal(["authenticate"], Server.Actions);
        Assert.Equal(Password, (string?)Server.Requests[0]["password"]);
        Assert.Equal(1, Server.LoginCount);
    }

    [Fact]
    public async Task AwaitUsingCloses()
    {
        var client = NewClient(Server);
        await using (client)
        {
            Assert.True((await client.PingAsync()).Time > 0);
            Assert.Equal(1, Server.ClientCount);
        }
        Assert.False(client.Connected);
        Assert.True(await WaitUntilAsync(() => Server.ClientCount == 0));
    }

    [Fact]
    public async Task ConnectAsyncFactory()
    {
        await using var client = await SmsDockClient.ConnectAsync(
            Server.Url, Password, new SmsDockClientOptions { Fingerprint = Server.Tls.Fingerprint });
        Assert.True(client.Connected);
    }

    [Fact]
    public async Task WrongPassword()
    {
        var client = NewClient(Server, password: "wrong-password");
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => client.ConnectAsync());
        Assert.Equal("invalid_password", error.Code);
        Assert.False(client.Connected);
        // A wrong password counts towards the lock: it must not be repeated.
        Assert.Equal(1, Server.LoginCount);
    }

    [Fact]
    public async Task WrongPasswordIsNotRetriedByRequests()
    {
        var client = NewClient(Server, password: "wrong-password");
        await Assert.ThrowsAsync<AuthenticationException>(() => client.StatusAsync());
        Assert.Equal(1, Server.LoginCount);
    }

    [Fact]
    public async Task LockedOut()
    {
        Server.LockLogins(30);
        var client = NewClient(Server);
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => client.ConnectAsync());
        Assert.Equal("locked_out", error.Code);
        Assert.InRange(error.RetryAfter!.Value, 1, 30);
        AssertJson($$"""{"retryAfter":{{error.RetryAfter}}}""", error.Details);
        Assert.Equal(1, Server.LoginCount);
    }

    [Fact]
    public async Task LicenceRequired()
    {
        Server.LicenceEnforced = true;
        var client = NewClient(Server);
        var error = await Assert.ThrowsAsync<LicenceRequiredException>(() => client.ConnectAsync());
        Assert.Equal("licence_required", error.Code);
    }

    [Fact]
    public async Task RefusesOtherProtocolVersions()
    {
        Server.ProtocolVersion = 2;
        var client = NewClient(Server);
        var error = await Assert.ThrowsAsync<ProtocolException>(() => client.ConnectAsync());
        Assert.Contains("version 2", error.Message);
        // The password is not sent to a server of another protocol version.
        Assert.Empty(Server.Requests);
    }

    [Fact]
    public async Task CloseCode4401WhileLoggingIn()
    {
        var server = StartServer(new FakeOptions { AuthTimeout = 0.2 });
        server.IgnoreNext("authenticate");
        var client = NewClient(server);
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => client.ConnectAsync());
        Assert.Equal("not_authenticated", error.Code);
        Assert.Equal(4401, (int?)error.Details["closeCode"]);
        Assert.Equal(1, client.ConnectionCount);
    }

    [Fact]
    public async Task TooManyClients()
    {
        var server = StartServer(new FakeOptions { MaxClients = 1 });
        await ConnectAsync(server);
        var second = NewClient(server);
        var error = await Assert.ThrowsAsync<ConnectionInterruptedException>(() => second.ConnectAsync());
        Assert.Equal(4409, error.CloseCode);
        Assert.Equal("too many clients", error.CloseReason);
    }

    [Fact]
    public async Task NothingListens()
    {
        var server = StartServer();
        var client = NewClient(server, options => options.ConnectTimeout = TimeSpan.FromSeconds(2));
        await server.StopAsync();
        var error = await Assert.ThrowsAsync<ConnectionInterruptedException>(() => client.ConnectAsync());
        Assert.Contains("Cannot connect", error.Message);
    }

    [Fact]
    public async Task PinsTheCertificate()
    {
        var fingerprint = Server.Tls.Fingerprint;
        string[] forms =
        [
            fingerprint, fingerprint.ToLowerInvariant(), fingerprint.Replace(":", ""),
            fingerprint.Replace(":", "").ToLowerInvariant(),
        ];
        foreach (var form in forms)
        {
            var client = await ConnectAsync(configure: options => options.Fingerprint = form);
            Assert.True((await client.PingAsync()).Time > 0);
        }
    }

    [Fact]
    public void FingerprintOfTheLibraryMatchesAnIndependentOne() =>
        Assert.Equal(
            TestCertificate.Server.Fingerprint,
            CertificateFingerprint.FromDer(TestCertificate.Server.Certificate.RawData));

    [Fact]
    public async Task CertificateMismatch()
    {
        var client = NewClient(Server, options => options.Fingerprint = TestCertificate.Other.Fingerprint);
        var error = await Assert.ThrowsAsync<CertificateMismatchException>(() => client.ConnectAsync());
        Assert.Equal(TestCertificate.Other.Fingerprint, error.Expected);
        Assert.Equal(Server.Tls.Fingerprint, error.Actual);
        Assert.Contains(TestCertificate.Other.Fingerprint, error.Message);
        Assert.Contains(Server.Tls.Fingerprint, error.Message);
        Assert.False(client.Connected);
        await Task.Delay(100);
        // Nothing reached the server: no WebSocket, no password.
        Assert.Equal(0, Server.ConnectionCount);
        Assert.Empty(Server.Requests);
    }

    [Fact]
    public async Task CertificateMismatchIsNotRetried()
    {
        var client = NewClient(Server, options => options.Fingerprint = TestCertificate.Other.Fingerprint);
        await Assert.ThrowsAsync<CertificateMismatchException>(() => client.StatusAsync());
        Assert.Equal(0, client.ConnectionCount);
    }

    [Fact]
    public async Task NewCertificateAfterReconnect()
    {
        var first = StartServer();
        var client = await ConnectAsync(first);
        await client.PingAsync();
        await first.StopAsync();
        var second = StartServer(new FakeOptions { Tls = TestCertificate.Other, Port = first.Port });
        await Assert.ThrowsAsync<CertificateMismatchException>(() => client.StatusAsync());
        Assert.Empty(second.Requests);
    }

    [Fact]
    public async Task FetchFingerprint()
    {
        Assert.Equal(Server.Tls.Fingerprint, await CertificateFingerprint.FetchAsync("127.0.0.1", Server.Port));
        await Task.Delay(100);
        Assert.Empty(Server.Requests);
        Assert.Equal(0, Server.ConnectionCount);
    }

    [Fact]
    public async Task FetchFingerprintOfNothing()
    {
        var server = StartServer();
        await server.StopAsync();
        await Assert.ThrowsAsync<ConnectionInterruptedException>(
            () => CertificateFingerprint.FetchAsync("127.0.0.1", server.Port, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task InsecureSkipsTheCheckAndWarns()
    {
        var client = await ConnectAsync(configure: options =>
        {
            options.Fingerprint = null;
            options.Insecure = true;
        });
        Assert.True((await client.PingAsync()).Time > 0);
        await Server.DropConnectionsAsync();
        Assert.True((await client.PingAsync()).Time > 0);
        var warnings = Warnings.ToString();
        Assert.Single(warnings.Split("WARNING").Skip(1));
        Assert.Contains("not checked", warnings);
    }

    [Fact]
    public async Task LoginWithoutEpochIsRefused()
    {
        var client = NewClient(Server);
        Server.BeforeNextResponse(new JsonObject
        {
            ["id"] = "cs-1",
            ["ok"] = true,
            ["result"] = new JsonObject
            {
                ["protocolVersion"] = 1,
                ["app"] = new JsonObject { ["name"] = "SMS Dock", ["version"] = "0.9.0" },
            },
        });
        var error = await Assert.ThrowsAsync<ProtocolException>(() => client.ConnectAsync());
        Assert.Contains("epoch", error.Message);
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task CancellationStopsARequest()
    {
        var client = await ConnectAsync();
        Server.IgnoreNext("status");
        using var cancel = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.StatusAsync(cancel.Token));
        // The connection is still usable.
        Assert.True((await client.PingAsync()).Time > 0);
        Assert.Equal(1, client.ConnectionCount);
    }
}
