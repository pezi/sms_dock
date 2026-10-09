package smsdock

import (
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/pezi/sms_dock/go/internal/fakeserver"
)

// seqs reads events until none arrives for a short time.
func seqs(t *testing.T, client *Client) []int64 {
	t.Helper()
	var numbers []int64
	for event, err := range client.Events(ctx, 100*time.Millisecond) {
		if err != nil {
			t.Fatal(err)
		}
		numbers = append(numbers, event.Seq)
	}
	return numbers
}

func TestSubscribeAndUnsubscribe(t *testing.T) {
	server, client := setup(t)
	subscription := must(client.Subscribe(ctx, "sms.received"))(t)
	equalLists(t, subscription.Events, []string{"sms.received"})
	equal(t, subscription.Address == nil, true)
	equalLists(t, client.Subscription().Events, []string{"sms.received"})

	// Names are added; * stands for all.
	subscription = must(client.Subscribe(ctx, "sms.sent", "sms.failed"))(t)
	equalLists(t, subscription.Events,
		[]string{"sms.received", "sms.sent", "sms.failed"})
	subscription = must(client.Subscribe(ctx, "*"))(t)
	equalLists(t, subscription.Events, EventNames)

	// The address limits the sms.* events; each subscribe sets it anew.
	subscription = must(client.SubscribeAddress(ctx, ben, "sms.received"))(t)
	equal(t, *subscription.Address, ben)
	equal(t, *client.Subscription().Address, ben)
	request := server.RequestsOf("subscribe")[3]
	equal(t, request["address"], any(ben))
	subscription = must(client.Subscribe(ctx, "sms.received"))(t)
	equal(t, subscription.Address == nil, true)
	_, sent := server.RequestsOf("subscribe")[4]["address"]
	equal(t, sent, false)

	subscription = must(client.Unsubscribe(ctx, "sms.deleted", "sms.queued"))(t)
	equal(t, len(subscription.Events), 5)
	must(client.SubscribeAddress(ctx, anna, "sms.received"))(t)
	subscription = must(client.Unsubscribe(ctx, "*"))(t)
	equal(t, len(subscription.Events), 0)
	equal(t, subscription.Address == nil, true)
	equal(t, len(client.Subscription().Events), 0)

	_, err := client.Subscribe(ctx, "sms.unknown")
	wantAPIError(t, err, "invalid_request")
	_, err = client.Subscribe(ctx)
	wantArgumentError(t, err)
	_, err = client.Subscribe(ctx, "sms.received", "")
	wantArgumentError(t, err)
	// The copy handed out is not the one the client keeps.
	must(client.Subscribe(ctx, "sms.received"))(t)
	client.Subscription().Events[0] = "changed"
	equalLists(t, client.Subscription().Events, []string{"sms.received"})
}

func TestANewConnectionGetsNoEvents(t *testing.T) {
	server, client := setup(t)
	server.ReceiveSMS(anna, "nobody listens")
	equal(t, len(seqs(t, client)), 0)
	equal(t, client.PendingEvents(), 0)
}

func TestIncomingSMS(t *testing.T) {
	server, client := setup(t)
	must(client.Subscribe(ctx, "sms.received"))(t)
	stored := server.ReceiveSMS(anna, "STATUS")
	event := must(client.WaitForEvent(ctx, 2*time.Second, nil, "sms.received"))(t)
	equal(t, event.Name, "sms.received")
	equal(t, event.Seq, 1)
	message := event.Data.Message
	equal(t, *message.ID, stored.ID)
	equal(t, message.Address, anna)
	equal(t, *message.Contact, "Anna Example")
	equal(t, message.Body, "STATUS")
	equal(t, message.Read, false)
	equal(t, message.Status, "received")
	equal(t, strings.HasPrefix(string(event.Raw), `{"event":"sms.received","seq":1,`), true)
	equal(t, strings.HasPrefix(string(event.Data.Raw), `{"message":{"id":`), true)
	equal(t, strings.HasPrefix(string(message.Raw), `{"id":`), true)

	// React to it: a request inside the loop.
	server.ReceiveSMS(anna, "STATUS")
	for event, err := range client.Events(ctx, 2*time.Second) {
		if err != nil {
			t.Fatal(err)
		}
		must(client.Send(ctx, event.Data.Message.Address, "All systems running.",
			&SendOptions{Sim: event.Data.Message.Sim}))(t)
		break
	}
	equal(t, server.Sent()[0].To, anna)
}

func TestEventsBetweenRequestAndResponseAreQueued(t *testing.T) {
	server, client := setup(t)
	server.BeforeNextResponse(eventFrame(1), eventFrame(2))
	equal(t, must(client.Status(ctx))(t).DeleteMode, "hidden")
	equal(t, client.PendingEvents(), 2)
	equal(t, client.LatestSeq(), 2)
	server.BeforeNextResponse(eventFrame(3))
	must(client.Ping(ctx))(t)
	// In the order of their arrival.
	equalLists(t, seqs(t, client), []int64{1, 2, 3})
	equal(t, client.PendingEvents(), 0)
	equal(t, client.DroppedEvents(), 0)
}

