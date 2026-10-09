package smsdock

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"os"
	"sort"
	"strconv"
	"strings"
	"time"
)

const (
	// APIProtocolVersion is the protocol version this client speaks.
	APIProtocolVersion = 1

	// DefaultAPIPort is the default port of the WebSocket API.
	DefaultAPIPort = 9443

	// MaxFrameBytes is the largest frame the server accepts from a
	// client.
	MaxFrameBytes = 64 * 1024

	// MaxIncomingBytes is the largest frame the client accepts. Answers
	// may exceed 64 KiB.
	MaxIncomingBytes = 4 * 1024 * 1024

	// MaxPageSize is the largest value of limit.
	MaxPageSize = 200

	// PasswordEnv is the environment variable that holds the password.
	PasswordEnv = "SMSDOCK_PASSWORD"

	// FingerprintEnv is the environment variable that holds the
	// certificate fingerprint.
	FingerprintEnv = "SMSDOCK_FINGERPRINT"
)

// Values of Options.ReconnectAttempts and Options.RateLimitRetries that
// stand for something other than a number.
const (
	// ReconnectForever reconnects without limit.
	ReconnectForever = -1

	// NoReconnect never reconnects.
	NoReconnect = -2

	// NoRateLimitRetries never repeats a request that was answered with
	// rate_limited.
	NoRateLimitRetries = -1
)

// EventNames lists all event names of protocol version 1.
var EventNames = []string{
	"sms.received",
	"sms.queued",
	"sms.sent",
	"sms.delivered",
	"sms.failed",
	"sms.deleted",
	"conversations.changed",
}

const (
	defaultTimeout           = 30 * time.Second
	defaultConnectTimeout    = 10 * time.Second
	defaultReconnectAttempts = 5
	defaultReconnectDelay    = time.Second
	defaultMaxReconnectDelay = 10 * time.Second
	defaultEventQueueSize    = 1000
	defaultRateLimitRetries  = 2
	maxBackoffExponent       = 16
)

// Options are the settings of a [Client]. The zero value of every field
// selects its default.
type Options struct {
	// Password is the password set in the app. Without it the
	// environment variable SMSDOCK_PASSWORD is read.
	Password string

	// Fingerprint is the SHA-256 fingerprint of the certificate as shown
	// on the Server screen of the app: hex, with or without colons, in
	// any case. Without it the environment variable SMSDOCK_FINGERPRINT
	// is read. The client compares it with the certificate of the server
	// during the TLS handshake and sends nothing if they differ.
	Fingerprint string

	// Insecure skips this comparison and prints a warning to standard
	// error. Use it only for tests.
	Insecure bool

	// Timeout is the time to wait for a response. Default: 30 seconds.
	Timeout time.Duration

	// ConnectTimeout is the time to open the connection. Default: 10
	// seconds.
	ConnectTimeout time.Duration

	// ReconnectAttempts is the number of reconnect attempts after a lost
	// connection. Default: 5. ReconnectForever means without limit,
	// NoReconnect none.
	ReconnectAttempts int

	// ReconnectDelay is the time before the first attempt; it is doubled
	// for each further one. Default: 1 second.
	ReconnectDelay time.Duration

	// MaxReconnectDelay is the upper limit of that delay. Default: 10
	// seconds.
	MaxReconnectDelay time.Duration

	// EventQueueSize is the number of events kept while nobody reads
	// them; after that the oldest are dropped and counted in
	// DroppedEvents. Default: 1000.
	EventQueueSize int

	// RateLimitRetries tells how often a request is repeated when the
	// server answers rate_limited and names the seconds to wait.
	// Default: 2. NoRateLimitRetries means never.
	RateLimitRetries int

	// OnReconnect is called with the client after every reconnect, once
	// the subscription is restored. Use it to read what was missed, for
	// example with IterMessages. It can also be set later with
	// SetOnReconnect.
	OnReconnect func(*Client)
}

