package smsdock

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/pezi/sms_dock/go/internal/fakeserver"
)

func TestReconnectsAndLogsInAgain(t *testing.T) {
	server, client := setup(t)
	must(client.Ping(ctx))(t)
	server.DropConnections(0, "")
	equal(t, must(client.Status(ctx))(t).DeleteMode, "hidden")
	equal(t, client.ConnectionCount(), 2)
	equal(t, server.LoginCount(), 2)
	equalLists(t, server.Actions(),
		[]string{"authenticate", "ping", "authenticate", "status"})
}

func TestReconnectsAfterACloseFrame(t *testing.T) {
	server, client := setup(t)
	server.DropConnections(1001, "server is stopping")
	must(client.Ping(ctx))(t)
	server.DropConnections(4408, "no answer to pings")
	must(client.Ping(ctx))(t)
	server.DropConnections(4429, "too many requests")
	must(client.Ping(ctx))(t)
	equal(t, client.ConnectionCount(), 4)
}

func TestRequestLostBeforeItWasHandled(t *testing.T) {
	server, client := setup(t)
	server.DropOn("status", "before")
	equal(t, must(client.Status(ctx))(t).DeleteMode, "hidden")
	equal(t, count(server.Actions(), "status"), 2)
	equal(t, client.ConnectionCount(), 2)
}

func TestRestoresTheSubscription(t *testing.T) {
	server, client := setup(t)
	must(client.SubscribeAddress(ctx, ben, "sms.received", "sms.sent"))(t)
	server.DropConnections(0, "")
	must(client.Ping(ctx))(t)
	requests := server.RequestsOf("subscribe")
	equal(t, len(requests), 2)
	equal(t, requests[1]["address"], any(ben))
	names := requests[1]["events"].([]any)
	equal(t, len(names), 2)
	equal(t, names[0], any("sms.received"))
	equal(t, names[1], any("sms.sent"))
	// The subscription is in force: events arrive, filtered as before.
	server.ReceiveSMS(anna, "ignored")
	server.ReceiveSMS(ben, "wanted")
	event := must(client.WaitForEvent(ctx, 2*time.Second, nil, "sms.received"))(t)
	equal(t, event.Data.Message.Body, "wanted")
	equal(t, client.PendingEvents(), 0)
}

func TestDoesNotRestoreAnEmptySubscription(t *testing.T) {
	server, client := setup(t)
	must(client.Subscribe(ctx, "sms.received"))(t)
	must(client.Unsubscribe(ctx, "sms.received"))(t)
	server.DropConnections(0, "")
	must(client.Ping(ctx))(t)
	equal(t, count(server.Actions(), "subscribe"), 1)
}

func TestWaitingForEventsReconnects(t *testing.T) {
	server, client := setup(t)
	must(client.Subscribe(ctx, "sms.received"))(t)
	server.DropConnections(0, "")
	go func() {
		if waitUntil(func() bool {
			return count(server.Actions(), "subscribe") == 2
		}) {
			server.ReceiveSMS(anna, "after the reconnect")
		}
	}()
	var received []Event
	for event, err := range client.Events(ctx, 5*time.Second) {
		if err != nil {
			t.Fatal(err)
		}
		received = append(received, event)
		break
	}
	equal(t, len(received), 1)
	equal(t, received[0].Data.Message.Body, "after the reconnect")
	equal(t, client.ConnectionCount(), 2)
}

func TestSendIsNotRepeatedAfterALostResponse(t *testing.T) {
	server, client := setup(t)
	server.DropOn("messages.send", "after")
	result := must(client.Send(ctx, ben, "Only once", nil))(t)
	// The repeat tells the status of now; the message may be out already.
	if result.Status != "queued" && result.Status != "sent" {
		t.Fatalf("got the status %s", result.Status)
	}
	requests := server.RequestsOf("messages.send")
	equal(t, len(requests), 2)
	equal(t, requests[0]["requestId"], any(result.RequestID))
	equal(t, requests[1]["requestId"], any(result.RequestID))
	equal(t, requests[0]["id"] != requests[1]["id"], true)
	equal(t, client.ConnectionCount(), 2)
	entry := must(client.WaitForResult(ctx, result.RequestID, nil))(t)
	equal(t, entry.Status, "sent")
	// The radio was used once although the request arrived twice.
	sent := server.Sent()
	equal(t, len(sent), 1)
	equal(t, sent[0].Body, "Only once")
	stored := 0
	for _, message := range server.Messages() {
		if message.Body == "Only once" {
			stored++
		}
	}
	equal(t, stored, 1)
}

