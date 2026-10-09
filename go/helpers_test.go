package smsdock

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/pezi/sms_dock/go/internal/fakeserver"
)

const (
	testPassword = "secret123"
	baseTime     = int64(1790672531000)
	anna         = "+4369912345678"
	ben          = "+4369087654321"
	testURL      = "wss://192.168.1.50:9443/ws"
)

var testFingerprint = strings.TrimSuffix(strings.Repeat("AB:", 32), ":")

var ctx = context.Background()

// cleanEnvironment hides the password and fingerprint of the developer.
func cleanEnvironment(t *testing.T) {
	t.Helper()
	t.Setenv(PasswordEnv, "")
	t.Setenv(FingerprintEnv, "")
}

// fill stores 29 messages in three conversations.
func fill(server *fakeserver.Server) {
	server.AddContact("Anna Example", anna, "Mobile")
	server.AddContact("Ben Example", ben, "Work")
	for index := 0; index < 25; index++ {
		direction := "in"
		if index%2 == 1 {
			direction = "out"
		}
		server.AddMessage(fakeserver.NewMessage{
			Address: anna, Body: fmt.Sprintf("Anna %d", index),
			Direction: direction, Timestamp: baseTime + int64(index)*1000,
			Unread: index >= 22,
		})
	}
	server.AddMessage(fakeserver.NewMessage{
		Address: ben, Body: "Your invoice is attached", Direction: "out",
		Timestamp: baseTime - 5000, Sim: 2,
	})
	server.AddMessage(fakeserver.NewMessage{
		Address: ben, Body: "Thanks for the invoice",
		Timestamp: baseTime - 4000, Sim: 2,
	})
	server.AddMessage(fakeserver.NewMessage{
		Address: ben, Body: "See you", Timestamp: baseTime - 3000, Sim: 2,
	})
	server.AddMessage(fakeserver.NewMessage{
		Address: "A1", Body: "Your bill is ready",
		Timestamp: baseTime - 9000, Unread: true,
	})
}

// startServer starts a fake server with the data of fill; it stops at
// the end of the test.
func startServer(t *testing.T, options *fakeserver.Options) *fakeserver.Server {
	t.Helper()
	cleanEnvironment(t)
	server, err := fakeserver.Start(options)
	if err != nil {
		t.Fatalf("The fake server did not start: %v", err)
	}
	t.Cleanup(server.Stop)
	fill(server)
	return server
}

// newClient makes a client for a server without connecting it; it is
// closed at the end of the test.
func newClient(
	t *testing.T, server *fakeserver.Server, options *Options,
) *Client {
	t.Helper()
	var settings Options
	if options != nil {
		settings = *options
	}
	if settings.ReconnectDelay == 0 {
		settings.ReconnectDelay = 10 * time.Millisecond
	}
	if settings.Timeout == 0 {
		settings.Timeout = 5 * time.Second
	}
	if settings.Password == "" {
		settings.Password = testPassword
	}
	if settings.Fingerprint == "" {
		settings.Fingerprint = server.Fingerprint()
	}
	client, err := New(server.URL(), &settings)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	t.Cleanup(func() { client.Close() })
	return client
}

// connect returns a client that is connected and logged in.
func connect(
	t *testing.T, server *fakeserver.Server, options *Options,
) *Client {
	t.Helper()
	client := newClient(t, server, options)
	if err := client.Connect(ctx); err != nil {
		t.Fatalf("Connect: %v", err)
	}
	return client
}

// setup returns a running server and a client connected to it.
func setup(t *testing.T) (*fakeserver.Server, *Client) {
	t.Helper()
	server := startServer(t, nil)
	return server, connect(t, server, nil)
}

// waitUntil polls until the condition holds; it returns whether it did.
func waitUntil(condition func() bool) bool {
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if condition() {
			return true
		}
		time.Sleep(5 * time.Millisecond)
	}
	return condition()
}

// recordSleeps replaces the pauses of the client by a list of their
// lengths.
func recordSleeps(client *Client) *[]time.Duration {
	var pauses []time.Duration
	client.sleep = func(_ context.Context, pause time.Duration) error {
		pauses = append(pauses, pause)
		return nil
	}
	return &pauses
}

