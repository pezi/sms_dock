package smsdock

import (
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/pezi/sms_dock/go/internal/fakeserver"
)

// ----------------------------------------------------------------------
// Options
// ----------------------------------------------------------------------

func TestRefusesEverythingButWSS(t *testing.T) {
	cleanEnvironment(t)
	for _, address := range []string{
		"ws://192.168.1.50:9443/ws", "https://192.168.1.50:8443/",
		"192.168.1.50", "wss://", "", "wss:///ws",
	} {
		_, err := New(address, &Options{
			Password: testPassword, Fingerprint: testFingerprint,
		})
		wantArgumentError(t, err)
		if !strings.Contains(err.Error(), "wss://") {
			t.Fatalf("%q: got %v", address, err)
		}
	}
}

func TestReadsThePasswordFromTheEnvironment(t *testing.T) {
	cleanEnvironment(t)
	_, err := New(testURL, &Options{Fingerprint: testFingerprint})
	wantArgumentError(t, err)
	if !strings.Contains(err.Error(), PasswordEnv) {
		t.Fatalf("got %v", err)
	}
	t.Setenv(PasswordEnv, testPassword)
	client := must(New(testURL, &Options{Fingerprint: testFingerprint}))(t)
	equal(t, client.password, testPassword)
}

func TestNeedsAFingerprintUnlessInsecure(t *testing.T) {
	cleanEnvironment(t)
	_, err := New(testURL, &Options{Password: testPassword})
	wantArgumentError(t, err)
	if !strings.Contains(err.Error(), FingerprintEnv) {
		t.Fatalf("got %v", err)
	}
	must(New(testURL, &Options{Password: testPassword, Insecure: true}))(t)

	t.Setenv(FingerprintEnv, strings.ToLower(testFingerprint))
	client := must(New(testURL, &Options{Password: testPassword}))(t)
	equal(t, len(client.fingerprint), 32)
	equal(t, client.fingerprint[0], 0xAB)
}

func TestRefusesMalformedFingerprints(t *testing.T) {
	cleanEnvironment(t)
	for _, fingerprint := range []string{
		"AB:CD", "not a fingerprint", strings.Repeat("g", 64),
		strings.Repeat("ab", 31), strings.Repeat("ab", 33),
	} {
		_, err := New(testURL, &Options{
			Password: testPassword, Fingerprint: fingerprint,
		})
		wantArgumentError(t, err)
		if !strings.Contains(err.Error(), "SHA-256") {
			t.Fatalf("%q: got %v", fingerprint, err)
		}
	}
}

func TestRefusesImpossibleOptions(t *testing.T) {
	cleanEnvironment(t)
	for name, options := range map[string]Options{
		"timeout":         {Timeout: -time.Second},
		"connect timeout": {ConnectTimeout: -time.Second},
		"attempts":        {ReconnectAttempts: -3},
		"delay":           {ReconnectDelay: -time.Second},
		"max delay":       {MaxReconnectDelay: -time.Second},
		"queue":           {EventQueueSize: -1},
		"retries":         {RateLimitRetries: -2},
	} {
		options.Password, options.Fingerprint = testPassword, testFingerprint
		if _, err := New(testURL, &options); err == nil {
			t.Fatalf("%s: no error", name)
		} else {
			wantArgumentError(t, err)
		}
	}
}

func TestDefaults(t *testing.T) {
	cleanEnvironment(t)
	client := must(New(testURL, &Options{
		Password: testPassword, Fingerprint: testFingerprint,
	}))(t)
	equal(t, client.timeout, 30*time.Second)
	equal(t, client.connectTimeout, 10*time.Second)
	equal(t, client.reconnectAttempts, 5)
	equal(t, client.reconnectDelay, time.Second)
	equal(t, client.maxReconnectDelay, 10*time.Second)
	equal(t, client.eventQueueSize, 1000)
	equal(t, client.rateLimitRetries, 2)
	equal(t, client.Connected(), false)
	equal(t, client.Epoch(), 0)
	equal(t, client.ConnectionCount(), 0)

	client = must(New(testURL, &Options{
		Password: testPassword, Fingerprint: testFingerprint,
		ReconnectAttempts: NoReconnect, RateLimitRetries: NoRateLimitRetries,
	}))(t)
	equal(t, client.reconnectAttempts, 0)
	equal(t, client.rateLimitRetries, 0)
	client = must(New(testURL, &Options{
		Password: testPassword, Fingerprint: testFingerprint,
		ReconnectAttempts: ReconnectForever,
	}))(t)
	equal(t, client.reconnectAttempts, ReconnectForever)
}

