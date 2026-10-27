package smsdock

import (
	"encoding/json"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/pezi/sms_dock/go/internal/fakeserver"
)

func TestStatus(t *testing.T) {
	_, client := setup(t)
	status := must(client.Status(ctx))(t)
	equal(t, status.App, AppInfo{Name: "SMS Dock", Version: "1.0.0", ProtocolVersion: 1})
	equal(t, len(status.Sims), 2)
	equal(t, status.Sims[0].Number == nil, true)
	equal(t, *status.Sims[1].Number, ben)
	equal(t, status.Sims[0].IsDefault, true)
	equal(t, status.Permissions["sendSms"], true)
	equal(t, len(status.Permissions), 6)
	equal(t, status.DefaultSmsApp, false)
	equal(t, status.DeleteMode, "hidden")
	equal(t, status.Licence.DailyLimit, 10)
	equal(t, status.Licence.Remaining == nil, true)
	equal(t, status.Popup, true)
	// Raw is the object of the server, with its fields in their order.
	if !strings.HasPrefix(string(status.Raw), `{"app":{"name":"SMS Dock"`) {
		t.Fatalf("got %s", status.Raw)
	}
}

func TestPing(t *testing.T) {
	_, client := setup(t)
	before := time.Now().Add(-time.Minute).UnixMilli()
	equal(t, must(client.Ping(ctx))(t).Time > before, true)
}

func TestConversations(t *testing.T) {
	_, client := setup(t)
	page := must(client.Conversations(ctx, ConversationFilter{}, PageOptions{}))(t)
	equal(t, len(page.Items), 3)
	equal(t, page.NextCursor, "")
	first := page.Items[0]
	equal(t, first.Address, anna)
	equal(t, *first.Contact, "Anna Example")
	equal(t, first.Snippet, "Anna 24")
	equal(t, first.Timestamp, baseTime+24000)
	equal(t, first.Direction, "in")
	equal(t, first.MessageCount, 25)
	equal(t, first.UnreadCount, 3)
	equal(t, *first.Sim, 1)
	equal(t, page.Items[2].Address, "A1")
	equal(t, page.Items[2].Contact == nil, true)
	if !strings.Contains(string(first.Raw), `"snippet":"Anna 24"`) {
		t.Fatalf("got %s", first.Raw)
	}
}

func TestConversationsFilters(t *testing.T) {
	server, client := setup(t)
	addresses := func(filter ConversationFilter) []string {
		page := must(client.Conversations(ctx, filter, PageOptions{}))(t)
		var found []string
		for _, item := range page.Items {
			found = append(found, item.Address)
		}
		return found
	}
	equalLists(t, addresses(ConversationFilter{Unread: true}), []string{anna, "A1"})
	equalLists(t, addresses(ConversationFilter{Query: "invoice"}), []string{ben})
	equalLists(t, addresses(ConversationFilter{Query: "anna"}), []string{anna})
	equalLists(t, addresses(ConversationFilter{Sim: Ptr(2)}), []string{ben})
	request := server.RequestsOf("conversations.list")[3]
	equal(t, request["sim"], any(2.0))
	equal(t, request["limit"], any(50.0))
	_, hasUnread := request["unread"]
	equal(t, hasUnread, false)
}

func TestConversationsPaging(t *testing.T) {
	_, client := setup(t)
	first := must(client.Conversations(
		ctx, ConversationFilter{}, PageOptions{Limit: 2}))(t)
	equal(t, len(first.Items), 2)
	equal(t, first.NextCursor != "", true)
	second := must(client.Conversations(
		ctx, ConversationFilter{},
		PageOptions{Limit: 2, Cursor: first.NextCursor}))(t)
	equal(t, len(second.Items), 1)
	equal(t, second.NextCursor, "")
	all := collect(t, client.IterConversations(
		ctx, ConversationFilter{}, IterOptions{PageSize: 1}))
	equal(t, len(all), 3)
}