func TestEventsEndAfterTheTimeout(t *testing.T) {
	_, client := setup(t)
	must(client.Subscribe(ctx, "sms.received"))(t)
	started := time.Now()
	equal(t, len(seqs(t, client)), 0)
	elapsed := time.Since(started)
	if elapsed < 90*time.Millisecond || elapsed > 2*time.Second {
		t.Fatalf("waited %v", elapsed)
	}
	equal(t, client.Connected(), true)
	equal(t, must(client.Ping(ctx))(t).Time > 0, true)
}

func TestEventsEndWhenTheContextIsDone(t *testing.T) {
	_, client := setup(t)
	must(client.Subscribe(ctx, "sms.received"))(t)
	limited, cancel := contextWithTimeout(100 * time.Millisecond)
	defer cancel()
	var last error
	for _, err := range client.Events(limited, 0) {
		last = err
	}
	equal(t, errors.Is(last, errDeadline), true)
	equal(t, must(client.Ping(ctx))(t).Time > 0, true)
	equal(t, client.ConnectionCount(), 1)
}

func TestWaitForEventKeepsTheOthers(t *testing.T) {
	server, client := setup(t)
	must(client.Subscribe(ctx, "*"))(t)
	server.ReceiveSMS(anna, "first")
	server.Emit("conversations.changed", fakeserver.Obj())
	server.ReceiveSMS(ben, "second")
	event := must(client.WaitForEvent(
		ctx, 2*time.Second, nil, "conversations.changed"))(t)
	equal(t, event.Seq, 2)
	equal(t, client.PendingEvents(), 1)

	fromBen := func(event Event) bool {
		return event.Data.Message != nil && event.Data.Message.Address == ben
	}
	event = must(client.WaitForEvent(
		ctx, 2*time.Second, fromBen, "sms.received", "sms.sent"))(t)
	equal(t, event.Data.Message.Body, "second")
	equal(t, client.PendingEvents(), 1)
	// No name stands for every event.
	event = must(client.WaitForEvent(ctx, 2*time.Second, nil))(t)
	equal(t, event.Data.Message.Body, "first")

	_, err := client.WaitForEvent(ctx, 100*time.Millisecond, nil, "sms.failed")
	wantTimeout(t, err)
	equal(t, err.Error(), "No event sms.failed in time")
}

func TestAddressFilter(t *testing.T) {
	server, client := setup(t)
	// The number is compared as the phone does.
	must(client.SubscribeAddress(ctx, "0690 876 54 321", "*"))(t)
	server.ReceiveSMS(anna, "ignored")
	server.ReceiveSMS(ben, "wanted")
	server.Emit("sms.delivered", fakeserver.Obj(
		"requestId", "r1", "id", 7, "conversationId", 171, "to", anna))
	server.Emit("sms.delivered", fakeserver.Obj(
		"requestId", "r2", "id", 8, "conversationId", 171, "to", ben))
	// Events that name no number always arrive.
	server.Emit("conversations.changed", fakeserver.Obj())
	equalLists(t, seqs(t, client), []int64{2, 4, 5})
	// The phone counts all events.
	equal(t, client.LatestSeq(), 5)
}

func TestEventsOfASentMessage(t *testing.T) {
	server := startServer(t, &fakeserver.Options{DeliveryReports: true})
	client := connect(t, server, nil)
	must(client.Subscribe(ctx, "*"))(t)
	result := must(client.Send(ctx, ben, "Hello", nil))(t)
	var names []string
	for len(names) < 3 {
		event := must(client.WaitForEvent(ctx, 2*time.Second, nil))(t)
		names = append(names, event.Name)
		equal(t, *event.Data.RequestID, result.RequestID)
		equal(t, *event.Data.To, ben)
		switch event.Name {
		case "sms.queued":
			equal(t, *event.Data.Parts, 1)
			equal(t, *event.Data.Sim, 1)
			equal(t, *event.Data.Source, "api")
		case "sms.sent":
			equal(t, event.Data.Message.Body, "Hello")
			equal(t, *event.Data.Message.RequestID, result.RequestID)
		case "sms.delivered":
			equal(t, *event.Data.ID > 0, true)
		}
	}
	equalLists(t, names, []string{"sms.queued", "sms.sent", "sms.delivered"})

	server.FailNumbers("+43123456")
	must(client.Send(ctx, "+43123456", "Hello", nil))(t)
	event := must(client.WaitForEvent(ctx, 2*time.Second, nil, "sms.failed"))(t)
	equal(t, *event.Data.ErrorCode, 1)
	equal(t, *event.Data.Reason, "generic_failure")
	equal(t, event.Data.ID == nil, true)
}

func TestFullEventQueueDropsTheOldest(t *testing.T) {
	server := startServer(t, nil)
	client := connect(t, server, &Options{EventQueueSize: 2})
	server.BeforeNextResponse(
		eventFrame(1), eventFrame(2), eventFrame(3), eventFrame(4))
	must(client.Ping(ctx))(t)
	equal(t, client.PendingEvents(), 2)
	equal(t, client.DroppedEvents(), 2)
	equalLists(t, seqs(t, client), []int64{3, 4})
}

