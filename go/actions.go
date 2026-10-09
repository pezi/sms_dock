package smsdock

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"iter"
	"regexp"
	"strings"
	"time"
)

var requestIDPattern = regexp.MustCompile(`^[A-Za-z0-9._-]{1,64}$`)

const (
	defaultPageSize = 50
	// firstPoll is the pause before WaitForResult asks a second time.
	firstPoll = 100 * time.Millisecond
)

// PageOptions select one page of a list.
type PageOptions struct {
	// Limit is the page size, 1 to 200. Default: 50.
	Limit int

	// Cursor is the NextCursor of the previous page; "" for the first
	// page.
	Cursor string
}

// IterOptions steer an iterator over all pages of a list.
type IterOptions struct {
	// PageSize is the limit of each request, 1 to 200. Default: 50.
	PageSize int

	// MaxItems ends the iteration after that many items; 0 yields all of
	// them.
	MaxItems int
}

// ConversationFilter selects conversations. The zero value selects all.
type ConversationFilter struct {
	// Query searches text, numbers and contact names.
	Query string

	// Unread keeps only conversations with unread messages.
	Unread bool

	// Sim keeps only conversations of this subscription id.
	Sim *int
}

// MessageFilter selects messages. The zero value selects all.
type MessageFilter struct {
	// Address keeps messages from or to one number. Numbers are compared
	// as the phone does: +43 699 1234567 and 0699 1234567 are the same.
	Address string

	// ConversationID keeps the messages of one conversation. It wins if
	// Address is given too.
	ConversationID *int64

	// Direction is "in", "out" or "all"; "" means "all".
	Direction string

	// Query searches the text.
	Query string

	// Since and Until limit the time, both inclusive. They are sent as
	// the fields from and to, in milliseconds since the epoch. The zero
	// time means no limit; use time.UnixMilli for a timestamp of the API.
	Since time.Time
	Until time.Time

	// Sim keeps only messages of this subscription id.
	Sim *int

	// Unread keeps only unread messages.
	Unread bool
}

// ContactFilter selects contacts. The zero value selects all.
type ContactFilter struct {
	// Query searches names and numbers.
	Query string
}

// SendOptions are the optional fields of Send.
type SendOptions struct {
	// Sim is the subscription id of the SIM; nil means the SMS SIM of
	// the phone.
	Sim *int

	// RequestID is your own id, 1 to 64 characters of A-Z a-z 0-9 . _ -.
	// The phone sends a message with the same id only once. Without it
	// the client creates a random one.
	RequestID string
}

// WaitOptions steer WaitForResult.
type WaitOptions struct {
	// Timeout is the longest time to wait. Default: Options.Timeout.
	Timeout time.Duration

	// Delivered waits for the delivery report: the status sent is not
	// enough then. Some networks never send a delivery report.
	Delivered bool

	// PollInterval is the longest pause between two questions to the
	// server. Default: 1 second.
	PollInterval time.Duration
}

// Status returns phone, SIMs, permissions, licence and limits.
func (c *Client) Status(ctx context.Context) (*Status, error) {
	return call[Status](ctx, c, "status", nil)
}

// Ping checks the connection and returns the time of the phone.
func (c *Client) Ping(ctx context.Context) (*PingResult, error) {
	return call[PingResult](ctx, c, "ping", nil)
}

// Conversations returns one page of conversations, newest first.
func (c *Client) Conversations(
	ctx context.Context, filter ConversationFilter, page PageOptions,
) (*Page[Conversation], error) {
	return listPage[Conversation](
		ctx, c, "conversations.list", filter.fields(), page)
}

// IterConversations yields conversations, newest first, page after page.
// The iteration ends after the first error.
func (c *Client) IterConversations(
	ctx context.Context, filter ConversationFilter, options IterOptions,
) iter.Seq2[Conversation, error] {
	return iterate[Conversation](
		ctx, c, "conversations.list", filter.fields(), nil, options)
}