func TestLastTenIncomingMessages(t *testing.T) {
	server, client := setup(t)
	page := must(client.Messages(
		ctx, MessageFilter{Direction: "in"}, PageOptions{Limit: 10}))(t)
	equal(t, len(page.Items), 10)
	equal(t, page.NextCursor != "", true)
	newest := page.Items[0]
	equal(t, newest.Body, "Anna 24")
	equal(t, newest.Direction, "in")
	equal(t, newest.Address, anna)
	equal(t, *newest.Contact, "Anna Example")
	equal(t, newest.Sender(), "Anna Example")
	equal(t, newest.Timestamp, baseTime+24000)
	equal(t, *newest.SentTimestamp, baseTime+22000)
	equal(t, newest.Read, false)
	equal(t, newest.Status, "received")
	equal(t, *newest.Sim, 1)
	equal(t, newest.RequestID == nil, true)
	equal(t, newest.Time().UnixMilli(), baseTime+24000)
	for _, message := range page.Items {
		equal(t, message.Direction, "in")
	}
	request := server.RequestsOf("messages.list")[0]
	equal(t, request["direction"], any("in"))
	equal(t, request["limit"], any(10.0))
}

func TestMessagesFilters(t *testing.T) {
	server, client := setup(t)
	list := func(filter MessageFilter) []string {
		return bodies(must(client.Messages(
			ctx, filter, PageOptions{Limit: 200}))(t).Items)
	}
	equal(t, len(list(MessageFilter{Address: ben})), 3)
	// The number is compared as the phone does.
	equal(t, len(list(MessageFilter{Address: "0690 876 54 321"})), 3)
	equal(t, len(list(MessageFilter{})), 29)
	equal(t, len(list(MessageFilter{Direction: "all"})), 29)
	equal(t, len(list(MessageFilter{Direction: "out"})), 13)
	equalLists(t, list(MessageFilter{Query: "invoice"}),
		[]string{"Thanks for the invoice", "Your invoice is attached"})
	equal(t, len(list(MessageFilter{Unread: true})), 4)
	equal(t, len(list(MessageFilter{Sim: Ptr(2)})), 3)
	equalLists(t, list(MessageFilter{Address: "a1"}),
		[]string{"Your bill is ready"})

	// The conversation wins over the address.
	conversation := server.Messages()[0].ConversationID
	equal(t, len(list(MessageFilter{
		Address: ben, ConversationID: &conversation,
	})), 25)

	// Both ends of the time range are inclusive.
	equalLists(t, list(MessageFilter{
		Since: time.UnixMilli(baseTime + 22000),
		Until: time.UnixMilli(baseTime + 23000),
	}), []string{"Anna 23", "Anna 22"})
	request := server.RequestsOf("messages.list")
	last := request[len(request)-1]
	equal(t, last["from"], any(float64(baseTime+22000)))
	equal(t, last["to"], any(float64(baseTime+23000)))
}

func TestIterMessagesFollowsTheCursor(t *testing.T) {
	server, client := setup(t)
	all := collect(t, client.IterMessages(
		ctx, MessageFilter{Address: anna}, IterOptions{PageSize: 10}))
	equal(t, len(all), 25)
	equal(t, all[0].Body, "Anna 24")
	equal(t, all[24].Body, "Anna 0")
	requests := server.RequestsOf("messages.list")
	equal(t, len(requests), 3)
	_, hasCursor := requests[0]["cursor"]
	equal(t, hasCursor, false)
	equal(t, requests[1]["cursor"] != nil, true)
}

func TestIterMessagesIsLazyAndBounded(t *testing.T) {
	server, client := setup(t)
	seen := 0
	for _, err := range client.IterMessages(
		ctx, MessageFilter{}, IterOptions{PageSize: 5}) {
		if err != nil {
			t.Fatal(err)
		}
		seen++
		if seen == 3 {
			break
		}
	}
	equal(t, len(server.RequestsOf("messages.list")), 1)

	some := collect(t, client.IterMessages(
		ctx, MessageFilter{}, IterOptions{PageSize: 5, MaxItems: 12}))
	equal(t, len(some), 12)
	var limits []any
	for _, request := range server.RequestsOf("messages.list")[1:] {
		limits = append(limits, request["limit"])
	}
	equalLists(t, limits, []any{5.0, 5.0, 2.0})
}