// Client is one authenticated connection to SMS Dock.
//
// After a lost connection the client reconnects, logs in again and
// restores the subscription. Events that happened while the connection
// was down are lost; MissedEvents and ServerRestarted tell whether there
// were any.
//
// A Client is not safe for concurrent use: its methods must not be called
// from several goroutines at once. Use one Client per goroutine, or guard
// it with a mutex.
type Client struct {
	url               string
	password          string
	fingerprint       []byte
	insecure          bool
	timeout           time.Duration
	connectTimeout    time.Duration
	reconnectAttempts int
	reconnectDelay    time.Duration
	maxReconnectDelay time.Duration
	eventQueueSize    int
	rateLimitRetries  int
	onReconnect       func(*Client)

	link            *connection
	serverInfo      *AppInfo
	events          []Event
	droppedEvents   int
	connectionCount int
	requestNumber   int
	outstanding     map[string]struct{}
	responses       map[string]*frame
	subscription    *Subscription
	warned          bool
	loggedIn        bool
	epoch           int64
	latestSeq       int64
	restarted       bool
	missed          int64
	missedKnown     bool

	// sleep waits between attempts; tests replace it.
	sleep func(ctx context.Context, duration time.Duration) error
}

// frame is one decoded frame of the server.
type frame struct {
	raw    []byte
	fields map[string]json.RawMessage
}

func (f *frame) isEvent() bool {
	_, present := f.fields["event"]
	return present
}

// New returns a client for the wss:// address the app shows, for example
// wss://192.168.1.50:9443/ws. Any other scheme is refused. Nothing is
// sent before Connect or the first request. options may be nil.
//
// Wrong settings are reported as an [ArgumentError].
func New(address string, options *Options) (*Client, error) {
	var settings Options
	if options != nil {
		settings = *options
	}
	parts, err := url.Parse(address)
	if err != nil || strings.ToLower(parts.Scheme) != "wss" ||
		parts.Hostname() == "" {
		return nil, argumentError(
			"SMS Dock needs a wss:// URL, for example " +
				"wss://192.168.1.50:9443/ws")
	}
	password := settings.Password
	if password == "" {
		password = os.Getenv(PasswordEnv)
	}
	if password == "" {
		return nil, argumentError(
			"No password: pass one or set the environment variable %s",
			PasswordEnv)
	}
	fingerprint := settings.Fingerprint
	if fingerprint == "" {
		fingerprint = os.Getenv(FingerprintEnv)
	}
	if fingerprint == "" && !settings.Insecure {
		return nil, argumentError(
			"No certificate fingerprint: pass the fingerprint shown on "+
				"the Server screen of the app or set the environment "+
				"variable %s", FingerprintEnv)
	}
	if settings.Timeout < 0 || settings.ConnectTimeout < 0 {
		return nil, argumentError("Timeouts must be positive")
	}
	if settings.ReconnectAttempts < NoReconnect {
		return nil, argumentError("ReconnectAttempts cannot be negative")
	}
	if settings.ReconnectDelay < 0 || settings.MaxReconnectDelay < 0 {
		return nil, argumentError("Reconnect delays cannot be negative")
	}
	if settings.EventQueueSize < 0 {
		return nil, argumentError("EventQueueSize must be at least 1")
	}
	if settings.RateLimitRetries < NoRateLimitRetries {
		return nil, argumentError("RateLimitRetries cannot be negative")
	}

	client := &Client{
		url:               address,
		password:          password,
		insecure:          settings.Insecure,
		timeout:           orDefault(settings.Timeout, defaultTimeout),
		connectTimeout:    orDefault(settings.ConnectTimeout, defaultConnectTimeout),
		reconnectAttempts: orDefault(settings.ReconnectAttempts, defaultReconnectAttempts),
		reconnectDelay:    orDefault(settings.ReconnectDelay, defaultReconnectDelay),
		maxReconnectDelay: orDefault(settings.MaxReconnectDelay, defaultMaxReconnectDelay),
		eventQueueSize:    orDefault(settings.EventQueueSize, defaultEventQueueSize),
		rateLimitRetries:  orDefault(settings.RateLimitRetries, defaultRateLimitRetries),
		onReconnect:       settings.OnReconnect,
		outstanding:       make(map[string]struct{}),
		responses:         make(map[string]*frame),
		missedKnown:       true,
		sleep:             sleepContext,
	}
	if client.reconnectAttempts == NoReconnect {
		client.reconnectAttempts = 0
	}
	if client.rateLimitRetries == NoRateLimitRetries {
		client.rateLimitRetries = 0
	}
	if !settings.Insecure {
		digits, err := NormalizeFingerprint(fingerprint)
		if err != nil {
			return nil, err
		}
		client.fingerprint = mustDecodeHex(digits)
	}
	return client, nil
}