// Messages returns one page of messages, newest first.
func (c *Client) Messages(
	ctx context.Context, filter MessageFilter, page PageOptions,
) (*Page[Message], error) {
	fields, err := filter.fields()
	if err != nil {
		return nil, err
	}
	return listPage[Message](ctx, c, "messages.list", fields, page)
}

// IterMessages yields messages, newest first, following the cursor from
// page to page. The iteration ends after the first error; wrong
// arguments are reported as the first item.
func (c *Client) IterMessages(
	ctx context.Context, filter MessageFilter, options IterOptions,
) iter.Seq2[Message, error] {
	fields, err := filter.fields()
	return iterate[Message](ctx, c, "messages.list", fields, err, options)
}

// GetMessage returns one message. The error has the code not_found if it
// does not exist.
func (c *Client) GetMessage(
	ctx context.Context, messageID int64,
) (*Message, error) {
	return call[Message](
		ctx, c, "messages.get", map[string]any{"messageId": messageID})
}

// Send sends an SMS. options may be nil.
//
// to is the phone number, body the text (at most 10 SMS parts). The
// message is on its way when this method returns; use WaitForResult for
// the outcome.
//
// The request id makes the request safe to repeat: the phone sends a
// message with the same id only once and remembers the id for 30 days. A
// repeat returns the result of the first request with the current
// status, which may be "sent" already. A repeat never sends again, also
// not after the first attempt failed: the status is "failed" then, and a
// new attempt needs a new id. A repeat with another recipient or text
// fails with invalid_request.
//
// Without SendOptions.RequestID the client creates a random one. If the
// connection drops before the response arrives, the request is repeated
// with the same id.
//
// In the result To is the number as it is sent, without spaces and other
// ignored characters.
func (c *Client) Send(
	ctx context.Context, to, body string, options *SendOptions,
) (*SendResult, error) {
	var settings SendOptions
	if options != nil {
		settings = *options
	}
	if strings.TrimSpace(to) == "" {
		return nil, argumentError("'to' must be a phone number")
	}
	if body == "" {
		return nil, argumentError("'body' must be a text")
	}
	requestID := settings.RequestID
	if requestID == "" {
		random := make([]byte, 16)
		if _, err := rand.Read(random); err != nil {
			return nil, err
		}
		requestID = hex.EncodeToString(random)
	} else if !requestIDPattern.MatchString(requestID) {
		return nil, argumentError(
			"The request id must be 1 to 64 characters of A-Z a-z 0-9 . _ -")
	}
	fields := map[string]any{"to": to, "body": body, "requestId": requestID}
	if settings.Sim != nil {
		fields["sim"] = *settings.Sim
	}
	return call[SendResult](ctx, c, "messages.send", fields)
}

// MessageStatus returns the outbox entry of a message sent through SMS
// Dock.
func (c *Client) MessageStatus(
	ctx context.Context, requestID string,
) (*OutboxEntry, error) {
	return call[OutboxEntry](
		ctx, c, "messages.status", map[string]any{"requestId": requestID})
}

// WaitForResult waits until a sent message succeeded or failed. options
// may be nil.
//
// It returns the outbox entry once its status is sent, delivered or
// failed. Check Status and ErrorCode of the result; a failed message is
// no error.
//
// The method asks the server with messages.status: soon after the start,
// then less often, at last every PollInterval. If the connection is
// subscribed to sms.sent, sms.delivered and sms.failed it asks as soon as
// such an event arrives. The events stay in the queue. The error is a
// [TimeoutError] when the time is over.
func (c *Client) WaitForResult(
	ctx context.Context, requestID string, options *WaitOptions,
) (*OutboxEntry, error) {
	var settings WaitOptions
	if options != nil {
		settings = *options
	}
	if settings.PollInterval < 0 || settings.Timeout < 0 {
		return nil, argumentError("PollInterval and Timeout must be positive")
	}
	interval := orDefault(settings.PollInterval, time.Second)
	deadline := time.Now().Add(orDefault(settings.Timeout, c.timeout))
	pause := min(firstPoll, interval)
	for {
		entry, err := c.MessageStatus(ctx, requestID)
		if err != nil {
			return nil, err
		}
		switch entry.Status {
		case "delivered", "failed":
			return entry, nil
		case "sent":
			if !settings.Delivered {
				return entry, nil
			}
		}
		remaining := time.Until(deadline)
		if remaining <= 0 {
			return nil, &TimeoutError{Message: "No result for the message '" +
				requestID + "' in time; its status is '" + entry.Status + "'"}
		}
		wake := time.Now().Add(min(pause, remaining))
		if err := c.waitForOutcomeEvent(ctx, requestID, wake); err != nil {
			return nil, err
		}
		pause = min(pause*2, interval)
	}
}