func TestIterMessagesRefusesBadPages(t *testing.T) {
	server, client := setup(t)
	stuck := fakeserver.Obj("items", []any{}, "nextCursor", "same")
	server.AnswerNext("messages.list", stuck)
	server.AnswerNext("messages.list", stuck)
	var last error
	for _, err := range client.IterMessages(ctx, MessageFilter{}, IterOptions{}) {
		last = err
	}
	wantProtocolError(t, last, "cursor")

	server.AnswerNext("messages.list", fakeserver.Obj("nextCursor", nil))
	_, err := client.Messages(ctx, MessageFilter{}, PageOptions{})
	wantProtocolError(t, err, "no list of items")

	server.AnswerNext("messages.list",
		fakeserver.Obj("items", []any{}, "nextCursor", 7))
	_, err = client.Messages(ctx, MessageFilter{}, PageOptions{})
	wantProtocolError(t, err, "cursor")
}

func TestPagesMayBeShorterThanTheLimit(t *testing.T) {
	server, client := setup(t)
	server.AnswerNext("contacts.list", fakeserver.Obj(
		"items", []any{fakeserver.Obj("name", "One", "numbers", []any{})},
		"nextCursor", "1"))
	server.AnswerNext("contacts.list", fakeserver.Obj(
		"items", []any{}, "nextCursor", "2"))
	server.AnswerNext("contacts.list", fakeserver.Obj(
		"items", []any{fakeserver.Obj("name", "Two", "numbers", []any{})},
		"nextCursor", nil))
	contacts := collect(t, client.IterContacts(ctx, ContactFilter{}, IterOptions{}))
	equal(t, len(contacts), 2)
	equal(t, contacts[1].Name, "Two")
}

func TestListsRefuseWrongArguments(t *testing.T) {
	server, client := setup(t)
	_, err := client.Messages(ctx, MessageFilter{Direction: "sideways"}, PageOptions{})
	wantArgumentError(t, err)
	for _, limit := range []int{-1, 201} {
		_, err = client.Messages(ctx, MessageFilter{}, PageOptions{Limit: limit})
		wantArgumentError(t, err)
		_, err = client.Contacts(ctx, ContactFilter{}, PageOptions{Limit: limit})
		wantArgumentError(t, err)
	}
	first := func(items func(func(Message, error) bool)) error {
		for _, err := range items {
			return err
		}
		return nil
	}
	wantArgumentError(t, first(client.IterMessages(
		ctx, MessageFilter{Direction: "up"}, IterOptions{})))
	wantArgumentError(t, first(client.IterMessages(
		ctx, MessageFilter{}, IterOptions{PageSize: 500})))
	wantArgumentError(t, first(client.IterMessages(
		ctx, MessageFilter{}, IterOptions{MaxItems: -1})))
	// Nothing of this was sent.
	equalLists(t, server.Actions(), []string{"authenticate"})
}

func TestGetMessage(t *testing.T) {
	server, client := setup(t)
	stored := server.Messages()[3]
	message := must(client.GetMessage(ctx, stored.ID))(t)
	equal(t, *message.ID, stored.ID)
	equal(t, message.Body, "Anna 3")
	equal(t, message.Direction, "out")
	equal(t, message.SentTimestamp == nil, true)

	_, err := client.GetMessage(ctx, 1)
	failure := wantAPIError(t, err, "not_found")
	equal(t, failure.IsAuthentication(), false)
	if !strings.HasPrefix(err.Error(), "not_found: ") {
		t.Fatalf("got %q", err.Error())
	}
}