func TestFingerprintForms(t *testing.T) {
	digits := strings.Repeat("ab", 32)
	colons := testFingerprint
	for _, form := range []string{
		digits, strings.ToUpper(digits), colons, strings.ToLower(colons),
		"  " + colons + "\n", "sha256 Fingerprint=" + colons,
		"SHA256 Fingerprint=" + colons,
	} {
		equal(t, must(NormalizeFingerprint(form))(t), digits)
		equal(t, must(FormatFingerprint(form))(t), colons)
	}
	_, err := NormalizeFingerprint("AB:CD")
	wantArgumentError(t, err)
	equal(t, CertificateFingerprint([]byte("abc")),
		"BA:78:16:BF:8F:01:CF:EA:41:41:40:DE:5D:AE:22:23:"+
			"B0:03:61:A3:96:17:7A:9C:B4:10:FF:61:F2:00:15:AD")
}

func TestStringDoesNotShowThePassword(t *testing.T) {
	cleanEnvironment(t)
	client := must(New(testURL, &Options{
		Password: "very-secret", Fingerprint: testFingerprint,
	}))(t)
	text := client.String()
	if strings.Contains(text, "very-secret") || !strings.Contains(text, testURL) ||
		!strings.Contains(text, "closed") {
		t.Fatalf("got %q", text)
	}
}

// ----------------------------------------------------------------------
// Login
// ----------------------------------------------------------------------

func TestLogsIn(t *testing.T) {
	server, client := setup(t)
	equal(t, client.Connected(), true)
	equal(t, *client.ServerInfo(), AppInfo{
		Name: "SMS Dock", Version: "1.0.0", ProtocolVersion: 1,
	})
	equal(t, client.ConnectionCount(), 1)
	equal(t, server.LoginCount(), 1)
	equal(t, client.Subscription() == nil, true)
	equalLists(t, server.Actions(), []string{"authenticate"})
	if !strings.Contains(client.String(), "connected") {
		t.Fatalf("got %q", client.String())
	}
	client.Close()
	equal(t, client.Connected(), false)
	if !waitUntil(func() bool { return server.ClientCount() == 0 }) {
		t.Fatal("the server still has a client")
	}
}

func TestDial(t *testing.T) {
	server := startServer(t, nil)
	client := must(Dial(ctx, server.URL(), &Options{
		Password: testPassword, Fingerprint: server.Fingerprint(),
	}))(t)
	defer client.Close()
	equal(t, must(client.Ping(ctx))(t).Time > 0, true)
}

func TestWrongPassword(t *testing.T) {
	server := startServer(t, nil)
	client := newClient(t, server, &Options{Password: "wrong-password"})
	err := client.Connect(ctx)
	failure := wantAPIError(t, err, "invalid_password")
	equal(t, failure.IsAuthentication(), true)
	equal(t, errors.Is(err, ErrAuthentication), true)
	equal(t, errors.Is(err, ErrLicenceRequired), false)
	equal(t, errors.Is(err, ErrSMSDock), true)
	if strings.Contains(err.Error(), "wrong-password") {
		t.Fatalf("the error shows the password: %v", err)
	}
	equal(t, client.Connected(), false)
	equal(t, server.LoginCount(), 1)
}