// DeleteMessage deletes one message.
//
// With the mode "hidden" the message disappears from SMS Dock but stays
// on the phone. If the connection drops after the server deleted the
// message, the repeated request fails with not_found.
func (c *Client) DeleteMessage(
	ctx context.Context, messageID int64,
) (*DeleteResult, error) {
	return call[DeleteResult](
		ctx, c, "messages.delete", map[string]any{"messageId": messageID})
}

// DeleteConversation deletes a conversation. Deleted is the number of
// messages.
func (c *Client) DeleteConversation(
	ctx context.Context, conversationID int64,
) (*DeleteResult, error) {
	return call[DeleteResult](ctx, c, "conversations.delete",
		map[string]any{"conversationId": conversationID})
}

// MarkRead marks all messages of a conversation as read.
//
// Android lets only the default SMS app change messages; otherwise the
// error has the code not_default_app.
func (c *Client) MarkRead(
	ctx context.Context, conversationID int64,
) (*MarkReadResult, error) {
	return call[MarkReadResult](ctx, c, "conversations.markRead",
		map[string]any{"conversationId": conversationID})
}

// Contacts returns one page of contacts, sorted by name.
func (c *Client) Contacts(
	ctx context.Context, filter ContactFilter, page PageOptions,
) (*Page[Contact], error) {
	return listPage[Contact](ctx, c, "contacts.list", filter.fields(), page)
}

// IterContacts yields contacts, sorted by name, page after page. The
// iteration ends after the first error.
func (c *Client) IterContacts(
	ctx context.Context, filter ContactFilter, options IterOptions,
) iter.Seq2[Contact, error] {
	return iterate[Contact](
		ctx, c, "contacts.list", filter.fields(), nil, options)
}

// ---------------------------------------------------------------------
// Internals
// ---------------------------------------------------------------------

func (f ConversationFilter) fields() map[string]any {
	fields := make(map[string]any)
	if f.Query != "" {
		fields["q"] = f.Query
	}
	if f.Unread {
		fields["unread"] = true
	}
	if f.Sim != nil {
		fields["sim"] = *f.Sim
	}
	return fields
}

func (f MessageFilter) fields() (map[string]any, error) {
	switch f.Direction {
	case "", "in", "out", "all":
	default:
		return nil, argumentError("Direction must be 'in', 'out' or 'all'")
	}
	fields := make(map[string]any)
	if f.Address != "" {
		fields["address"] = f.Address
	}
	if f.ConversationID != nil {
		fields["conversationId"] = *f.ConversationID
	}
	if f.Direction != "" && f.Direction != "all" {
		fields["direction"] = f.Direction
	}
	if f.Query != "" {
		fields["q"] = f.Query
	}
	if !f.Since.IsZero() {
		fields["from"] = f.Since.UnixMilli()
	}
	if !f.Until.IsZero() {
		fields["to"] = f.Until.UnixMilli()
	}
	if f.Sim != nil {
		fields["sim"] = *f.Sim
	}
	if f.Unread {
		fields["unread"] = true
	}
	return fields, nil
}