func TestSend(t *testing.T) {
	server, client := setup(t)
	result := must(client.Send(ctx, "+43 690 876-54-321", "All systems running.", nil))(t)
	equal(t, result.To, ben)
	equal(t, result.Parts, 1)
	equal(t, result.Status, "queued")
	equal(t, len(result.RequestID), 32)
	sent := server.Sent()
	equal(t, len(sent), 1)
	equal(t, sent[0], fakeserver.SentMessage{
		RequestID: result.RequestID, To: ben,
		Body: "All systems running.", Sim: 1,
	})
	// A new request id each time.
	second := must(client.Send(ctx, ben, "All systems running.", nil))(t)
	equal(t, second.RequestID != result.RequestID, true)
	equal(t, len(server.Sent()), 2)
}

func TestSendWithOwnRequestIDAndSim(t *testing.T) {
	server, client := setup(t)
	options := &SendOptions{Sim: Ptr(2), RequestID: "order-4711.shipped_1"}
	result := must(client.Send(ctx, ben, "Grüße", options))(t)
	equal(t, result.RequestID, "order-4711.shipped_1")
	equal(t, server.Sent()[0].Sim, 2)
	equal(t, server.Sent()[0].Body, "Grüße")

	// A repeat does not send again, also with the number written
	// differently; it tells the status of now.
	if !waitUntil(func() bool {
		return server.Outbox("order-4711.shipped_1").Status == "sent"
	}) {
		t.Fatal("the message was not sent")
	}
	again := must(client.Send(ctx, "0690 876 54 321", "Grüße", options))(t)
	equal(t, again.Status, "sent")
	equal(t, again.To, ben)
	equal(t, len(server.Sent()), 1)

	// The id of another message.
	_, err := client.Send(ctx, ben, "Another text", options)
	wantAPIError(t, err, "invalid_request")
	_, err = client.Send(ctx, anna, "Grüße", options)
	wantAPIError(t, err, "invalid_request")
	equal(t, len(server.Sent()), 1)
}

func TestSendGroup(t *testing.T) {
	server, client := setup(t)
	options := &SendOptions{Sim: Ptr(2), RequestID: "news-1"}
	result := must(client.SendGroup(
		ctx, []string{"+43 690 876-54-321", anna, ben}, "Hello all", options))(t)
	equal(t, result.RequestID, "news-1")
	equalLists(t, result.To, []string{ben, anna})
	equal(t, result.Parts, 1)
	equal(t, result.Status, "queued")
	equal(t, len(result.Messages), 2)
	equal(t, result.Messages[0].RequestID, "news-1.1")
	equal(t, result.Messages[0].To, ben)
	equal(t, result.Messages[1].RequestID, "news-1.2")
	equal(t, result.Messages[1].To, anna)
	equal(t, result.Messages[1].Status, "queued")
	equal(t, string(result.Messages[1].Raw) != "", true)
	equal(t, len(result.Failed()), 0)
	sent := server.Sent()
	equal(t, len(sent), 2)
	equal(t, sent[0], fakeserver.SentMessage{
		RequestID: "news-1.1", To: ben, Body: "Hello all", Sim: 2, Group: "news-1",
	})
	equal(t, sent[1].RequestID, "news-1.2")
	request := server.RequestsOf("messages.sendGroup")[0]
	equal(t, request["requestId"], any("news-1"))
	equal(t, request["sim"], any(2.0))
	equal(t, len(request["to"].([]any)), 3)
	entry := must(client.WaitForResult(ctx, "news-1.2", nil))(t)
	equal(t, entry.Status, "sent")
	equal(t, must(client.MessageStatus(ctx, "news-1.1"))(t).To, ben)
	_, err := client.MessageStatus(ctx, "news-1")
	wantAPIError(t, err, "not_found")

	// A random id without one; the members derive from it.
	random := must(client.SendGroup(ctx, []string{ben}, "one", nil))(t)
	equal(t, len(random.RequestID), 32)
	equal(t, random.Messages[0].RequestID, random.RequestID+".1")
}