func TestWrongPasswordIsNotRetriedByRequests(t *testing.T) {
	server := startServer(t, nil)
	client := newClient(t, server, &Options{Password: "wrong-password"})
	_, err := client.Status(ctx)
	wantAPIError(t, err, "invalid_password")
	equal(t, server.LoginCount(), 1)
	equal(t, client.ConnectionCount(), 1)
}

func TestLockedOut(t *testing.T) {
	server := startServer(t, nil)
	server.LockLogins(30 * time.Second)
	client := newClient(t, server, nil)
	err := client.Connect(ctx)
	failure := wantAPIError(t, err, "locked_out")
	seconds, named := failure.RetryAfter()
	equal(t, named, true)
	if seconds < 29 || seconds > 30 {
		t.Fatalf("got retryAfter %d", seconds)
	}
	equal(t, errors.Is(err, ErrAuthentication), true)
	equal(t, server.LoginCount(), 1)
}

func TestLicenceRequired(t *testing.T) {
	server := startServer(t, nil)
	server.SetLicence(true, false)
	client := newClient(t, server, nil)
	err := client.Connect(ctx)
	failure := wantAPIError(t, err, "licence_required")
	equal(t, failure.IsLicenceRequired(), true)
	equal(t, errors.Is(err, ErrLicenceRequired), true)
	equal(t, errors.Is(err, ErrAuthentication), false)
	equal(t, server.LoginCount(), 1)

	server.SetLicence(true, true)
	if err := client.Connect(ctx); err != nil {
		t.Fatal(err)
	}
}

func TestRefusesOtherProtocolVersions(t *testing.T) {
	server := startServer(t, nil)
	server.SetProtocolVersion(2)
	client := newClient(t, server, nil)
	wantProtocolError(t, client.Connect(ctx), "protocol version 2")
	equal(t, client.Connected(), false)
	equal(t, server.LoginCount(), 0)
}

func TestCloseCode4401WhileLoggingIn(t *testing.T) {
	server := startServer(t, &fakeserver.Options{
		AuthTimeout: 100 * time.Millisecond,
	})
	server.IgnoreNext("authenticate", 1)
	client := newClient(t, server, nil)
	err := client.Connect(ctx)
	failure := wantAPIError(t, err, "not_authenticated")
	equal(t, failure.Details["closeCode"], any(4401))
	equal(t, failure.Message, "not authenticated in time")
	equal(t, client.ConnectionCount(), 1)
}

func TestTooManyClients(t *testing.T) {
	server := startServer(t, &fakeserver.Options{MaxClients: 1})
	connect(t, server, nil)
	second := newClient(t, server, nil)
	failure := wantInterrupted(t, second.Connect(ctx))
	equal(t, failure.CloseCode, 4409)
	equal(t, failure.CloseReason, "too many clients")
	if !strings.Contains(failure.Message, "Cannot connect") ||
		!strings.Contains(failure.Message, "4409") {
		t.Fatalf("got %q", failure.Message)
	}
}

func TestNothingListens(t *testing.T) {
	server := startServer(t, nil)
	client := newClient(t, server, &Options{ConnectTimeout: 2 * time.Second})
	server.Stop()
	failure := wantInterrupted(t, client.Connect(ctx))
	equal(t, failure.CloseCode, 0)
	equal(t, failure.Unwrap() != nil, true)
	equal(t, errors.Is(failure, ErrSMSDock), true)
}

// ----------------------------------------------------------------------
// Certificate
// ----------------------------------------------------------------------

func TestPinsTheCertificate(t *testing.T) {
	server := startServer(t, nil)
	for _, form := range []string{
		server.Fingerprint(),
		strings.ToLower(server.Fingerprint()),
		strings.ReplaceAll(server.Fingerprint(), ":", ""),
		"sha256 Fingerprint=" + server.Fingerprint(),
	} {
		client := connect(t, server, &Options{Fingerprint: form})
		equal(t, must(client.Status(ctx))(t).DeleteMode, "hidden")
	}
}