func TestWaitForResultSurvivesAReconnect(t *testing.T) {
	server := startServer(t, &fakeserver.Options{
		SendDelay: 300 * time.Millisecond,
	})
	client := connect(t, server, nil)
	must(client.Subscribe(ctx, "sms.sent", "sms.failed"))(t)
	result := must(client.Send(ctx, ben, "text", nil))(t)
	server.DropConnections(0, "")
	entry := must(client.WaitForResult(ctx, result.RequestID, &WaitOptions{
		PollInterval: 100 * time.Millisecond,
	}))(t)
	equal(t, entry.Status, "sent")
	equal(t, len(server.Sent()), 1)
}

func TestGivesUpAfterTheLastAttempt(t *testing.T) {
	server := startServer(t, nil)
	client := connect(t, server, &Options{
		ReconnectAttempts: 3, ConnectTimeout: 2 * time.Second,
	})
	server.Stop()
	_, err := client.Status(ctx)
	failure := wantInterrupted(t, err)
	if !strings.Contains(failure.Message, "'status'") {
		t.Fatalf("got %q", failure.Message)
	}
	equal(t, failure.Unwrap() != nil, true)
	equal(t, client.Connected(), false)
}

func TestBackoffIsBounded(t *testing.T) {
	server := startServer(t, nil)
	client := connect(t, server, &Options{
		ReconnectAttempts: 6, ReconnectDelay: time.Second,
		MaxReconnectDelay: 10 * time.Second, ConnectTimeout: 2 * time.Second,
	})
	server.Stop()
	pauses := recordSleeps(client)
	_, err := client.Status(ctx)
	wantInterrupted(t, err)
	equalLists(t, *pauses, []time.Duration{
		time.Second, 2 * time.Second, 4 * time.Second, 8 * time.Second,
		10 * time.Second, 10 * time.Second,
	})
}

func TestReconnectsWithoutLimit(t *testing.T) {
	server := startServer(t, nil)
	client := connect(t, server, &Options{
		ReconnectAttempts: ReconnectForever, ConnectTimeout: 2 * time.Second,
	})
	server.Stop()
	attempts := 0
	client.sleep = func(context.Context, time.Duration) error {
		attempts++
		if attempts == 40 {
			return context.Canceled
		}
		return nil
	}
	_, err := client.Status(ctx)
	equal(t, errors.Is(err, context.Canceled), true)
	equal(t, attempts, 40)
}

func TestACancelledContextEndsTheWait(t *testing.T) {
	server := startServer(t, nil)
	client := connect(t, server, &Options{
		ReconnectAttempts: ReconnectForever, ReconnectDelay: time.Hour,
	})
	server.Stop()
	limited, cancel := contextWithTimeout(200 * time.Millisecond)
	defer cancel()
	started := time.Now()
	_, err := client.Status(limited)
	equal(t, errors.Is(err, context.DeadlineExceeded), true)
	if time.Since(started) > 3*time.Second {
		t.Fatal("the context did not end the wait")
	}
}

func TestNoRetryOnRequestOnce(t *testing.T) {
	server, client := setup(t)
	server.DropOn("status", "before")
	_, err := client.RequestOnce(ctx, "status", nil)
	wantInterrupted(t, err)
	equal(t, client.ConnectionCount(), 1)
	must(client.Status(ctx))(t)
	equal(t, client.ConnectionCount(), 2)
}

func TestNoReconnect(t *testing.T) {
	server := startServer(t, nil)
	client := connect(t, server, &Options{ReconnectAttempts: NoReconnect})
	server.DropOn("status", "before")
	_, err := client.Status(ctx)
	wantInterrupted(t, err)
	equal(t, client.ConnectionCount(), 1)
}

func TestPipelinedRequestsDoNotReconnect(t *testing.T) {
	server, client := setup(t)
	server.DropOn("status", "after")
	id := must(client.SendRequest(ctx, "status", nil))(t)
	_, err := client.WaitForResponse(ctx, id, 2*time.Second)
	wantInterrupted(t, err)
	equal(t, client.ConnectionCount(), 1)
	equal(t, client.Connected(), false)
}