func TestEventsOfAnUnexpectedForm(t *testing.T) {
	server, client := setup(t)
	server.BeforeNextResponse(
		`{"event":"sms.delivered","seq":"x","data":{"requestId":7,"to":"+431234567"}}`,
		`{"event":"future.event","seq":9,"data":[1,2]}`,
	)
	must(client.Ping(ctx))(t)
	first := must(client.WaitForEvent(ctx, time.Second, nil))(t)
	equal(t, first.Name, "sms.delivered")
	equal(t, first.Seq, 0)
	equal(t, *first.Data.To, "+431234567")
	second := must(client.WaitForEvent(ctx, time.Second, nil, "future.event"))(t)
	equal(t, second.Seq, 9)
	equal(t, len(second.Data.Raw), 0)
	equal(t, strings.Contains(string(second.Raw), "[1,2]"), true)
}

// ----------------------------------------------------------------------
// Several requests at once
// ----------------------------------------------------------------------

func TestResponsesInAnotherOrder(t *testing.T) {
	server, client := setup(t)
	server.ReverseNextResponses(3)
	first := must(client.SendRequest(
		ctx, "messages.list", map[string]any{"limit": 1}))(t)
	second := must(client.SendRequest(ctx, "status", nil))(t)
	third := must(client.SendRequest(ctx, "contacts.list", nil))(t)
	if first == second || second == third || first == third {
		t.Fatalf("the ids are not distinct: %s %s %s", first, second, third)
	}
	status := must(client.WaitForResponse(ctx, second, 2*time.Second))(t)
	equal(t, strings.Contains(string(status), `"deleteMode"`), true)
	messages := must(client.WaitForResponse(ctx, first, 2*time.Second))(t)
	equal(t, strings.Contains(string(messages), `"Anna 24"`), true)
	contacts := must(client.WaitForResponse(ctx, third, 2*time.Second))(t)
	equal(t, strings.Contains(string(contacts), `"Ben Example"`), true)
	_, err := client.WaitForResponse(ctx, first, 0)
	wantArgumentError(t, err)
	equal(t, must(client.Ping(ctx))(t).Time > 0, true)
}

func TestResponseErrorOfAPipelinedRequest(t *testing.T) {
	server, client := setup(t)
	server.ReverseNextResponses(2)
	missing := must(client.SendRequest(
		ctx, "messages.get", map[string]any{"messageId": 1}))(t)
	fine := must(client.SendRequest(ctx, "ping", nil))(t)
	_, err := client.WaitForResponse(ctx, missing, 2*time.Second)
	wantAPIError(t, err, "not_found")
	must(client.WaitForResponse(ctx, fine, 2*time.Second))(t)
}

func TestPipelinedRequestTimesOut(t *testing.T) {
	server, client := setup(t)
	server.IgnoreNext("status", 1)
	id := must(client.SendRequest(ctx, "status", nil))(t)
	_, err := client.WaitForResponse(ctx, id, 100*time.Millisecond)
	wantTimeout(t, err)
	// The connection stays open.
	equal(t, client.Connected(), true)
	equal(t, must(client.Ping(ctx))(t).Time > 0, true)
	equal(t, client.ConnectionCount(), 1)
}

func TestStaleResponsesAreIgnored(t *testing.T) {
	server, client := setup(t)
	server.BeforeNextResponse(
		fakeserver.Obj("id", "go-0", "ok", true,
			"result", fakeserver.Obj("stale", true)),
		fakeserver.Obj("id", 77, "ok", false, "error",
			fakeserver.Obj("code", "internal_error", "message", "stale")),
	)
	equal(t, must(client.Status(ctx))(t).DeleteMode, "hidden")
	equal(t, must(client.Ping(ctx))(t).Time > 0, true)
}

func TestResponseWithoutIDAnswersTheOpenRequest(t *testing.T) {
	server, client := setup(t)
	server.BeforeNextResponse(fakeserver.Obj("id", nil, "ok", false, "error",
		fakeserver.Obj("code", "invalid_json", "message", "Not a JSON object.")))
	_, err := client.Status(ctx)
	wantAPIError(t, err, "invalid_json")
	// The late real response is dropped; the next request works.
	equal(t, must(client.Ping(ctx))(t).Time > 0, true)
}

func TestMalformedFrames(t *testing.T) {
	for frame, message := range map[string]string{
		"this is not JSON":                     "invalid JSON",
		"[1, 2, 3]":                            "not a JSON object",
		`{"id":"go-2","ok":false}`:             "Malformed",
		`{"id":"go-2","ok":true,"result":[1]}`: "not a JSON object",
	} {
		server, client := setup(t)
		server.BeforeNextResponse(frame)
		_, err := client.Status(ctx)
		wantProtocolError(t, err, message)
		equal(t, errors.Is(err, ErrSMSDock), true)
	}
}