func TestCertificateMismatch(t *testing.T) {
	server := startServer(t, nil)
	other := must(fakeserver.NewCertificate())(t)
	client := newClient(t, server, &Options{Fingerprint: other.Fingerprint})
	err := client.Connect(ctx)
	var mismatch *CertificateMismatchError
	if !errors.As(err, &mismatch) {
		t.Fatalf("got %T (%v)", err, err)
	}
	equal(t, mismatch.Expected, other.Fingerprint)
	equal(t, mismatch.Actual, server.Fingerprint())
	equal(t, errors.Is(err, ErrSMSDock), true)
	for _, part := range []string{
		other.Fingerprint, server.Fingerprint(), "Server screen",
	} {
		if !strings.Contains(err.Error(), part) {
			t.Fatalf("%q is missing in %q", part, err.Error())
		}
	}
	equal(t, client.Connected(), false)
	// Nothing was sent: not the password, not even the WebSocket request.
	time.Sleep(50 * time.Millisecond)
	equal(t, server.ConnectionCount(), 0)
	equal(t, len(server.Requests()), 0)
	equal(t, server.LoginCount(), 0)
}

func TestCertificateMismatchIsNotRetried(t *testing.T) {
	server := startServer(t, nil)
	other := must(fakeserver.NewCertificate())(t)
	client := newClient(t, server, &Options{Fingerprint: other.Fingerprint})
	pauses := recordSleeps(client)
	_, err := client.Status(ctx)
	var mismatch *CertificateMismatchError
	if !errors.As(err, &mismatch) {
		t.Fatalf("got %T (%v)", err, err)
	}
	equal(t, len(*pauses), 0)
	equal(t, server.ConnectionCount(), 0)
}

func TestNewCertificateAfterReconnect(t *testing.T) {
	server := startServer(t, nil)
	client := connect(t, server, nil)
	fingerprint, port := server.Fingerprint(), server.Port()
	server.Stop()
	// Another server with another certificate takes over the port.
	impostor, err := fakeserver.Start(&fakeserver.Options{Port: port})
	if err != nil {
		t.Skipf("the port is taken: %v", err)
	}
	defer impostor.Stop()
	_, err = client.Status(ctx)
	var mismatch *CertificateMismatchError
	if !errors.As(err, &mismatch) {
		t.Fatalf("got %T (%v)", err, err)
	}
	equal(t, mismatch.Expected, fingerprint)
	equal(t, mismatch.Actual, impostor.Fingerprint())
	equal(t, impostor.ConnectionCount(), 0)
}

func TestFetchFingerprint(t *testing.T) {
	server := startServer(t, nil)
	fingerprint := must(FetchFingerprint(ctx, "127.0.0.1", server.Port(), 0))(t)
	equal(t, fingerprint, server.Fingerprint())
	time.Sleep(50 * time.Millisecond)
	equal(t, server.ConnectionCount(), 0)

	server.Stop()
	_, err := FetchFingerprint(ctx, "127.0.0.1", server.Port(), 2*time.Second)
	if err == nil {
		t.Fatal("no error although nothing listens")
	}
}

func TestInsecureSkipsTheCheckAndWarns(t *testing.T) {
	server := startServer(t, nil)
	client := must(New(server.URL(), &Options{
		Password: testPassword, Insecure: true,
		// Ignored: nothing is compared.
		Fingerprint: testFingerprint,
	}))(t)
	defer client.Close()
	warning := captureStderr(t, func() {
		if err := client.Connect(ctx); err != nil {
			t.Error(err)
		}
		server.DropConnections(0, "")
		if _, err := client.Ping(ctx); err != nil {
			t.Error(err)
		}
	})
	if !strings.HasPrefix(warning, "smsdock: WARNING:") ||
		!strings.Contains(warning, "not checked") {
		t.Fatalf("got %q", warning)
	}
	// Once, not for every connection.
	equal(t, strings.Count(warning, "WARNING"), 1)
	equal(t, client.ConnectionCount(), 2)
	if strings.Contains(warning, testPassword) {
		t.Fatal("the warning shows the password")
	}
}