func TestSendGroupRepeatsOnlyOnce(t *testing.T) {
	server, client := setup(t)
	options := &SendOptions{RequestID: "news-2"}
	first := must(client.SendGroup(ctx, []string{ben, anna}, "Same", options))(t)
	again := must(client.SendGroup(
		ctx, []string{"0690 876 54 321", anna}, "Same", options))(t)
	equal(t, again.RequestID, first.RequestID)
	equalLists(t, again.To, first.To)
	equal(t, len(server.Sent()), 2)
	must(client.WaitForResult(ctx, "news-2.1", nil))(t)
	later := must(client.SendGroup(ctx, []string{anna, ben}, "Same", options))(t)
	equal(t, later.Messages[0].Status == "sent" || later.Messages[0].Status == "delivered", true)
	for _, wrong := range []struct {
		to   []string
		body string
	}{{[]string{ben, anna}, "Other"}, {[]string{ben}, "Same"}} {
		_, err := client.SendGroup(ctx, wrong.to, wrong.body, options)
		wantAPIError(t, err, "invalid_request")
	}
	equal(t, len(server.Sent()), 2)
}

func TestSendGroupRefusesTheWholeGroup(t *testing.T) {
	server, client := setup(t)
	_, err := client.SendGroup(ctx, []string{ben, "abc"}, "Hello", nil)
	wantAPIError(t, err, "invalid_number")
	_, err = client.SendGroup(ctx, []string{ben, anna}, strings.Repeat("x", 1531), nil)
	wantAPIError(t, err, "message_too_long")
	_, err = client.SendGroup(
		ctx, []string{ben, anna, "+43123456789"}, strings.Repeat("x", 1000), nil)
	wantAPIError(t, err, "invalid_request")
	_, err = client.SendGroup(ctx, []string{ben}, "Hello", &SendOptions{Sim: Ptr(9)})
	wantAPIError(t, err, "sim_unavailable")
	equal(t, len(server.Sent()), 0)
}

func TestSendGroupGoesOnAfterARefusedMember(t *testing.T) {
	server, client := setup(t)
	server.RefuseNumbers(anna)
	options := &SendOptions{RequestID: "news-3"}
	result := must(client.SendGroup(ctx, []string{ben, anna}, "Hello", options))(t)
	equal(t, result.Status, "queued")
	equal(t, result.Messages[0].Status, "queued")
	equal(t, result.Messages[1].Status, "failed")
	equal(t, len(result.Failed()), 1)
	equal(t, result.Failed()[0].To, anna)
	equal(t, must(client.WaitForResult(ctx, "news-3.1", nil))(t).Status, "sent")
	equal(t, must(client.MessageStatus(ctx, "news-3.2"))(t).Status, "failed")
	server.RefuseNumbers(ben, anna)
	all := must(client.SendGroup(
		ctx, []string{ben, anna}, "Hello", &SendOptions{RequestID: "news-4"}))(t)
	equal(t, all.Status, "failed")
}

func TestSendGroupRefusesWrongArguments(t *testing.T) {
	server, client := setup(t)
	var many []string
	for index := 0; index < 21; index++ {
		many = append(many, fmt.Sprintf("+4369900000%02d", index))
	}
	for _, wrong := range []struct {
		to   []string
		body string
	}{
		{nil, "text"}, {[]string{}, "text"}, {[]string{ben, " "}, "text"},
		{[]string{ben}, ""}, {many, "text"},
	} {
		_, err := client.SendGroup(ctx, wrong.to, wrong.body, nil)
		wantArgumentError(t, err)
	}
	for _, id := range []string{"with space", strings.Repeat("x", 62)} {
		_, err := client.SendGroup(ctx, []string{ben}, "text", &SendOptions{RequestID: id})
		wantArgumentError(t, err)
	}
	equalLists(t, server.Actions(), []string{"authenticate"})
	equal(t, len(server.Sent()), 0)
}

func TestRepeatAfterAFailureDoesNotSendAgain(t *testing.T) {
	server, client := setup(t)
	server.FailNumbers("+43123456")
	options := &SendOptions{RequestID: "job-1"}
	must(client.Send(ctx, "+43123456", "Hello", options))(t)
	entry := must(client.WaitForResult(ctx, "job-1", nil))(t)
	equal(t, entry.Status, "failed")
	server.FailNumbers()
	again := must(client.Send(ctx, "+43123456", "Hello", options))(t)
	equal(t, again.Status, "failed")
	equal(t, len(server.Sent()), 1)
}