// captureStderr returns what the function writes to standard error.
func captureStderr(t *testing.T, action func()) string {
	t.Helper()
	reader, writer, err := os.Pipe()
	if err != nil {
		t.Fatal(err)
	}
	original := os.Stderr
	os.Stderr = writer
	defer func() { os.Stderr = original }()
	collected := make(chan string)
	go func() {
		text, _ := io.ReadAll(reader)
		collected <- string(text)
	}()
	action()
	writer.Close()
	return <-collected
}

func must[T any](value T, err error) func(t *testing.T) T {
	return func(t *testing.T) T {
		t.Helper()
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		return value
	}
}

func equal[T comparable](t *testing.T, got, want T) {
	t.Helper()
	if got != want {
		t.Fatalf("got %v, want %v", got, want)
	}
}

func equalLists[T comparable](t *testing.T, got, want []T) {
	t.Helper()
	if len(got) != len(want) {
		t.Fatalf("got %v, want %v", got, want)
	}
	for index := range got {
		if got[index] != want[index] {
			t.Fatalf("got %v, want %v", got, want)
		}
	}
}

// wantAPIError checks that err is an APIError with this code.
func wantAPIError(t *testing.T, err error, code string) *APIError {
	t.Helper()
	var failure *APIError
	if !errors.As(err, &failure) {
		t.Fatalf("got %T (%v), want an APIError %s", err, err, code)
	}
	if failure.Code != code {
		t.Fatalf("got the code %s, want %s", failure.Code, code)
	}
	return failure
}

func wantArgumentError(t *testing.T, err error) {
	t.Helper()
	var failure *ArgumentError
	if !errors.As(err, &failure) {
		t.Fatalf("got %T (%v), want an ArgumentError", err, err)
	}
}

func wantProtocolError(t *testing.T, err error, part string) {
	t.Helper()
	var failure *ProtocolError
	if !errors.As(err, &failure) {
		t.Fatalf("got %T (%v), want a ProtocolError", err, err)
	}
	if !strings.Contains(failure.Message, part) {
		t.Fatalf("got %q, want a message with %q", failure.Message, part)
	}
}

func wantInterrupted(t *testing.T, err error) *ConnectionInterruptedError {
	t.Helper()
	var failure *ConnectionInterruptedError
	if !errors.As(err, &failure) {
		t.Fatalf("got %T (%v), want a ConnectionInterruptedError", err, err)
	}
	return failure
}

func wantTimeout(t *testing.T, err error) {
	t.Helper()
	var failure *TimeoutError
	if !errors.As(err, &failure) {
		t.Fatalf("got %T (%v), want a TimeoutError", err, err)
	}
}

// bodies returns the texts of messages.
func bodies(messages []Message) []string {
	texts := make([]string, len(messages))
	for index, message := range messages {
		texts[index] = message.Body
	}
	return texts
}

// collect reads an iterator to its end.
func collect[T any](t *testing.T, items func(func(T, error) bool)) []T {
	t.Helper()
	var all []T
	for item, err := range items {
		if err != nil {
			t.Fatalf("unexpected error: %v", err)
		}
		all = append(all, item)
	}
	return all
}

func count(list []string, wanted string) int {
	total := 0
	for _, entry := range list {
		if entry == wanted {
			total++
		}
	}
	return total
}

// eventFrame returns an sms.received event with this sequence number.
func eventFrame(sequence int) string {
	return fmt.Sprintf(
		`{"event":"sms.received","seq":%d,"data":{"message":{"id":%d,`+
			`"conversationId":170,"address":"%s","contact":null,`+
			`"direction":"in","body":"Event %d","timestamp":%d,`+
			`"sentTimestamp":null,"read":false,"status":"received",`+
			`"sim":1,"requestId":null}}}`,
		sequence, 5000+sequence, anna, sequence, baseTime)
}

var errDeadline = context.DeadlineExceeded

func contextWithTimeout(
	timeout time.Duration,
) (context.Context, context.CancelFunc) {
	return context.WithTimeout(context.Background(), timeout)
}
