using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace SmsDock.Tests;

public class ConstructionTests : ServerTestBase
{
    private static SmsDockClientOptions Options(Action<SmsDockClientOptions>? configure = null)
    {
        var options = new SmsDockClientOptions { Fingerprint = Fingerprint, Environment = _ => null };
        configure?.Invoke(options);
        return options;
    }

    [Theory]
    [InlineData("ws://192.168.1.50:9443/ws")]
    [InlineData("http://192.168.1.50:9443/ws")]
    [InlineData("https://192.168.1.50:9443/ws")]
    [InlineData("192.168.1.50:9443")]
    [InlineData("wss:///ws")]
    [InlineData("")]
    public void RefusesEverythingButWss(string url)
    {
        var error = Assert.Throws<ArgumentException>(() => new SmsDockClient(url, Password, Options()));
        Assert.Contains("wss://", error.Message);
    }

    [Fact]
    public async Task ReadsThePasswordFromTheEnvironment()
    {
        var server = Server;
        var client = new SmsDockClient(server.Url, null, new SmsDockClientOptions
        {
            Fingerprint = server.Tls.Fingerprint,
            Environment = name => name == "SMSDOCK_PASSWORD" ? Password : null,
        });
        await using (client)
        {
            await client.ConnectAsync();
            AssertJson($$"""{"id":"cs-1","action":"authenticate","password":"{{Password}}"}""", server.Requests[0]);
        }
    }

    [Fact]
    public void NeedsAPassword()
    {
        var error = Assert.Throws<ArgumentException>(() => new SmsDockClient(Url, null, Options()));
        Assert.Contains("SMSDOCK_PASSWORD", error.Message);
    }

    [Fact]
    public void NeedsAFingerprintUnlessInsecure()
    {
        var error = Assert.Throws<ArgumentException>(
            () => new SmsDockClient(Url, Password, new SmsDockClientOptions { Environment = _ => null }));
        Assert.Contains("SMSDOCK_FINGERPRINT", error.Message);
        var insecure = new SmsDockClient(
            Url, Password, new SmsDockClientOptions { Insecure = true, Environment = _ => null });
        Assert.True(insecure.Insecure);
    }

    [Fact]
    public async Task ReadsTheFingerprintFromTheEnvironment()
    {
        var server = Server;
        var client = new SmsDockClient(server.Url, Password, new SmsDockClientOptions
        {
            Environment = name => name == "SMSDOCK_FINGERPRINT" ? server.Tls.Fingerprint.ToLowerInvariant() : null,
        });
        await using (client)
        {
            Assert.True((await client.PingAsync()).Time > 0);
        }
        var error = Assert.Throws<ArgumentException>(() => new SmsDockClient(
            Url, Password, new SmsDockClientOptions { Environment = _ => "AB:CD" }));
        Assert.Contains("SHA-256", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB:CD")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB")]
    [InlineData("ABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABABAB")]
    [InlineData("ABABABABABABABABABABABABABABABABABABABAB")]
    public void RefusesMalformedFingerprints(string fingerprint)
    {
        var error = Assert.Throws<ArgumentException>(
            () => new SmsDockClient(Url, Password, Options(options => options.Fingerprint = fingerprint)));
        Assert.Contains("SHA-256", error.Message);
    }

    [Fact]
    public void RefusesImpossibleOptions()
    {
        Action<SmsDockClientOptions>[] wrong =
        [
            options => options.Timeout = TimeSpan.Zero,
            options => options.ConnectTimeout = TimeSpan.FromSeconds(-1),
            options => options.ReconnectAttempts = -1,
            options => options.ReconnectDelay = TimeSpan.FromSeconds(-1),
            options => options.EventQueueSize = 0,
            options => options.RateLimitRetries = -1,
        ];
        foreach (var configure in wrong)
        {
            Assert.Throws<ArgumentException>(() => new SmsDockClient(Url, Password, Options(configure)));
        }
    }

    [Fact]
    public void FingerprintForms()
    {
        var digits = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));
        var colons = string.Join(":", digits.Chunk(2).Select(pair => new string(pair)));
        Assert.Equal(digits, CertificateFingerprint.Normalize(digits.ToUpperInvariant()));
        Assert.Equal(digits, CertificateFingerprint.Normalize(colons));
        Assert.Equal(digits, CertificateFingerprint.Normalize($" {colons.ToUpperInvariant()}\n"));
        Assert.Equal(digits, CertificateFingerprint.Normalize($"sha256 Fingerprint={colons.ToUpperInvariant()}"));
        Assert.Equal(colons.ToUpperInvariant(), CertificateFingerprint.Format(digits));
        var der = Encoding.ASCII.GetBytes("certificate");
        Assert.Equal(
            CertificateFingerprint.Format(Convert.ToHexString(SHA256.HashData(der))),
            CertificateFingerprint.FromDer(der));
    }

    [Fact]
    public void ToStringDoesNotShowThePassword()
    {
        var client = new SmsDockClient(Url, "very-secret", Options());
        Assert.DoesNotContain("very-secret", client.ToString());
        Assert.Contains(Url, client.ToString());
    }

    [Fact]
    public void AllErrorsShareABaseClass()
    {
        Type[] kinds =
        [
            typeof(ApiException), typeof(AuthenticationException), typeof(LicenceRequiredException),
            typeof(ProtocolException), typeof(ConnectionInterruptedException), typeof(CertificateMismatchException),
        ];
        Assert.All(kinds, kind => Assert.True(kind.IsSubclassOf(typeof(SmsDockException))));
        Assert.True(typeof(AuthenticationException).IsSubclassOf(typeof(ApiException)));
        Assert.True(typeof(LicenceRequiredException).IsSubclassOf(typeof(ApiException)));
    }

    [Fact]
    public void WritesJsonLikePython()
    {
        var value = JsonNode.Parse(
            """{"a":"Grüße \"x\"\n\u0001","b":[1,2.5,1e20,true,null,{}],"c":{},"d":[],"e":-0,"f":1.0}""");
        Assert.Equal(
            """{"a":"Grüße \"x\"\n\u0001","b":[1,2.5,1e+20,true,null,{}],"c":{},"d":[],"e":0,"f":1.0}""",
            PyText.Compact(PyText.Parse(value!.ToJsonString())));
        var parsed = PyText.Parse("""{"a":[1,{"b":null}],"c":"ü"}""");
        Assert.Equal("""{"a": [1, {"b": null}], "c": "ü"}""", PyText.Line(parsed));
        Assert.Equal("{\n  \"a\": [\n    1,\n    {\n      \"b\": null\n    }\n  ],\n  \"c\": \"ü\"\n}", PyText.Pretty(parsed));
        Assert.Equal("{'a': [1, {'b': None}], 'c': 'ü'}", PyText.Repr(parsed));
        Assert.Equal("'it\\'s \"x\"'", PyText.Repr("it's \"x\""));
        Assert.Equal("\"it's\"", PyText.Repr("it's"));
        Assert.Equal("1e+16", PyText.FloatRepr(1e16));
        Assert.Equal("1234567.125", PyText.FloatRepr(1234567.125));
        Assert.Equal("1e-05", PyText.FloatRepr(0.00001));
        Assert.Equal("0.0001", PyText.FloatRepr(0.0001));
        Assert.Equal("100.0", PyText.FloatRepr(100));
    }
}