func TestSendRefusesWrongArguments(t *testing.T) {
	server, client := setup(t)
	for _, arguments := range [][2]string{{"", "text"}, {"  ", "text"}, {ben, ""}} {
		_, err := client.Send(ctx, arguments[0], arguments[1], nil)
		wantArgumentError(t, err)
	}
	for _, id := range []string{
		"with space", "ä", strings.Repeat("x", 65), "semi;colon",
	} {
		_, err := client.Send(ctx, ben, "text", &SendOptions{RequestID: id})
		wantArgumentError(t, err)
	}
	equalLists(t, server.Actions(), []string{"authenticate"})
	equal(t, len(server.Sent()), 0)
}

func TestSendErrors(t *testing.T) {
	server, client := setup(t)
	_, err := client.Send(ctx, "abc", "text", nil)
	wantAPIError(t, err, "invalid_number")
	_, err = client.Send(ctx, ben, strings.Repeat("x", 1531), nil)
	wantAPIError(t, err, "message_too_long")
	_, err = client.Send(ctx, ben, "text", &SendOptions{Sim: Ptr(9)})
	wantAPIError(t, err, "sim_unavailable")
	equal(t, len(server.Sent()), 0)
	// The longest message: 10 parts.
	result := must(client.Send(ctx, ben, strings.Repeat("x", 1530), nil))(t)
	equal(t, result.Parts, 10)
}

func TestErrorDetails(t *testing.T) {
	server, client := setup(t)
	server.FailNext("messages.send", "permission_missing",
		"The permission to send SMS is missing.", 0,
		fakeserver.Field{Name: "permission", Value: "sendSms"})
	_, err := client.Send(ctx, ben, "text", nil)
	failure := wantAPIError(t, err, "permission_missing")
	equal(t, failure.Permission(), "sendSms")
	equal(t, failure.Message, "The permission to send SMS is missing.")
	equal(t, failure.Details["permission"], any("sendSms"))
	_, named := failure.RetryAfter()
	equal(t, named, false)
	equal(t, err.Error(),
		"permission_missing: The permission to send SMS is missing.")

	server.FailNext("status", "quota_exceeded", "", 0)
	_, err = client.Status(ctx)
	failure = wantAPIError(t, err, "quota_exceeded")
	equal(t, failure.Permission(), "")
	equal(t, len(failure.Details), 0)
}

func TestMessageStatusAndWaitForResult(t *testing.T) {
	server := startServer(t, &fakeserver.Options{
		SendDelay: 150 * time.Millisecond,
	})
	client := connect(t, server, nil)
	result := must(client.Send(ctx, ben, "text", nil))(t)
	entry := must(client.MessageStatus(ctx, result.RequestID))(t)
	equal(t, entry.RequestID, result.RequestID)
	equal(t, entry.Status, "queued")
	equal(t, entry.PartsSent, 0)
	equal(t, entry.ErrorCode == nil, true)
	equal(t, entry.MessageID == nil, true)
	equal(t, entry.Source, "api")
	equal(t, *entry.Sim, 1)

	// By polling: the connection is not subscribed.
	entry = must(client.WaitForResult(ctx, result.RequestID, nil))(t)
	equal(t, entry.Status, "sent")
	equal(t, entry.PartsSent, 1)
	equal(t, *entry.MessageID > 0, true)
	equal(t, len(server.RequestsOf("messages.status")) >= 3, true)

	_, err := client.MessageStatus(ctx, "unknown")
	wantAPIError(t, err, "not_found")
}

