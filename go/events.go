package smsdock

import (
	"context"
	"encoding/json"
	"errors"
	"iter"
	"slices"
	"sort"
	"strings"
	"time"
)

// Subscribe asks for events and returns the subscription now in force.
//
// events is one name or several, or "*" for all; the names are added to
// the subscription. A limit to one number set before with
// SubscribeAddress is removed.
//
// In the result "*" is replaced by the names it stands for. The client
// remembers the subscription and restores it after a reconnect.
func (c *Client) Subscribe(
	ctx context.Context, events ...string,
) (*Subscription, error) {
	return c.subscribe(ctx, "subscribe", events, nil)
}

// SubscribeAddress is Subscribe with a limit: the sms.* events are
// delivered for one number only. The limit replaces the one set before.
// Numbers are compared as the phone does: +43 699 1234567 and 0699
// 1234567 are the same. Events that name no number always arrive.
func (c *Client) SubscribeAddress(
	ctx context.Context, address string, events ...string,
) (*Subscription, error) {
	return c.subscribe(ctx, "subscribe", events, &address)
}

// Unsubscribe stops events and returns the subscription now in force.
//
// events is one name or several, or "*" for all. When no name is left,
// the limit to one number is removed too.
func (c *Client) Unsubscribe(
	ctx context.Context, events ...string,
) (*Subscription, error) {
	return c.subscribe(ctx, "unsubscribe", events, nil)
}

func (c *Client) subscribe(
	ctx context.Context, action string, events []string, address *string,
) (*Subscription, error) {
	if err := checkEventNames(events); err != nil {
		return nil, err
	}
	fields := map[string]any{"events": events}
	if address != nil {
		fields["address"] = *address
	}
	raw, err := c.request(ctx, action, fields, true)
	if err != nil {
		return nil, err
	}
	var result struct {
		Events  *[]string       `json:"events"`
		Address json.RawMessage `json:"address"`
	}
	if json.Unmarshal(raw, &result) != nil || result.Events == nil {
		return nil, protocolError(
			"Malformed subscription: %s", strings.TrimSpace(string(raw)))
	}
	subscription := &Subscription{Events: *result.Events}
	var filter string
	if json.Unmarshal(result.Address, &filter) == nil &&
		len(result.Address) > 0 && result.Address[0] == '"' {
		subscription.Address = &filter
	}
	c.subscription = subscription
	return c.Subscription(), nil
}

// Events yields events as they arrive.
//
// Events that were queued while a request waited for its response come
// first. timeout is the longest time to wait for the next event; the
// iteration ends without an error when it passes. 0 waits without limit.
// Subscribe first, a new connection gets no events.
//
// After a lost connection the iterator reconnects and goes on. When that
// fails, or when ctx is done, it yields the error and ends.
func (c *Client) Events(
	ctx context.Context, timeout time.Duration,
) iter.Seq2[Event, error] {
	return func(yield func(Event, error) bool) {
		for {
			if len(c.events) > 0 {
				event := c.events[0]
				c.events = c.events[1:]
				if !yield(event, nil) {
					return
				}
				continue
			}
			var deadline time.Time
			if timeout > 0 {
				deadline = time.Now().Add(timeout)
			}
			event, err := c.readEvent(ctx, deadline, "waiting for events")
			if err != nil {
				yield(Event{}, err)
				return
			}
			if event == nil || !yield(*event, nil) {
				return
			}
		}
	}
}

// WaitForEvent returns the next event with one of these names; "*" or no
// name at all stands for every event.
//
// match, if not nil, is called with each candidate and must return true
// for the wanted one. Other events stay in the queue. A timeout of 0
// means Options.Timeout. The error is a [TimeoutError] when the time is
// over.
func (c *Client) WaitForEvent(
	ctx context.Context, timeout time.Duration, match func(Event) bool,
	names ...string,
) (Event, error) {
	if len(names) == 0 {
		names = []string{"*"}
	}
	if err := checkEventNames(names); err != nil {
		return Event{}, err
	}
	every := slices.Contains(names, "*")
	wanted := func(event Event) bool {
		if !every && !slices.Contains(names, event.Name) {
			return false
		}
		return match == nil || match(event)
	}
	for index, event := range c.events {
		if wanted(event) {
			c.events = slices.Delete(c.events, index, index+1)
			return event, nil
		}
	}
	deadline := time.Now().Add(orDefault(timeout, c.timeout))
	sorted := slices.Clone(names)
	sort.Strings(sorted)
	label := strings.Join(slices.Compact(sorted), ", ")
	for {
		event, err := c.readEvent(ctx, deadline, "waiting for "+label)
		if err != nil {
			return Event{}, err
		}
		if event == nil {
			return Event{}, &TimeoutError{
				Message: "No event " + label + " in time",
			}
		}
		if wanted(*event) {
			return *event, nil
		}
		c.queueEvent(*event)
	}
}

// readEvent reads from the connection until an event arrives.
//
// It returns the event without queueing it, or nil when the deadline
// passed (a zero deadline means without limit). It reconnects when
// needed.
func (c *Client) readEvent(
	ctx context.Context, deadline time.Time, what string,
) (*Event, error) {
	attempt := 0
	for {
		if c.link == nil {
			err := c.open(ctx)
			if failure, isTransport := asTransport(err); isTransport {
				if err := c.recoverFrom(ctx, what, failure, attempt); err != nil {
					return nil, err
				}
				attempt++
				continue
			}
			if err != nil {
				return nil, err
			}
		}
		if !deadline.IsZero() && !time.Now().Before(deadline) {
			return nil, nil
		}
		message, err := c.receive(ctx, deadline)
		if errors.Is(err, errReceiveTimeout) {
			return nil, nil
		}
		if failure, isTransport := asTransport(err); isTransport {
			if err := c.recoverFrom(ctx, what, failure, attempt); err != nil {
				return nil, err
			}
			attempt++
			continue
		}
		if err != nil {
			return nil, err
		}
		attempt = 0
		if message.isEvent() {
			event := decodeEvent(message)
			return &event, nil
		}
		c.fileResponse(message)
	}
}

// waitForOutcomeEvent returns at the deadline, or earlier when the
// outcome of the message arrives as an event.
func (c *Client) waitForOutcomeEvent(
	ctx context.Context, requestID string, deadline time.Time,
) error {
	for {
		event, err := c.readEvent(ctx, deadline, "waiting for the result")
		if err != nil || event == nil {
			return err
		}
		c.queueEvent(*event)
		switch event.Name {
		case "sms.sent", "sms.delivered", "sms.failed":
			if id := event.Data.RequestID; id != nil && *id == requestID {
				return nil
			}
		}
	}
}

// decodeEvent reads an event frame. Fields of an unexpected form are left
// empty; Raw always holds the frame.
func decodeEvent(message *frame) Event {
	event := Event{Raw: message.raw}
	json.Unmarshal(message.fields["event"], &event.Name)
	if sequence, isInteger := integer(message.fields["seq"]); isInteger {
		event.Seq = sequence
	}
	data := message.fields["data"]
	if !isObject(data) {
		return event
	}
	// A field of an unexpected type is skipped; the others are read.
	json.Unmarshal(data, &event.Data)
	event.Data.Raw = data
	if event.Data.Message != nil {
		var fields struct {
			Message json.RawMessage `json:"message"`
		}
		json.Unmarshal(data, &fields)
		event.Data.Message.Raw = fields.Message
	}
	return event
}

func checkEventNames(names []string) error {
	if len(names) == 0 || slices.Contains(names, "") {
		return argumentError("events must be one event name or several")
	}
	return nil
}