// Dial returns a client that is connected and logged in: New followed by
// Connect.
func Dial(
	ctx context.Context, address string, options *Options,
) (*Client, error) {
	client, err := New(address, options)
	if err != nil {
		return nil, err
	}
	if err := client.Connect(ctx); err != nil {
		return nil, err
	}
	return client, nil
}

// String returns the URL and whether the client is connected. It never
// shows the password.
func (c *Client) String() string {
	state := "closed"
	if c.Connected() {
		state = "connected"
	}
	return "<smsdock.Client " + c.url + " " + state + ">"
}

// ---------------------------------------------------------------------
// Connection
// ---------------------------------------------------------------------

// URL returns the address of the server.
func (c *Client) URL() string { return c.url }

// Connected reports whether a connection is open as far as the client
// knows.
func (c *Client) Connected() bool { return c.link != nil }

// ServerInfo returns name, version and protocol version of the app. It is
// nil before the first login.
func (c *Client) ServerInfo() *AppInfo {
	if c.serverInfo == nil {
		return nil
	}
	info := *c.serverInfo
	return &info
}

// Subscription returns the subscription in force, or nil without one.
func (c *Client) Subscription() *Subscription {
	if c.subscription == nil {
		return nil
	}
	copied := Subscription{
		Events: append([]string{}, c.subscription.Events...),
	}
	if c.subscription.Address != nil {
		copied.Address = Ptr(*c.subscription.Address)
	}
	return &copied
}

// PendingEvents returns the number of events that wait in the queue.
func (c *Client) PendingEvents() int { return len(c.events) }

// DroppedEvents returns the number of events dropped because the event
// queue was full.
func (c *Client) DroppedEvents() int { return c.droppedEvents }

// ConnectionCount returns the number of connections opened so far,
// reconnects included.
func (c *Client) ConnectionCount() int { return c.connectionCount }

// Epoch returns the epoch of the last login; 0 before the first.
//
// It identifies one run of the app. Sequence numbers begin at 1 again
// when it changes.
func (c *Client) Epoch() int64 { return c.epoch }

// LatestSeq returns the sequence number of the newest event the client
// knows of; 0 before the first login.
//
// This is the latestSeq of the last login or the seq of an event received
// since, whichever is higher. The phone counts all events, so a client
// that subscribed to some of them sees gaps.
func (c *Client) LatestSeq() int64 { return c.latestSeq }

// ServerRestarted reports whether the app was restarted before the last
// reconnect.
//
// It is true when the epoch of the current connection differs from the
// one of the connection before. Events were lost then, and their number
// is not known.
func (c *Client) ServerRestarted() bool { return c.restarted }

// MissedEvents returns the number of events of the phone during the last
// interruption, counted over all events, subscribed or not. It is 0
// after the first connect. known is false when the app was restarted and
// the number is not known.
func (c *Client) MissedEvents() (count int64, known bool) {
	return c.missed, c.missedKnown
}

// SetOnReconnect sets the function that is called with the client after
// every reconnect; nil removes it.
func (c *Client) SetOnReconnect(hook func(*Client)) { c.onReconnect = hook }

// Connect opens the connection, checks the certificate and logs in.
//
// It makes one attempt. The error is a [CertificateMismatchError], an
// [APIError] (authentication, licence), a [ProtocolError] or a
// [ConnectionInterruptedError].
func (c *Client) Connect(ctx context.Context) error {
	c.Close()
	err := c.open(ctx)
	if failure, isTransport := asTransport(err); isTransport {
		c.Close()
		return final("Cannot connect to "+c.url, failure)
	}
	return err
}

// Close closes the connection. Queued events are kept. It always returns
// nil.
func (c *Client) Close() error {
	link := c.link
	c.link = nil
	clear(c.outstanding)
	clear(c.responses)
	if link != nil {
		link.close()
	}
	return nil
}

// ---------------------------------------------------------------------
// Requests
// ---------------------------------------------------------------------

