package smsdock

import (
	"encoding/json"
	"time"
)

// The types in this file mirror the JSON objects of API.md. All
// timestamps are milliseconds since the Unix epoch. Fields that the
// server sends as null are pointers and nil then.
//
// The types that are results or items carry the field Raw: the object
// exactly as the server sent it, with all fields, also those this package
// does not know.

// Message is one SMS.
//
// ID is nil only inside an sms.received event whose stored copy was not
// found in time. Direction is "in" or "out". Status is one of received,
// queued, sending, sent, delivered and failed. SentTimestamp is set for
// incoming messages only. RequestID is set for messages sent through SMS
// Dock.
type Message struct {
	ID             *int64  `json:"id"`
	ConversationID *int64  `json:"conversationId"`
	Address        string  `json:"address"`
	Contact        *string `json:"contact"`
	Direction      string  `json:"direction"`
	Body           string  `json:"body"`
	Timestamp      int64   `json:"timestamp"`
	SentTimestamp  *int64  `json:"sentTimestamp"`
	Read           bool    `json:"read"`
	Status         string  `json:"status"`
	Sim            *int    `json:"sim"`
	RequestID      *string `json:"requestId"`

	// Raw is the object as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// Time returns Timestamp as a time in the local time zone.
func (m *Message) Time() time.Time { return time.UnixMilli(m.Timestamp) }

// Sender returns the name of the contact, or the address without one.
func (m *Message) Sender() string {
	if m.Contact != nil && *m.Contact != "" {
		return *m.Contact
	}
	return m.Address
}

// Conversation is all messages exchanged with one phone number.
//
// Snippet is the newest message cut to 160 characters; Timestamp,
// Direction and Sim describe that newest message.
type Conversation struct {
	ID           int64   `json:"id"`
	Address      string  `json:"address"`
	Contact      *string `json:"contact"`
	Snippet      string  `json:"snippet"`
	Timestamp    int64   `json:"timestamp"`
	Direction    string  `json:"direction"`
	MessageCount int     `json:"messageCount"`
	UnreadCount  int     `json:"unreadCount"`
	Sim          *int    `json:"sim"`

	// Raw is the object as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// ContactNumber is one phone number of a contact.
type ContactNumber struct {
	Number string  `json:"number"`
	Label  *string `json:"label"`
}

// Contact is one contact of the phone that has at least one number.
type Contact struct {
	Name    string          `json:"name"`
	Numbers []ContactNumber `json:"numbers"`

	// Raw is the object as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// Sim is one SIM. SubscriptionID is the value used as sim elsewhere.
type Sim struct {
	SubscriptionID int     `json:"subscriptionId"`
	Slot           int     `json:"slot"`
	Name           string  `json:"name"`
	Number         *string `json:"number"`
	IsDefault      bool    `json:"isDefault"`
}

// AppInfo is name and version of the app and the protocol version it
// speaks.
type AppInfo struct {
	Name            string `json:"name"`
	Version         string `json:"version"`
	ProtocolVersion int    `json:"protocolVersion"`
}

// Licence is the licence state. Remaining is nil when sending is
// unlimited.
type Licence struct {
	Enforced   bool `json:"enforced"`
	Licensed   bool `json:"licensed"`
	DailyLimit int  `json:"dailyLimit"`
	SentToday  int  `json:"sentToday"`
	Remaining  *int `json:"remaining"`
}

// Status is the result of the action status.
//
// DeleteMode is "hidden" or "provider". Permissions maps the names
// readSms, sendSms, receiveSms, contacts, phoneState and notifications to
// booleans.
type Status struct {
	App           AppInfo         `json:"app"`
	Sims          []Sim           `json:"sims"`
	Permissions   map[string]bool `json:"permissions"`
	DefaultSmsApp bool            `json:"defaultSmsApp"`
	DeleteMode    string          `json:"deleteMode"`
	Licence       Licence         `json:"licence"`
	Popup         bool            `json:"popup"`

	// Raw is the object as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// SendResult is the result of messages.send.
//
// To is the number as it is sent, without the ignored characters. Status
// is "queued" for a new message; a repeated request returns the current
// status, "failed" included.
type SendResult struct {
	RequestID string `json:"requestId"`
	To        string `json:"to"`
	Parts     int    `json:"parts"`
	Status    string `json:"status"`

	// Raw is the object as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// GroupSendResult is the result of messages.sendGroup: one SMS per
// recipient.
//
// Messages holds one SendResult per recipient in the order of To, with the
// request ids "<RequestID>.1", "<RequestID>.2" and so on. Status is the
// least advanced status of the members, or "failed" when every member
// failed.
type GroupSendResult struct {
	RequestID string       `json:"requestId"`
	To        []string     `json:"to"`
	Parts     int          `json:"parts"`
	Status    string       `json:"status"`
	Messages  []SendResult `json:"messages"`

	// Raw is the object as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// Failed returns the members the phone refused.
func (r *GroupSendResult) Failed() []SendResult {
	var failed []SendResult
	for _, member := range r.Messages {
		if member.Status == "failed" {
			failed = append(failed, member)
		}
	}
	return failed
}

// OutboxEntry is the state of a message sent through SMS Dock.
//
// Status is queued (handed to the phone), sending (some parts are left),
// sent, delivered or failed. Source tells who sent: web, api or app.
type OutboxEntry struct {
	RequestID string `json:"requestId"`
	To        string `json:"to"`
	Parts     int    `json:"parts"`
	PartsSent int    `json:"partsSent"`
	Status    string `json:"status"`
	ErrorCode *int   `json:"errorCode"`
	MessageID *int64 `json:"messageId"`
	Sim       *int   `json:"sim"`
	Source    string `json:"source"`
	CreatedAt int64  `json:"createdAt"`

	// Raw is the object as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// DeleteResult is the result of a delete. Mode is "hidden" or "provider".
type DeleteResult struct {
	Deleted int    `json:"deleted"`
	Mode    string `json:"mode"`

	// Raw is the object as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// MarkReadResult is the result of conversations.markRead: {"ok": true}.
type MarkReadResult struct {
	OK bool `json:"ok"`

	// Raw is the object as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// Subscription is the subscription in force after subscribe or
// unsubscribe.
//
// Events holds event names; "*" is replaced by the names it stands for.
// Address is the number the sms.* events are limited to, or nil.
type Subscription struct {
	Events  []string `json:"events"`
	Address *string  `json:"address"`
}

// PingResult is the result of ping: the time of the phone.
type PingResult struct {
	Time int64 `json:"time"`
}

// Page is one page of a list. NextCursor is "" on the last page; pass it
// as cursor to get the next page.
type Page[T any] struct {
	Items      []T
	NextCursor string

	// Raw is the object as the server sent it.
	Raw json.RawMessage
}

// Event is an event: {"event": name, "seq": number, "data": {…}}.
//
// Seq counts all events of the phone and restarts at 1 when the app is
// restarted.
type Event struct {
	Name string    `json:"event"`
	Seq  int64     `json:"seq"`
	Data EventData `json:"data"`

	// Raw is the event as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// EventData holds the fields of the data of all events. Which ones are
// set depends on the event; see "Events" in API.md:
//
//	sms.received           Message
//	sms.queued             RequestID, To, Parts, Sim, Source
//	sms.sent               RequestID, ConversationID, To, Message
//	sms.delivered          RequestID, ID, ConversationID, To
//	sms.failed             RequestID, ID, ConversationID, To, ErrorCode, Reason
//	sms.deleted            ID, ConversationID, Address, Mode
//	conversations.changed  nothing
//
// ID and ConversationID of sms.sent, sms.delivered and sms.failed are nil
// as long as the phone has not stored the message.
type EventData struct {
	Message        *Message `json:"message,omitempty"`
	RequestID      *string  `json:"requestId,omitempty"`
	ID             *int64   `json:"id,omitempty"`
	ConversationID *int64   `json:"conversationId,omitempty"`
	To             *string  `json:"to,omitempty"`
	Address        *string  `json:"address,omitempty"`
	Parts          *int     `json:"parts,omitempty"`
	Sim            *int     `json:"sim,omitempty"`
	Source         *string  `json:"source,omitempty"`
	ErrorCode      *int     `json:"errorCode,omitempty"`
	Reason         *string  `json:"reason,omitempty"`
	Mode           *string  `json:"mode,omitempty"`

	// Raw is the data as the server sent it.
	Raw json.RawMessage `json:"-"`
}

// Ptr returns a pointer to value. It is a help for optional fields such
// as MessageFilter.Sim.
func Ptr[T any](value T) *T { return &value }

// rawSetter is implemented by the types that keep the object of the
// server.
type rawSetter interface {
	setRaw(raw json.RawMessage)
}

func (m *Message) setRaw(raw json.RawMessage)      { m.Raw = raw }
func (c *Conversation) setRaw(raw json.RawMessage) { c.Raw = raw }
func (c *Contact) setRaw(raw json.RawMessage)      { c.Raw = raw }
func (s *Status) setRaw(raw json.RawMessage)       { s.Raw = raw }
func (r *SendResult) setRaw(raw json.RawMessage)   { r.Raw = raw }
func (r *GroupSendResult) setRaw(raw json.RawMessage) {
	r.Raw = raw
	var fields struct {
		Messages []json.RawMessage `json:"messages"`
	}
	json.Unmarshal(raw, &fields)
	for index := range r.Messages {
		if index < len(fields.Messages) {
			r.Messages[index].Raw = fields.Messages[index]
		}
	}
}
func (e *OutboxEntry) setRaw(raw json.RawMessage)    { e.Raw = raw }
func (r *DeleteResult) setRaw(raw json.RawMessage)   { r.Raw = raw }
func (r *MarkReadResult) setRaw(raw json.RawMessage) { r.Raw = raw }

// decode reads a result of the server into a typed value.
func decode(raw json.RawMessage, target any, label string) error {
	if err := json.Unmarshal(raw, target); err != nil {
		return protocolError("The %s has an unexpected form: %v", label, err)
	}
	if setter, keeps := target.(rawSetter); keeps {
		setter.setRaw(raw)
	}
	return nil
}