func (f ContactFilter) fields() map[string]any {
	fields := make(map[string]any)
	if f.Query != "" {
		fields["q"] = f.Query
	}
	return fields
}

// call performs an action and reads its result into a T.
func call[T any](
	ctx context.Context, c *Client, action string, fields map[string]any,
) (*T, error) {
	raw, err := c.request(ctx, action, fields, true)
	if err != nil {
		return nil, err
	}
	result := new(T)
	if err := decode(raw, result, "result of '"+action+"'"); err != nil {
		return nil, err
	}
	return result, nil
}

// withPaging adds limit and cursor to the fields of a list action.
func withPaging(
	fields map[string]any, limit int, cursor string,
) (map[string]any, error) {
	limit = orDefault(limit, defaultPageSize)
	if limit < 1 || limit > MaxPageSize {
		return nil, argumentError(
			"The limit must be between 1 and %d", MaxPageSize)
	}
	paged := make(map[string]any, len(fields)+2)
	for name, value := range fields {
		paged[name] = value
	}
	paged["limit"] = limit
	if cursor != "" {
		paged["cursor"] = cursor
	}
	return paged, nil
}

// rawPage is a page before its items are read.
type rawPage struct {
	Items      *[]json.RawMessage `json:"items"`
	NextCursor json.RawMessage    `json:"nextCursor"`
}

// listPage requests one page and reads its items into values of T.
func listPage[T any](
	ctx context.Context, c *Client, action string, fields map[string]any,
	options PageOptions,
) (*Page[T], error) {
	paged, err := withPaging(fields, options.Limit, options.Cursor)
	if err != nil {
		return nil, err
	}
	raw, err := c.request(ctx, action, paged, true)
	if err != nil {
		return nil, err
	}
	var parsed rawPage
	if json.Unmarshal(raw, &parsed) != nil || parsed.Items == nil {
		return nil, protocolError("%s returned no list of items", action)
	}
	page := &Page[T]{Raw: raw, Items: make([]T, len(*parsed.Items))}
	for index, item := range *parsed.Items {
		label := "item of '" + action + "'"
		if err := decode(item, &page.Items[index], label); err != nil {
			return nil, err
		}
	}
	cursor := strings.TrimSpace(string(parsed.NextCursor))
	if cursor != "" && cursor != "null" {
		if json.Unmarshal(parsed.NextCursor, &page.NextCursor) != nil ||
			page.NextCursor == "" {
			return nil, protocolError(
				"%s returned the cursor %s", action, cursor)
		}
	}
	return page, nil
}

// iterate yields the items of all pages of a list. invalid is an error
// found in the arguments; it is reported as the first item.
func iterate[T any](
	ctx context.Context, c *Client, action string, fields map[string]any,
	invalid error, options IterOptions,
) iter.Seq2[T, error] {
	pageSize := orDefault(options.PageSize, defaultPageSize)
	maxItems := options.MaxItems
	if invalid == nil && maxItems < 0 {
		invalid = argumentError("MaxItems cannot be negative")
	}
	if invalid == nil {
		_, invalid = withPaging(nil, pageSize, "")
	}
	return func(yield func(T, error) bool) {
		var zero T
		if invalid != nil {
			yield(zero, invalid)
			return
		}
		cursor := ""
		count := 0
		for {
			limit := pageSize
			if maxItems > 0 {
				limit = min(pageSize, maxItems-count)
			}
			page, err := listPage[T](
				ctx, c, action, fields,
				PageOptions{Limit: limit, Cursor: cursor})
			if err != nil {
				yield(zero, err)
				return
			}
			for _, item := range page.Items {
				if !yield(item, nil) {
					return
				}
				count++
				if maxItems > 0 && count >= maxItems {
					return
				}
			}
			if page.NextCursor == "" {
				return
			}
			if page.NextCursor == cursor {
				yield(zero, protocolError(
					"%s returned the cursor \"%s\"", action, cursor))
				return
			}
			cursor = page.NextCursor
		}
	}
}