// Request sends one request and returns the result of its response as
// JSON.
//
// fields are the fields of the action as named in API.md; nil means
// none. The request is sent again after a lost connection and, when the
// server answers rate_limited and names the seconds to wait, after that
// time; use RequestOnce for anything that must not happen twice. The
// client waits for the response as long as Options.Timeout says, or until
// ctx is done.
//
// The error is an [APIError] when the server answers with an error and a
// [ConnectionInterruptedError] when the connection could not be restored.
func (c *Client) Request(
	ctx context.Context, action string, fields map[string]any,
) (json.RawMessage, error) {
	return c.request(ctx, action, fields, true)
}

// RequestOnce is Request without a repeat: it neither reconnects after a
// lost connection nor waits when the server answers rate_limited.
func (c *Client) RequestOnce(
	ctx context.Context, action string, fields map[string]any,
) (json.RawMessage, error) {
	return c.request(ctx, action, fields, false)
}

func (c *Client) request(
	ctx context.Context, action string, fields map[string]any, retry bool,
) (json.RawMessage, error) {
	if err := checkFields(fields); err != nil {
		return nil, err
	}
	what := "performing '" + action + "'"
	retries := 0
	if retry {
		retries = c.rateLimitRetries
	}
	for attempt := 0; ; attempt++ {
		result, err := c.requestConnected(ctx, action, fields, retries)
		failure, isTransport := asTransport(err)
		if !isTransport {
			return result, err
		}
		if !retry {
			c.Close()
			return nil, final("Connection interrupted while "+what, failure)
		}
		if err := c.recoverFrom(ctx, what, failure, attempt); err != nil {
			return nil, err
		}
	}
}

func (c *Client) requestConnected(
	ctx context.Context, action string, fields map[string]any, retries int,
) (json.RawMessage, error) {
	if c.link == nil {
		if err := c.open(ctx); err != nil {
			return nil, err
		}
	}
	return c.requestPatiently(ctx, action, fields, c.timeout, retries)
}

// SendRequest sends a request without waiting and returns its id.
//
// Several requests can be on their way at once; the server may answer
// them in any order. Fetch each response with WaitForResponse. These two
// methods do not reconnect and do not repeat a request that was answered
// with rate_limited.
func (c *Client) SendRequest(
	ctx context.Context, action string, fields map[string]any,
) (string, error) {
	if err := checkFields(fields); err != nil {
		return "", err
	}
	var key string
	err := func() error {
		if c.link == nil {
			if err := c.open(ctx); err != nil {
				return err
			}
		}
		var err error
		key, err = c.sendFrame(action, fields)
		return err
	}()
	if failure, isTransport := asTransport(err); isTransport {
		c.Close()
		return "", final(
			"Connection interrupted while sending '"+action+"'", failure)
	}
	return key, err
}

// WaitForResponse returns the result of a request sent with SendRequest.
// A timeout of 0 means Options.Timeout.
//
// The error is an [APIError] when the response is an error, a
// [TimeoutError] when none arrives in time and a
// [ConnectionInterruptedError] when the connection was lost.
func (c *Client) WaitForResponse(
	ctx context.Context, requestID string, timeout time.Duration,
) (json.RawMessage, error) {
	_, open := c.outstanding[requestID]
	_, answered := c.responses[requestID]
	if !open && !answered {
		return nil, argumentError(
			"No request with the id '%s' is open", requestID)
	}
	deadline := time.Now().Add(orDefault(timeout, c.timeout))
	result, err := c.awaitResponse(
		ctx, requestID, "request "+requestID, deadline)
	if failure, isTransport := asTransport(err); isTransport {
		var expired *TimeoutError
		if errors.As(failure.err, &expired) {
			return nil, expired
		}
		c.Close()
		return nil, final(
			"Connection interrupted while waiting for '"+requestID+"'",
			failure)
	}
	return result, err
}

// ---------------------------------------------------------------------
// Internals
// ---------------------------------------------------------------------