func TestCloseCodesThatForbidARetry(t *testing.T) {
	for code, errorCode := range map[int]string{
		4410: "not_authenticated",
		4401: "not_authenticated",
		4403: "licence_required",
	} {
		server, client := setup(t)
		server.DropConnections(code, "")
		_, err := client.Status(ctx)
		failure := wantAPIError(t, err, errorCode)
		equal(t, failure.Details["closeCode"], any(code))
		equal(t, errors.Is(err, ErrAuthentication), code != 4403)
		equal(t, errors.Is(err, ErrLicenceRequired), code == 4403)
		// No new login with a password that may be wrong by now.
		equal(t, server.LoginCount(), 1)
		equal(t, client.ConnectionCount(), 1)
		equal(t, client.Connected(), false)
	}
}

func TestPasswordChangedWhileListening(t *testing.T) {
	server, client := setup(t)
	must(client.Subscribe(ctx, "sms.received"))(t)
	timer := time.AfterFunc(200*time.Millisecond, func() {
		server.DropConnections(4410, "password changed")
	})
	defer timer.Stop()
	var last error
	for _, err := range client.Events(ctx, 5*time.Second) {
		last = err
	}
	failure := wantAPIError(t, last, "not_authenticated")
	equal(t, failure.Message, "password changed")
	equal(t, server.LoginCount(), 1)
}

func TestProtocolErrorCloseCodes(t *testing.T) {
	server, client := setup(t)
	server.DropConnections(4400, "binary frame")
	_, err := client.Status(ctx)
	wantProtocolError(t, err, "binary frame")
	equal(t, client.ConnectionCount(), 1)

	client = connect(t, server, nil)
	server.DropConnections(1009, "frame too large")
	_, err = client.Status(ctx)
	wantProtocolError(t, err, "64 KiB")
	equal(t, client.ConnectionCount(), 1)
}

func TestRequestTimeoutReconnects(t *testing.T) {
	server := startServer(t, nil)
	client := connect(t, server, &Options{
		Timeout: 300 * time.Millisecond, ReconnectAttempts: 1,
	})
	server.IgnoreNext("status", 1)
	equal(t, must(client.Status(ctx))(t).DeleteMode, "hidden")
	equal(t, client.ConnectionCount(), 2)
	server.IgnoreNext("status", 2)
	_, err := client.Status(ctx)
	failure := wantInterrupted(t, err)
	if !strings.Contains(failure.Message, "No response to 'status' in time") {
		t.Fatalf("got %q", failure.Message)
	}
	equal(t, client.ConnectionCount(), 3)
}

// ----------------------------------------------------------------------
// Epoch and sequence numbers
// ----------------------------------------------------------------------

func TestLoginTellsEpochAndSequenceNumber(t *testing.T) {
	server := startServer(t, nil)
	server.ReceiveSMS(anna, "one")
	server.ReceiveSMS(anna, "two")
	client := newClient(t, server, nil)
	equal(t, client.Epoch(), 0)
	equal(t, client.LatestSeq(), 0)
	if err := client.Connect(ctx); err != nil {
		t.Fatal(err)
	}
	equal(t, client.Epoch(), server.Epoch())
	equal(t, client.LatestSeq(), 2)
	equal(t, client.ServerRestarted(), false)
	missed, known := client.MissedEvents()
	equal(t, missed, 0)
	equal(t, known, true)

	// The sequence number follows the events.
	must(client.Subscribe(ctx, "sms.received"))(t)
	server.ReceiveSMS(anna, "three")
	event := must(client.WaitForEvent(ctx, 2*time.Second, nil, "sms.received"))(t)
	equal(t, event.Seq, 3)
	equal(t, client.LatestSeq(), 3)
}

func TestCountsTheEventsMissedWhileDisconnected(t *testing.T) {
	type call struct {
		missed       int64
		known        bool
		restarted    bool
		connected    bool
		subscription string
	}
	var calls []call
	server := startServer(t, nil)
	client := connect(t, server, &Options{OnReconnect: func(c *Client) {
		missed, known := c.MissedEvents()
		calls = append(calls, call{
			missed, known, c.ServerRestarted(), c.Connected(),
			strings.Join(c.Subscription().Events, ","),
		})
	}})
	equal(t, len(calls), 0)
	must(client.Subscribe(ctx, "sms.received"))(t)
	server.ReceiveSMS(anna, "seen")
	event := must(client.WaitForEvent(ctx, 2*time.Second, nil, "sms.received"))(t)
	equal(t, event.Seq, 1)
	server.DropConnections(0, "")
	if !waitUntil(func() bool { return server.ClientCount() == 0 }) {
		t.Fatal("the server still has a client")
	}
	for _, body := range []string{"lost one", "lost two", "lost three"} {
		server.ReceiveSMS(anna, body)
	}
	must(client.Ping(ctx))(t)
	missed, known := client.MissedEvents()
	equal(t, missed, 3)
	equal(t, known, true)
	equal(t, client.ServerRestarted(), false)
	equal(t, client.LatestSeq(), 4)
	// The hook runs once the subscription is in force again.
	equalLists(t, calls, []call{{3, true, false, true, "sms.received"}})

	server.DropConnections(0, "")
	must(client.Ping(ctx))(t)
	missed, _ = client.MissedEvents()
	equal(t, missed, 0)
	equal(t, len(calls), 2)
}