func TestWaitForResultWakesUpOnTheEvent(t *testing.T) {
	server := startServer(t, &fakeserver.Options{
		SendDelay: 300 * time.Millisecond,
	})
	client := connect(t, server, nil)
	must(client.Subscribe(ctx, "sms.sent", "sms.failed"))(t)
	result := must(client.Send(ctx, ben, "text", nil))(t)
	started := time.Now()
	entry := must(client.WaitForResult(ctx, result.RequestID, &WaitOptions{
		PollInterval: 10 * time.Second,
	}))(t)
	equal(t, entry.Status, "sent")
	if time.Since(started) > 2*time.Second {
		t.Fatal("the event did not end the wait")
	}
	// The event stays in the queue.
	event := must(client.WaitForEvent(ctx, time.Second, nil, "sms.sent"))(t)
	equal(t, *event.Data.RequestID, result.RequestID)
}

func TestWaitForResultFailedDeliveredAndTimeout(t *testing.T) {
	server := startServer(t, &fakeserver.Options{
		SendDelay: 30 * time.Millisecond, DeliveryReports: true,
	})
	client := connect(t, server, nil)
	server.FailNumbers("+43123456")
	result := must(client.Send(ctx, "+43123456", "text", nil))(t)
	entry := must(client.WaitForResult(ctx, result.RequestID, nil))(t)
	equal(t, entry.Status, "failed")
	equal(t, *entry.ErrorCode, 1)
	equal(t, entry.MessageID == nil, true)

	result = must(client.Send(ctx, ben, "text", nil))(t)
	entry = must(client.WaitForResult(ctx, result.RequestID, &WaitOptions{
		Delivered: true, PollInterval: 20 * time.Millisecond,
	}))(t)
	equal(t, entry.Status, "delivered")

	_, err := client.WaitForResult(ctx, result.RequestID, &WaitOptions{
		PollInterval: -time.Second,
	})
	wantArgumentError(t, err)
}

func TestWaitForResultTimesOut(t *testing.T) {
	server := startServer(t, &fakeserver.Options{SendDelay: 30 * time.Second})
	client := connect(t, server, nil)
	result := must(client.Send(ctx, ben, "text", nil))(t)
	_, err := client.WaitForResult(ctx, result.RequestID, &WaitOptions{
		Timeout: 300 * time.Millisecond, PollInterval: 100 * time.Millisecond,
	})
	wantTimeout(t, err)
	if !strings.Contains(err.Error(), "No result") ||
		!strings.Contains(err.Error(), "queued") {
		t.Fatalf("got %q", err.Error())
	}
}

func TestDelete(t *testing.T) {
	server, client := setup(t)
	must(client.Subscribe(ctx, "sms.deleted"))(t)
	doomed := server.Messages()[24]
	result := must(client.DeleteMessage(ctx, doomed.ID))(t)
	equal(t, result.Deleted, 1)
	equal(t, result.Mode, "hidden")
	equal(t, len(server.Messages()), 28)
	event := must(client.WaitForEvent(ctx, 2*time.Second, nil, "sms.deleted"))(t)
	equal(t, *event.Data.ID, doomed.ID)
	equal(t, *event.Data.ConversationID, doomed.ConversationID)
	equal(t, *event.Data.Address, anna)
	equal(t, *event.Data.Mode, "hidden")

	_, err := client.DeleteMessage(ctx, doomed.ID)
	wantAPIError(t, err, "not_found")

	server.SetDeleteMode("provider")
	result = must(client.DeleteConversation(ctx, doomed.ConversationID))(t)
	equal(t, result.Deleted, 24)
	equal(t, result.Mode, "provider")
	equal(t, len(server.Messages()), 4)
	event = must(client.WaitForEvent(ctx, 2*time.Second, nil, "sms.deleted"))(t)
	equal(t, event.Data.ID == nil, true)
	_, err = client.DeleteConversation(ctx, doomed.ConversationID)
	wantAPIError(t, err, "not_found")
}