// open connects, pins the certificate, logs in and restores the
// subscription.
func (c *Client) open(ctx context.Context) error {
	if c.insecure && !c.warned {
		c.warned = true
		fmt.Fprintln(os.Stderr,
			"smsdock: WARNING: the certificate of the server is not "+
				"checked (insecure). Anyone on the network can read the "+
				"password and the messages.")
	}
	link, err := dial(ctx, c.url, c.fingerprint, c.connectTimeout)
	if err != nil {
		return err
	}
	c.link = link
	c.connectionCount++
	reconnected := c.loggedIn
	if err := c.handshake(ctx); err != nil {
		c.Close()
		return err
	}
	if reconnected && c.onReconnect != nil {
		c.onReconnect(c)
	}
	return nil
}

func (c *Client) handshake(ctx context.Context) error {
	if c.fingerprint != nil {
		// The certificate was compared during the TLS handshake. Look
		// once more at the connection that is going to carry the
		// password.
		state, isTLS := c.link.tlsState()
		if !isTLS {
			return protocolError(
				"Cannot read the certificate of the server from the " +
					"connection; refusing to go on")
		}
		if mismatch := checkPin(state, c.fingerprint); mismatch != nil {
			return mismatch
		}
	}
	hello, err := c.receive(ctx, time.Now().Add(c.timeout))
	if err != nil {
		if errors.Is(err, errReceiveTimeout) {
			return &transportError{err: &TimeoutError{
				Message: "No 'hello' from the server in time",
			}}
		}
		return err
	}
	if string(hello.fields["event"]) != `"hello"` {
		return protocolError(
			"Expected 'hello', received %s", strings.TrimSpace(string(hello.raw)))
	}
	if version := string(hello.fields["protocolVersion"]); version !=
		strconv.Itoa(APIProtocolVersion) {
		return protocolError(
			"The server speaks protocol version %s, this client version %d",
			orText(version, "null"), APIProtocolVersion)
	}
	raw, err := c.requestPatiently(
		ctx, "authenticate", map[string]any{"password": c.password},
		c.timeout, c.rateLimitRetries,
	)
	if err != nil {
		return err
	}
	var result map[string]json.RawMessage
	if err := json.Unmarshal(raw, &result); err != nil {
		return protocolError("The result of 'authenticate' is not a JSON object")
	}
	if version := string(result["protocolVersion"]); version !=
		strconv.Itoa(APIProtocolVersion) {
		return protocolError(
			"The login answered with protocol version %s",
			orText(version, "null"))
	}
	c.serverInfo = nil
	var app AppInfo
	if isObject(result["app"]) && json.Unmarshal(result["app"], &app) == nil {
		c.serverInfo = &app
	}
	if err := c.noteLogin(result["epoch"], result["latestSeq"]); err != nil {
		return err
	}
	subscription := c.subscription
	if subscription != nil && len(subscription.Events) > 0 {
		fields := map[string]any{"events": subscription.Events}
		if subscription.Address != nil {
			fields["address"] = *subscription.Address
		}
		_, err := c.requestPatiently(
			ctx, "subscribe", fields, c.timeout, c.rateLimitRetries)
		if err != nil {
			return err
		}
	}
	return nil
}

// noteLogin compares epoch and sequence number with the connection
// before.
func (c *Client) noteLogin(rawEpoch, rawLatest json.RawMessage) error {
	epoch, epochOK := integer(rawEpoch)
	latest, latestOK := integer(rawLatest)
	if !epochOK || !latestOK {
		return protocolError(
			"The login answered without 'epoch' and 'latestSeq'")
	}
	switch {
	case !c.loggedIn:
		c.restarted, c.missed, c.missedKnown = false, 0, true
	case epoch != c.epoch:
		c.restarted, c.missed, c.missedKnown = true, 0, false
	default:
		c.restarted, c.missedKnown = false, true
		c.missed = max(0, latest-c.latestSeq)
	}
	c.loggedIn = true
	c.epoch, c.latestSeq = epoch, latest
	return nil
}

// requestPatiently sends a request; it waits and repeats it while it is
// rate limited.
//
// Only when the server names the seconds to wait. The limit of messages
// per minute comes without them and is reported at once.
func (c *Client) requestPatiently(
	ctx context.Context, action string, fields map[string]any,
	timeout time.Duration, retries int,
) (json.RawMessage, error) {
	for refused := 0; ; refused++ {
		result, err := c.requestOnce(ctx, action, fields, timeout)
		var refusal *APIError
		if !errors.As(err, &refusal) {
			return result, err
		}
		seconds, named := refusal.RetryAfter()
		wait := time.Duration(seconds) * time.Second
		if refusal.Code != "rate_limited" || !named || wait > timeout ||
			refused >= retries {
			return nil, err
		}
		if err := c.sleep(ctx, max(0, wait)); err != nil {
			return nil, err
		}
	}
}