func TestNoticesARestartOfTheApp(t *testing.T) {
	server, client := setup(t)
	must(client.Subscribe(ctx, "sms.received"))(t)
	for _, body := range []string{"a", "b", "c", "d", "e"} {
		server.ReceiveSMS(anna, body)
	}
	equal(t, len(seqs(t, client)), 5)
	first := client.Epoch()
	server.Restart()
	server.ReceiveSMS(anna, "lost")
	must(client.Ping(ctx))(t)
	equal(t, client.ServerRestarted(), true)
	_, known := client.MissedEvents()
	equal(t, known, false)
	equal(t, client.Epoch(), server.Epoch())
	equal(t, client.Epoch() != first, true)
	equal(t, client.LatestSeq(), 1)
	server.ReceiveSMS(anna, "after")
	event := must(client.WaitForEvent(ctx, 2*time.Second, nil, "sms.received"))(t)
	equal(t, event.Seq, 2)

	server.DropConnections(0, "")
	must(client.Ping(ctx))(t)
	equal(t, client.ServerRestarted(), false)
	missed, known := client.MissedEvents()
	equal(t, missed, 0)
	equal(t, known, true)
}

func TestHookReadsWhatWasMissed(t *testing.T) {
	var seen []string
	var newest int64
	handle := func(message *Message) {
		seen = append(seen, message.Body)
		newest = max(newest, message.Timestamp)
	}
	catchUp := func(dock *Client) {
		var missed []Message
		for message, err := range dock.IterMessages(ctx, MessageFilter{
			Direction: "in", Since: time.UnixMilli(newest + 1),
		}, IterOptions{}) {
			if err != nil {
				t.Error(err)
				return
			}
			missed = append(missed, message)
		}
		for index := len(missed) - 1; index >= 0; index-- {
			handle(&missed[index])
		}
	}
	server := startServer(t, nil)
	client := connect(t, server, nil)
	// The hook can be set later.
	client.SetOnReconnect(catchUp)
	must(client.Subscribe(ctx, "sms.received"))(t)
	server.ReceiveSMS(anna, "first")
	event := must(client.WaitForEvent(ctx, 2*time.Second, nil, "sms.received"))(t)
	handle(event.Data.Message)
	server.DropConnections(0, "")
	if !waitUntil(func() bool { return server.ClientCount() == 0 }) {
		t.Fatal("the server still has a client")
	}
	time.Sleep(10 * time.Millisecond)
	server.ReceiveSMS(anna, "while away 1")
	time.Sleep(10 * time.Millisecond)
	server.ReceiveSMS(ben, "while away 2")
	go func() {
		if waitUntil(func() bool {
			return count(server.Actions(), "subscribe") == 2
		}) {
			time.Sleep(50 * time.Millisecond)
			server.ReceiveSMS(anna, "live again")
		}
	}()
	for event, err := range client.Events(ctx, 5*time.Second) {
		if err != nil {
			t.Fatal(err)
		}
		handle(event.Data.Message)
		break
	}
	equalLists(t, seen,
		[]string{"first", "while away 1", "while away 2", "live again"})
}

func TestLoginWithoutEpochIsRefused(t *testing.T) {
	server := startServer(t, nil)
	client := newClient(t, server, nil)
	server.BeforeNextResponse(fakeserver.Obj("id", "go-1", "ok", true,
		"result", fakeserver.Obj("protocolVersion", 1,
			"app", fakeserver.Obj("name", "SMS Dock", "version", "0.9.0"))))
	wantProtocolError(t, client.Connect(ctx), "epoch")
	equal(t, client.Connected(), false)
}

// ----------------------------------------------------------------------
// Rate limit
// ----------------------------------------------------------------------