func TestMarkRead(t *testing.T) {
	server, client := setup(t)
	conversation := server.Messages()[0].ConversationID
	_, err := client.MarkRead(ctx, conversation)
	wantAPIError(t, err, "not_default_app")

	server.SetDeleteMode("provider")
	result := must(client.MarkRead(ctx, conversation))(t)
	equal(t, result.OK, true)
	for _, message := range server.Messages() {
		if message.ConversationID == conversation && !message.Read {
			t.Fatalf("message %d is unread", message.ID)
		}
	}
	_, err = client.MarkRead(ctx, 999)
	wantAPIError(t, err, "not_found")
}

func TestContacts(t *testing.T) {
	server, client := setup(t)
	page := must(client.Contacts(ctx, ContactFilter{}, PageOptions{}))(t)
	equal(t, len(page.Items), 2)
	equal(t, page.Items[0].Name, "Anna Example")
	equal(t, page.Items[0].Numbers[0].Number, anna)
	equal(t, *page.Items[0].Numbers[0].Label, "Mobile")
	found := must(client.Contacts(ctx, ContactFilter{Query: "ben"}, PageOptions{}))(t)
	equal(t, len(found.Items), 1)
	equal(t, found.Items[0].Name, "Ben Example")

	for index := 0; index < 5; index++ {
		server.AddContact("Person "+string(rune('A'+index)), "+4312345678", "Home")
	}
	first := must(client.Contacts(ctx, ContactFilter{}, PageOptions{Limit: 3}))(t)
	equal(t, first.NextCursor, "3")
	all := collect(t, client.IterContacts(ctx, ContactFilter{}, IterOptions{PageSize: 3}))
	equal(t, len(all), 7)
}

func TestGenericRequest(t *testing.T) {
	server, client := setup(t)
	raw := must(client.Request(ctx, "messages.list", map[string]any{
		"direction": "out", "limit": 2,
	}))(t)
	var page struct {
		Items []map[string]any `json:"items"`
	}
	if err := json.Unmarshal(raw, &page); err != nil {
		t.Fatal(err)
	}
	equal(t, len(page.Items), 2)
	equal(t, page.Items[0]["body"], any("Anna 23"))

	_, err := client.Request(ctx, "no.such.action", nil)
	wantAPIError(t, err, "unknown_action")
	raw = must(client.RequestOnce(ctx, "ping", nil))(t)
	equal(t, strings.HasPrefix(string(raw), `{"time":`), true)

	// The id of the request belongs to the client.
	_, err = client.Request(ctx, "ping", map[string]any{"id": "mine"})
	wantArgumentError(t, err)
	_, err = client.SendRequest(ctx, "ping", map[string]any{"id": "mine"})
	wantArgumentError(t, err)
	equal(t, count(server.Actions(), "ping"), 1)
}

func TestFramesAreCompactJSON(t *testing.T) {
	frame := must(encodeFrame("go-2", "messages.send", map[string]any{
		"to": ben, "body": "Grüße <&>", "requestId": "r1",
	}))(t)
	equal(t, string(frame), `{"id":"go-2","action":"messages.send",`+
		`"body":"Grüße <&>","requestId":"r1","to":"+4369087654321"}`)
	_, err := encodeFrame("go-1", "x", map[string]any{"bad": func() {}})
	wantArgumentError(t, err)
}

func TestFrameSizes(t *testing.T) {
	server, client := setup(t)
	// Answers may be larger than 64 KiB.
	server.AddMessage(fakeserver.NewMessage{
		Address: anna, Body: strings.Repeat("x", 100_000),
	})
	page := must(client.Messages(ctx, MessageFilter{}, PageOptions{Limit: 1}))(t)
	equal(t, len(page.Items[0].Body), 100_000)

	// Requests are not.
	_, err := client.Request(ctx, "messages.list", map[string]any{
		"q": strings.Repeat("x", MaxFrameBytes),
	})
	wantArgumentError(t, err)
	if !strings.Contains(err.Error(), "64 KiB") {
		t.Fatalf("got %q", err.Error())
	}
	equal(t, count(server.Actions(), "messages.list"), 1)
	equal(t, must(client.Ping(ctx))(t).Time > 0, true)
	equal(t, client.ConnectionCount(), 1)
}