// recoverFrom closes the connection after a failure and waits before the
// next attempt. It returns an error instead when the failure is final or
// no attempt is left.
func (c *Client) recoverFrom(
	ctx context.Context, what string, failure *transportError, attempt int,
) error {
	c.Close()
	if fatal := fatalError(failure); fatal != nil {
		return fatal
	}
	attempts := c.reconnectAttempts
	if attempts != ReconnectForever && attempt >= attempts {
		return interrupted("Connection interrupted while "+what, failure)
	}
	exponent := min(attempt, maxBackoffExponent)
	delay := min(c.reconnectDelay<<exponent, c.maxReconnectDelay)
	return c.sleep(ctx, delay)
}

func (c *Client) requestOnce(
	ctx context.Context, action string, fields map[string]any,
	timeout time.Duration,
) (json.RawMessage, error) {
	key, err := c.sendFrame(action, fields)
	if err != nil {
		return nil, err
	}
	return c.awaitResponse(
		ctx, key, "'"+action+"'", time.Now().Add(timeout))
}

func (c *Client) sendFrame(
	action string, fields map[string]any,
) (string, error) {
	if c.link == nil {
		return "", &ConnectionInterruptedError{
			Message: "No connection to SMS Dock",
		}
	}
	c.requestNumber++
	key := "go-" + strconv.Itoa(c.requestNumber)
	payload, err := encodeFrame(key, action, fields)
	if err != nil {
		return "", err
	}
	if len(payload) > MaxFrameBytes {
		return "", argumentError(
			"The request is larger than %d KiB", MaxFrameBytes/1024)
	}
	if err := c.link.send(payload, c.timeout); err != nil {
		return "", err
	}
	c.outstanding[key] = struct{}{}
	return key, nil
}

// awaitResponse reads until the response to the request key arrives.
// Events that arrive meanwhile are queued. A response that does not
// arrive by the deadline is a transportError that holds a TimeoutError.
func (c *Client) awaitResponse(
	ctx context.Context, key, label string, deadline time.Time,
) (json.RawMessage, error) {
	for {
		response, answered := c.responses[key]
		if !answered {
			message, err := c.receive(ctx, deadline)
			if errors.Is(err, errReceiveTimeout) {
				return nil, &transportError{err: &TimeoutError{
					Message: "No response to " + label + " in time",
				}}
			}
			if err != nil {
				if ctx.Err() != nil {
					// Nobody waits for this response any more.
					delete(c.outstanding, key)
				}
				return nil, err
			}
			if message.isEvent() {
				c.queueEvent(decodeEvent(message))
			} else {
				c.fileResponse(message)
			}
			continue
		}
		delete(c.responses, key)
		delete(c.outstanding, key)
		switch string(response.fields["ok"]) {
		case "true":
			result := response.fields["result"]
			if !isObject(result) {
				return nil, protocolError(
					"The result of %s is not a JSON object", label)
			}
			return result, nil
		case "false":
			var fields map[string]any
			if json.Unmarshal(response.fields["error"], &fields) == nil &&
				fields != nil {
				return nil, apiError(fields)
			}
		}
		return nil, protocolError(
			"Malformed response: %s", strings.TrimSpace(string(response.raw)))
	}
}

// fileResponse keeps a response for the request that waits for it.
func (c *Client) fileResponse(response *frame) {
	key, named := responseKey(response.fields["id"])
	if !named && len(c.outstanding) == 1 {
		// The server could not read the id of the only open request.
		for only := range c.outstanding {
			key, named = only, true
		}
	}
	if !named {
		return
	}
	if _, open := c.outstanding[key]; open {
		c.responses[key] = response
	}
	// Anything else answers a request nobody waits for any more.
}