func retryAfter(seconds int) fakeserver.Field {
	return fakeserver.Field{Name: "retryAfter", Value: seconds}
}

func TestWaitsAndRepeatsWhenRateLimited(t *testing.T) {
	server, client := setup(t)
	pauses := recordSleeps(client)
	server.FailNext("status", "rate_limited", "Too many requests.", 0, retryAfter(1))
	equal(t, must(client.Status(ctx))(t).DeleteMode, "hidden")
	equalLists(t, *pauses, []time.Duration{time.Second})
	server.FailNext("status", "rate_limited", "", 0, retryAfter(1))
	server.FailNext("status", "rate_limited", "", 0, retryAfter(3))
	equal(t, must(client.Status(ctx))(t).DeleteMode, "hidden")
	equalLists(t, *pauses,
		[]time.Duration{time.Second, time.Second, 3 * time.Second})
	equal(t, count(server.Actions(), "status"), 5)
	equal(t, client.ConnectionCount(), 1)
}

func TestGivesUpWhenRateLimitedAgainAndAgain(t *testing.T) {
	server, client := setup(t)
	pauses := recordSleeps(client)
	for range 3 {
		server.FailNext("status", "rate_limited", "", 0, retryAfter(1))
	}
	_, err := client.Status(ctx)
	failure := wantAPIError(t, err, "rate_limited")
	seconds, named := failure.RetryAfter()
	equal(t, seconds, 1)
	equal(t, named, true)
	equal(t, len(*pauses), 2)
	equal(t, count(server.Actions(), "status"), 3)
	must(client.Ping(ctx))(t)
}

func TestLimitOfMessagesPerMinuteIsReportedAtOnce(t *testing.T) {
	server, client := setup(t)
	pauses := recordSleeps(client)
	// The limit of 20 SMS parts per minute names no seconds to wait.
	server.FailNext("messages.send", "rate_limited",
		"Too many messages per minute. Try again shortly.", 0)
	_, err := client.Send(ctx, ben, "text", nil)
	failure := wantAPIError(t, err, "rate_limited")
	_, named := failure.RetryAfter()
	equal(t, named, false)
	equal(t, len(*pauses), 0)
	equal(t, len(server.Sent()), 0)
}

func TestRateLimitWithoutARepeat(t *testing.T) {
	for name, test := range map[string]struct {
		options Options
		seconds int
		once    bool
	}{
		"no retries":       {Options{RateLimitRetries: NoRateLimitRetries}, 1, false},
		"longer than wait": {Options{Timeout: 5 * time.Second}, 6, false},
		"request once":     {Options{}, 1, true},
	} {
		server := startServer(t, nil)
		client := connect(t, server, &test.options)
		pauses := recordSleeps(client)
		server.FailNext("status", "rate_limited", "", 0, retryAfter(test.seconds))
		var err error
		if test.once {
			_, err = client.RequestOnce(ctx, "status", nil)
		} else {
			_, err = client.Request(ctx, "status", nil)
		}
		wantAPIError(t, err, "rate_limited")
		if len(*pauses) != 0 {
			t.Fatalf("%s: waited %v", name, *pauses)
		}
	}
}

func TestSendIsNotRepeatedWhenRateLimited(t *testing.T) {
	server, client := setup(t)
	pauses := recordSleeps(client)
	server.FailNext("messages.send", "rate_limited", "", 0, retryAfter(1))
	result := must(client.Send(ctx, ben, "Only once", nil))(t)
	equal(t, len(*pauses), 1)
	requests := server.RequestsOf("messages.send")
	equal(t, len(requests), 2)
	equal(t, requests[0]["requestId"], any(result.RequestID))
	equal(t, requests[1]["requestId"], any(result.RequestID))
	equal(t, len(server.Sent()), 1)
}

func TestStaysWithinTheLimitOfTheServer(t *testing.T) {
	server := startServer(t, &fakeserver.Options{RequestsPerSecond: 5})
	client := connect(t, server, nil)
	started := time.Now()
	for range 9 {
		equal(t, must(client.Ping(ctx))(t).Time > 0, true)
	}
	// The login and 4 requests fit into the first second, 5 into the next.
	equal(t, server.RefusedCount() >= 1, true)
	equal(t, time.Since(started) >= 900*time.Millisecond, true)
	equal(t, count(server.Actions(), "ping"), 9)
	equal(t, client.ConnectionCount(), 1)
}