// receive returns the next frame of the server, decoded.
func (c *Client) receive(
	ctx context.Context, deadline time.Time,
) (*frame, error) {
	if c.link == nil {
		return nil, &ConnectionInterruptedError{
			Message: "No connection to SMS Dock",
		}
	}
	message, err := c.link.receive(ctx, deadline)
	if err != nil {
		return nil, err
	}
	if !message.text {
		return nil, protocolError("The server sent a binary frame")
	}
	if !json.Valid(message.data) {
		return nil, protocolError("The server sent invalid JSON")
	}
	decoded := &frame{raw: message.data}
	if !isObject(message.data) ||
		json.Unmarshal(message.data, &decoded.fields) != nil {
		return nil, protocolError("The frame is not a JSON object")
	}
	if decoded.isEvent() {
		sequence, isInteger := integer(decoded.fields["seq"])
		if isInteger && (!c.loggedIn || sequence > c.latestSeq) {
			c.latestSeq = sequence
		}
	}
	return decoded, nil
}

func (c *Client) queueEvent(event Event) {
	if len(c.events) >= c.eventQueueSize {
		c.events = c.events[1:]
		c.droppedEvents++
	}
	c.events = append(c.events, event)
}

// ---------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------

func checkFields(fields map[string]any) error {
	if _, present := fields["id"]; present {
		return argumentError(
			"'id' is the id of the request; the client sets it")
	}
	if _, present := fields["action"]; present {
		return argumentError(
			"'action' is the name of the action; pass it as the argument")
	}
	return nil
}

// encodeFrame returns the request as compact JSON: id, action, then the
// fields in the order of their names. Characters outside ASCII are
// written as they are.
func encodeFrame(
	key, action string, fields map[string]any,
) ([]byte, error) {
	var buffer bytes.Buffer
	encoder := json.NewEncoder(&buffer)
	encoder.SetEscapeHTML(false)
	write := func(name string, value any) error {
		if err := encoder.Encode(name); err != nil {
			return err
		}
		buffer.Truncate(buffer.Len() - 1)
		buffer.WriteByte(':')
		if err := encoder.Encode(value); err != nil {
			return argumentError("The field '%s' cannot be sent: %v", name, err)
		}
		buffer.Truncate(buffer.Len() - 1)
		return nil
	}
	buffer.WriteByte('{')
	if err := write("id", key); err != nil {
		return nil, err
	}
	buffer.WriteByte(',')
	if err := write("action", action); err != nil {
		return nil, err
	}
	names := make([]string, 0, len(fields))
	for name := range fields {
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		buffer.WriteByte(',')
		if err := write(name, fields[name]); err != nil {
			return nil, err
		}
	}
	buffer.WriteByte('}')
	return buffer.Bytes(), nil
}

// responseKey returns the id of a response as text; named is false for
// null and for a missing id.
func responseKey(raw json.RawMessage) (key string, named bool) {
	text := strings.TrimSpace(string(raw))
	if text == "" || text == "null" {
		return "", false
	}
	if text[0] == '"' {
		if json.Unmarshal(raw, &key) != nil {
			return "", false
		}
		return key, true
	}
	return text, true
}

// isObject reports whether valid JSON is an object.
func isObject(raw []byte) bool {
	trimmed := bytes.TrimSpace(raw)
	return len(trimmed) > 0 && trimmed[0] == '{'
}

// integer reads a JSON integer; ok is false for anything else.
func integer(raw json.RawMessage) (value int64, ok bool) {
	value, err := strconv.ParseInt(strings.TrimSpace(string(raw)), 10, 64)
	return value, err == nil
}

func sleepContext(ctx context.Context, duration time.Duration) error {
	if duration <= 0 {
		return ctx.Err()
	}
	timer := time.NewTimer(duration)
	defer timer.Stop()
	select {
	case <-timer.C:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func orDefault[T comparable](value, fallback T) T {
	var zero T
	if value == zero {
		return fallback
	}
	return value
}

func orText(value, fallback string) string {
	if value == "" {
		return fallback
	}
	return value
}

func mustDecodeHex(digits string) []byte {
	decoded := make([]byte, len(digits)/2)
	for index := range decoded {
		value, err := strconv.ParseUint(digits[2*index:2*index+2], 16, 8)
		if err != nil {
			panic("smsdock: fingerprint was not normalised")
		}
		decoded[index] = byte(value)
	}
	return decoded
}
