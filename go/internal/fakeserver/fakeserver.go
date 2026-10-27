// Package fakeserver is a fake SMS Dock server that speaks protocol
// version 1 of API.md.
//
// It keeps all data in memory and sends nothing. The tests use it over
// real TLS with a throwaway certificate. It can also run on its own to
// try the examples and the command-line tool without a phone; see
// internal/cmd/fakeserver.
//
// The real server runs on the phone. This one follows API.md; where the
// document leaves something open, a comment names the assumption. It is
// a port of python/tests/fake_server.py.
package fakeserver

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"math"
	"math/big"
	"net"
	"net/http"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/gorilla/websocket"
)

// ProtocolVersion is the protocol version of API.md.
const ProtocolVersion = 1

// MaxFrameBytes is the largest frame a client may send.
const MaxFrameBytes = 64 * 1024

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

var (
	requestIDPattern = regexp.MustCompile(`^[A-Za-z0-9._-]{1,64}$`)
	numberPattern    = regexp.MustCompile(`^\+?[0-9]{3,15}$`)
	digitsPattern    = regexp.MustCompile(`^\+?[0-9]+$`)
	ignoredInNumbers = regexp.MustCompile(`[\s\-./()]`)
)

// Certificate is a throwaway self-signed certificate like the one of the
// phone.
type Certificate struct {
	// TLS is the certificate with its key.
	TLS tls.Certificate
	// DER is the certificate in DER form.
	DER []byte
	// Fingerprint is its SHA-256 as AB:CD:….
	Fingerprint string
}

// NewCertificate creates a self-signed EC certificate that names no host.
func NewCertificate() (*Certificate, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	serial, err := rand.Int(rand.Reader, big.NewInt(math.MaxInt64))
	if err != nil {
		return nil, err
	}
	template := &x509.Certificate{
		SerialNumber: serial,
		Subject:      pkix.Name{CommonName: "SMS Dock"},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(48 * time.Hour),
	}
	der, err := x509.CreateCertificate(
		rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		return nil, err
	}
	digest := sha256.Sum256(der)
	text := strings.ToUpper(hex.EncodeToString(digest[:]))
	pairs := make([]string, 0, len(digest))
	for index := 0; index < len(text); index += 2 {
		pairs = append(pairs, text[index:index+2])
	}
	return &Certificate{
		TLS:         tls.Certificate{Certificate: [][]byte{der}, PrivateKey: key},
		DER:         der,
		Fingerprint: strings.Join(pairs, ":"),
	}, nil
}

// Field is one field of an [Object].
type Field struct {
	Name  string
	Value any
}

// Object is a JSON object whose fields keep their order.
type Object []Field

// Obj returns an Object made of names and values in turn:
// Obj("id", 7, "to", "+43…").
func Obj(pairs ...any) Object {
	if len(pairs)%2 != 0 {
		panic("fakeserver: Obj needs names and values in turn")
	}
	object := make(Object, 0, len(pairs)/2)
	for index := 0; index < len(pairs); index += 2 {
		object = append(object, Field{pairs[index].(string), pairs[index+1]})
	}
	return object
}

// MarshalJSON writes the fields in their order.
func (o Object) MarshalJSON() ([]byte, error) {
	var buffer strings.Builder
	buffer.WriteByte('{')
	for index, field := range o {
		if index > 0 {
			buffer.WriteByte(',')
		}
		buffer.Write(compact(field.Name))
		buffer.WriteByte(':')
		buffer.Write(compact(field.Value))
	}
	buffer.WriteByte('}')
	return []byte(buffer.String()), nil
}

// Message is a stored SMS, with the fields of API.md in their order.
type Message struct {
	ID             int64   `json:"id"`
	ConversationID int64   `json:"conversationId"`
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
}

// OutboxEntry is the state of a message handed to the fake server.
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
}

// SentMessage is one message handed to the (imaginary) radio. Group is
// the request id of the group message it belongs to, or "".
type SentMessage struct {
	RequestID string
	To        string
	Body      string
	Sim       int
	Group     string
}

type conversation struct {
	ID           int64   `json:"id"`
	Address      string  `json:"address"`
	Contact      *string `json:"contact"`
	Snippet      string  `json:"snippet"`
	Timestamp    int64   `json:"timestamp"`
	Direction    string  `json:"direction"`
	MessageCount int     `json:"messageCount"`
	UnreadCount  int     `json:"unreadCount"`
	Sim          *int    `json:"sim"`
}

type contactNumber struct {
	Number string `json:"number"`
	Label  string `json:"label"`
}

type contact struct {
	Name    string          `json:"name"`
	Numbers []contactNumber `json:"numbers"`
}

type sim struct {
	SubscriptionID int     `json:"subscriptionId"`
	Slot           int     `json:"slot"`
	Name           string  `json:"name"`
	Number         *string `json:"number"`
	IsDefault      bool    `json:"isDefault"`
}

// failure is an error response, optionally followed by a close frame.
type failure struct {
	fields    Object
	code      string
	closeCode int
}

func fail(code, message string, extra ...Field) *failure {
	fields := Object{{"code", code}, {"message", message}}
	return &failure{fields: append(fields, extra...), code: code}
}

type client struct {
	socket        *websocket.Conn
	authenticated bool
	events        map[string]bool
	address       *string
	requestTimes  []time.Time
	refused       int
}

// Options are the settings of a [Server]. The zero value of every field
// selects its default.
type Options struct {
	// Password is the password of the server. Default: "secret123".
	Password string
	// Certificate is the certificate to present. Default: a new one.
	Certificate *Certificate
	// Host is the address to listen on. Default: 127.0.0.1.
	Host string
	// Port is the port to listen on. Default: a free one.
	Port int
	// AuthTimeout is the time a client has to log in. Default: 10 s.
	AuthTimeout time.Duration
	// MaxClients is the number of clients allowed at once. Default: 8.
	MaxClients int
	// SendDelay is the time the imaginary radio needs for a message.
	// Default: 20 ms.
	SendDelay time.Duration
	// DeliveryReports lets a delivery report follow every sent message.
	DeliveryReports bool
	// RequestsPerSecond is the limit of frames per connection.
	// Default: 20.
	RequestsPerSecond int
	// MaxRefused is the number of refused frames in a row after which
	// the connection is closed with 4429. Default: 20.
	MaxRefused int
}

// Server is an in-memory SMS Dock server on wss://127.0.0.1:PORT/ws.
//
// All methods are safe for concurrent use.
type Server struct {
	options     Options
	certificate *Certificate
	listener    net.Listener
	http        *http.Server
	handlers    sync.WaitGroup
	port        int

	mu               sync.Mutex
	stopped          bool
	epoch            int64
	protocolVersion  int
	licenceEnforced  bool
	licensed         bool
	deleteMode       string
	failedLogins     int
	lockCount        int
	lockedUntil      time.Time
	sims             []sim
	contacts         []contact
	messages         []*Message
	outbox           map[string]*OutboxEntry
	bodies           map[string]string
	failNumbers      map[string]bool
	refuseNumbers    map[string]bool
	sent             []SentMessage
	requests         []map[string]any
	refusedCount     int
	connectionCount  int
	loginCount       int
	nextMessageID    int64
	conversationKeys []string
	sequence         int64
	clients          []*client
	drops            map[string][]string
	failures         map[string][]*failure
	results          map[string][]any
	ignored          map[string]int
	framesBefore     []any
	hold             int
	held             [][]byte
}

// Start starts a server and returns it. options may be nil.
func Start(options *Options) (*Server, error) {
	server := &Server{
		protocolVersion: ProtocolVersion,
		deleteMode:      "hidden",
		epoch:           time.Now().UnixMilli(),
		outbox:          make(map[string]*OutboxEntry),
		bodies:          make(map[string]string),
		failNumbers:     make(map[string]bool),
		refuseNumbers:   make(map[string]bool),
		nextMessageID:   2800,
		drops:           make(map[string][]string),
		failures:        make(map[string][]*failure),
		results:         make(map[string][]any),
		ignored:         make(map[string]int),
	}
	if options != nil {
		server.options = *options
	}
	settings := &server.options
	if settings.Password == "" {
		settings.Password = "secret123"
	}
	if settings.Host == "" {
		settings.Host = "127.0.0.1"
	}
	if settings.AuthTimeout == 0 {
		settings.AuthTimeout = 10 * time.Second
	}
	if settings.MaxClients == 0 {
		settings.MaxClients = 8
	}
	if settings.SendDelay == 0 {
		settings.SendDelay = 20 * time.Millisecond
	}
	if settings.RequestsPerSecond == 0 {
		settings.RequestsPerSecond = 20
	}
	if settings.MaxRefused == 0 {
		settings.MaxRefused = 20
	}
	server.certificate = settings.Certificate
	if server.certificate == nil {
		certificate, err := NewCertificate()
		if err != nil {
			return nil, err
		}
		server.certificate = certificate
	}
	work := "+4369087654321"
	server.sims = []sim{
		{SubscriptionID: 1, Slot: 0, Name: "Play", IsDefault: true},
		{SubscriptionID: 2, Slot: 1, Name: "Work", Number: &work},
	}
	address := net.JoinHostPort(settings.Host, strconv.Itoa(settings.Port))
	listener, err := tls.Listen("tcp", address, &tls.Config{
		Certificates: []tls.Certificate{server.certificate.TLS},
		MinVersion:   tls.VersionTLS12,
	})
	if err != nil {
		return nil, err
	}
	server.listener = listener
	server.port = listener.Addr().(*net.TCPAddr).Port
	server.http = &http.Server{
		Handler: http.HandlerFunc(server.serve),
		// Failed TLS handshakes are part of the tests; do not log them.
		ErrorLog: log.New(io.Discard, "", 0),
	}
	server.http.SetKeepAlivesEnabled(false)
	go server.http.Serve(listener)
	return server, nil
}

// Stop stops the server; clients see the close code 1001.
func (s *Server) Stop() {
	s.mu.Lock()
	if s.stopped {
		s.mu.Unlock()
		return
	}
	s.stopped = true
	for _, connected := range s.clients {
		s.closeClient(connected, 1001, "server is stopping")
	}
	s.mu.Unlock()
	s.http.Close()
	finished := make(chan struct{})
	go func() {
		s.handlers.Wait()
		close(finished)
	}()
	select {
	case <-finished:
	case <-time.After(3 * time.Second):
	}
}

// URL returns the address of the API.
func (s *Server) URL() string {
	return "wss://" + net.JoinHostPort(s.options.Host, strconv.Itoa(s.port)) +
		"/ws"
}

// Port returns the port the server listens on.
func (s *Server) Port() int { return s.port }

// Password returns the password of the server.
func (s *Server) Password() string { return s.options.Password }

// Fingerprint returns the fingerprint of the certificate as AB:CD:….
func (s *Server) Fingerprint() string { return s.certificate.Fingerprint }

// ---------------------------------------------------------------------
// What happened (for tests)
// ---------------------------------------------------------------------

// Requests returns every request object received and not refused, in
// order.
func (s *Server) Requests() []map[string]any {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]map[string]any{}, s.requests...)
}

// Actions returns the actions of all requests, in order.
func (s *Server) Actions() []string {
	var actions []string
	for _, request := range s.Requests() {
		action, _ := request["action"].(string)
		actions = append(actions, action)
	}
	return actions
}

// RequestsOf returns the requests with this action, in order.
func (s *Server) RequestsOf(action string) []map[string]any {
	var found []map[string]any
	for _, request := range s.Requests() {
		if request["action"] == action {
			found = append(found, request)
		}
	}
	return found
}

// Sent returns the messages handed to the (imaginary) radio, one entry
// per SMS.
func (s *Server) Sent() []SentMessage {
	s.mu.Lock()
	defer s.mu.Unlock()
	return append([]SentMessage{}, s.sent...)
}

// Messages returns copies of the stored messages.
func (s *Server) Messages() []Message {
	s.mu.Lock()
	defer s.mu.Unlock()
	messages := make([]Message, len(s.messages))
	for index, message := range s.messages {
		messages[index] = *message
	}
	return messages
}

// Outbox returns a copy of the outbox entry with this request id, or nil.
func (s *Server) Outbox(requestID string) *OutboxEntry {
	s.mu.Lock()
	defer s.mu.Unlock()
	entry := s.outbox[requestID]
	if entry == nil {
		return nil
	}
	copied := *entry
	return &copied
}

// Epoch returns the value that identifies one run of the app; Restart
// changes it.
func (s *Server) Epoch() int64 {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.epoch
}

// ClientCount returns the number of open connections.
func (s *Server) ClientCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.clients)
}

// ConnectionCount returns the number of WebSocket connections accepted
// so far.
func (s *Server) ConnectionCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.connectionCount
}

// LoginCount returns the number of authenticate requests so far.
func (s *Server) LoginCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.loginCount
}

// RefusedCount returns the number of frames answered with rate_limited
// by the limiter.
func (s *Server) RefusedCount() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.refusedCount
}

// ---------------------------------------------------------------------
// Controls (for tests)
// ---------------------------------------------------------------------

// SetLicence sets whether the licence is enforced and whether there is
// one.
func (s *Server) SetLicence(enforced, licensed bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.licenceEnforced, s.licensed = enforced, licensed
}

// SetDeleteMode sets the delete mode: "hidden" or "provider". With
// "provider" the server behaves as the default SMS app.
func (s *Server) SetDeleteMode(mode string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.deleteMode = mode
}

// SetProtocolVersion changes the protocol version the server announces.
func (s *Server) SetProtocolVersion(version int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.protocolVersion = version
}

// FailNumbers sets the numbers to which sending fails.
func (s *Server) FailNumbers(numbers ...string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.failNumbers = make(map[string]bool)
	for _, number := range numbers {
		s.failNumbers[number] = true
	}
}

// RefuseNumbers sets the numbers the phone refuses at once: a message to
// them is failed with the reason send_refused before anything is sent.
func (s *Server) RefuseNumbers(numbers ...string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.refuseNumbers = make(map[string]bool)
	for _, number := range numbers {
		s.refuseNumbers[number] = true
	}
}

// AddContact adds a contact with one number.
func (s *Server) AddContact(name, number, label string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.contacts = append(s.contacts, contact{
		Name: name, Numbers: []contactNumber{{Number: number, Label: label}},
	})
}

// NewMessage describes a message for AddMessage.
type NewMessage struct {
	Address string
	Body    string
	// Direction is "in" (default) or "out".
	Direction string
	// Timestamp is in milliseconds; 0 means now.
	Timestamp int64
	// Unread stores an unread message.
	Unread bool
	// Sim is the subscription id; 0 means 1.
	Sim int
	// RequestID is set for messages sent through SMS Dock.
	RequestID string
}

// AddMessage stores a message without sending an event.
func (s *Server) AddMessage(message NewMessage) Message {
	s.mu.Lock()
	defer s.mu.Unlock()
	return *s.addMessage(message)
}

// ReceiveSMS lets an SMS arrive: it is stored and sms.received is sent.
func (s *Server) ReceiveSMS(address, body string) Message {
	s.mu.Lock()
	defer s.mu.Unlock()
	message := s.addMessage(NewMessage{
		Address: address, Body: body, Unread: true,
	})
	s.emit("sms.received", Object{{"message", message}})
	return *message
}

// Emit sends an event to the subscribed clients.
func (s *Server) Emit(name string, data any) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.emit(name, data)
}

// Restart behaves like a restart of the app.
//
// All connections are lost, the sequence numbers begin at 1 again and
// the epoch changes. Messages and the outbox are kept.
func (s *Server) Restart() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.dropAll(0, "")
	s.sequence = 0
	s.epoch = max(s.epoch+1, time.Now().UnixMilli())
}

// DropConnections closes all connections: abruptly with the code 0, or
// with a close frame.
func (s *Server) DropConnections(code int, reason string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.dropAll(code, reason)
}

// DropOn drops the connection at the next request with this action.
//
// when is "before": before the request is carried out, or "after": after
// it was carried out but before the response is sent. Calling it again
// queues one more drop.
func (s *Server) DropOn(action, when string) {
	if when != "before" && when != "after" {
		panic("fakeserver: when must be 'before' or 'after'")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.drops[action] = append(s.drops[action], when)
}

// FailNext answers the next request with this action with an error.
// closeCode, if not 0, closes the connection after the answer; extra
// fields are added to the error object.
func (s *Server) FailNext(
	action, code, message string, closeCode int, extra ...Field,
) {
	if message == "" {
		message = "Injected failure."
	}
	injected := fail(code, message, extra...)
	injected.closeCode = closeCode
	s.mu.Lock()
	defer s.mu.Unlock()
	s.failures[action] = append(s.failures[action], injected)
}

// AnswerNext answers the next request with this action with this result.
func (s *Server) AnswerNext(action string, result any) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.results[action] = append(s.results[action], result)
}

// IgnoreNext does not answer the next requests with this action at all.
func (s *Server) IgnoreNext(action string, times int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.ignored[action] += times
}

// BeforeNextResponse sends these frames to the client right before the
// next response. A string is sent as it is, anything else as JSON.
func (s *Server) BeforeNextResponse(frames ...any) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.framesBefore = append(s.framesBefore, frames...)
}

// ReverseNextResponses holds the next count responses and sends them in
// reverse order.
func (s *Server) ReverseNextResponses(count int) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.hold = count
}

// LockLogins locks logins as after too many wrong passwords.
func (s *Server) LockLogins(duration time.Duration) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.lockedUntil = time.Now().Add(duration)
}

// SeedDemoData fills the server with a few contacts and conversations.
func (s *Server) SeedDemoData() {
	now := time.Now().UnixMilli()
	s.AddContact("Anna Example", "+4369912345678", "Mobile")
	s.AddContact("Ben Example", "+4369087654321", "Work")
	const minute = 60_000
	for _, row := range []struct {
		address, direction, body string
		age                      int64
	}{
		{"+4369912345678", "in", "Are you coming on Friday?", 300},
		{"+4369912345678", "out", "Yes, at eight.", 290},
		{"+4369912345678", "in", "Great, see you then.", 280},
		{"+4369087654321", "out", "The server is up again.", 200},
		{"+4369087654321", "in", "Thanks!", 190},
		{"A1", "in", "Your invoice is ready.", 120},
		{"+436641234567", "in", "STATUS", 5},
	} {
		s.AddMessage(NewMessage{
			Address: row.address, Body: row.body, Direction: row.direction,
			Timestamp: now - row.age*minute, Unread: row.age <= 100,
		})
	}
}

// ---------------------------------------------------------------------
// Connections
// ---------------------------------------------------------------------

var upgrader = websocket.Upgrader{
	CheckOrigin: func(*http.Request) bool { return true },
}

func (s *Server) serve(writer http.ResponseWriter, request *http.Request) {
	if request.URL.Path != "/ws" {
		http.Error(writer, "Not found", http.StatusNotFound)
		return
	}
	socket, err := upgrader.Upgrade(writer, request, nil)
	if err != nil {
		return
	}
	s.handlers.Add(1)
	defer s.handlers.Done()
	defer socket.Close()
	socket.SetReadLimit(MaxFrameBytes)

	connected := &client{socket: socket, events: make(map[string]bool)}
	s.mu.Lock()
	if s.stopped || len(s.clients) >= s.options.MaxClients {
		s.closeClient(connected, 4409, "too many clients")
		s.mu.Unlock()
		drain(socket)
		return
	}
	s.clients = append(s.clients, connected)
	s.connectionCount++
	timer := time.AfterFunc(s.options.AuthTimeout, func() {
		s.mu.Lock()
		defer s.mu.Unlock()
		if !connected.authenticated {
			s.closeClient(connected, 4401, "not authenticated in time")
		}
	})
	s.send(connected, Object{
		{"event", "hello"},
		{"protocolVersion", s.protocolVersion},
		{"authenticationRequired", true},
	})
	s.mu.Unlock()

	for {
		kind, frame, err := socket.ReadMessage()
		if err != nil {
			break
		}
		s.mu.Lock()
		if kind != websocket.TextMessage {
			s.closeClient(connected, 4400, "binary frame")
		} else {
			s.onFrame(connected, frame)
		}
		s.mu.Unlock()
	}
	timer.Stop()
	s.mu.Lock()
	s.removeClient(connected)
	s.mu.Unlock()
}

// drain reads until the client has closed the connection.
func drain(socket *websocket.Conn) {
	socket.SetReadDeadline(time.Now().Add(2 * time.Second))
	for {
		if _, _, err := socket.ReadMessage(); err != nil {
			return
		}
	}
}

func (s *Server) removeClient(gone *client) {
	for index, connected := range s.clients {
		if connected == gone {
			s.clients = append(s.clients[:index], s.clients[index+1:]...)
			return
		}
	}
}

// closeClient sends a close frame. The connection ends when the client
// answers it, or two seconds later.
func (s *Server) closeClient(connected *client, code int, reason string) {
	connected.socket.WriteControl(
		websocket.CloseMessage, websocket.FormatCloseMessage(code, reason),
		time.Now().Add(time.Second),
	)
	connected.socket.SetReadDeadline(time.Now().Add(2 * time.Second))
}

// abort ends a connection without a close frame.
func abort(connected *client) {
	underlying := connected.socket.UnderlyingConn()
	if secure, isTLS := underlying.(*tls.Conn); isTLS {
		underlying = secure.NetConn()
	}
	underlying.Close()
}

func (s *Server) dropAll(code int, reason string) {
	for _, connected := range s.clients {
		if code == 0 {
			abort(connected)
		} else {
			s.closeClient(connected, code, reason)
		}
	}
}

func compact(value any) []byte {
	var buffer strings.Builder
	encoder := json.NewEncoder(&buffer)
	encoder.SetEscapeHTML(false)
	if err := encoder.Encode(value); err != nil {
		panic(fmt.Sprintf("fakeserver: cannot encode %T: %v", value, err))
	}
	return []byte(strings.TrimSuffix(buffer.String(), "\n"))
}

func (s *Server) send(connected *client, frame any) {
	var text []byte
	switch value := frame.(type) {
	case string:
		text = []byte(value)
	case []byte:
		text = value
	default:
		text = compact(frame)
	}
	connected.socket.SetWriteDeadline(time.Now().Add(5 * time.Second))
	connected.socket.WriteMessage(websocket.TextMessage, text)
}

func (s *Server) onFrame(connected *client, frame []byte) {
	var request map[string]any
	var decoded any
	if json.Unmarshal(frame, &decoded) == nil {
		request, _ = decoded.(map[string]any)
	}
	if !s.withinRate(connected) {
		// Every frame counts, the login and unreadable ones as well.
		var identifier any
		switch value := request["id"].(type) {
		case string, float64:
			identifier = value
		}
		s.refusedCount++
		s.send(connected, Object{
			{"id", identifier}, {"ok", false},
			{"error", Object{
				{"code", "rate_limited"},
				{"message", "Too many requests."},
				{"retryAfter", 1},
			}},
		})
		connected.refused++
		if connected.refused > s.options.MaxRefused {
			s.closeClient(connected, 4429, "Too many requests.")
		}
		return
	}
	connected.refused = 0
	if request == nil {
		s.respond(connected, Object{
			{"id", nil}, {"ok", false},
			{"error", Object{
				{"code", "invalid_json"},
				{"message", "The frame is not a JSON object."},
			}},
		})
		return
	}
	s.requests = append(s.requests, request)
	identifier := request["id"]
	action, _ := request["action"].(string)
	drops := s.drops[action]
	if len(drops) > 0 && drops[0] == "before" {
		s.drops[action] = drops[1:]
		abort(connected)
		return
	}

	var after []func()
	var response Object
	closeCode := 0
	closeReason := ""
	result, err, answered := s.dispatch(connected, request, &after)
	if !answered {
		return
	}
	if err != nil {
		response = Object{{"id", identifier}, {"ok", false}, {"error", err.fields}}
		closeCode, closeReason = err.closeCode, err.code
	} else {
		response = Object{{"id", identifier}, {"ok", true}, {"result", result}}
	}

	drops = s.drops[action]
	if len(drops) > 0 && drops[0] == "after" {
		s.drops[action] = drops[1:]
		abort(connected)
		return
	}
	s.respond(connected, response)
	if closeCode != 0 {
		s.closeClient(connected, closeCode, closeReason)
	}
	for _, task := range after {
		task()
	}
}

// withinRate counts a frame; at most RequestsPerSecond fit in a second.
func (s *Server) withinRate(connected *client) bool {
	now := time.Now()
	times := connected.requestTimes
	for len(times) > 0 && now.Sub(times[0]) >= time.Second {
		times = times[1:]
	}
	connected.requestTimes = times
	if len(times) >= s.options.RequestsPerSecond {
		return false
	}
	connected.requestTimes = append(times, now)
	return true
}

func (s *Server) respond(connected *client, response any) {
	frames := s.framesBefore
	s.framesBefore = nil
	for _, frame := range frames {
		s.send(connected, frame)
	}
	if s.hold > 0 {
		s.held = append(s.held, compact(response))
		if len(s.held) < s.hold {
			return
		}
		held := s.held
		s.held, s.hold = nil, 0
		for index := len(held) - 1; index >= 0; index-- {
			s.send(connected, held[index])
		}
		return
	}
	s.send(connected, response)
}

// ---------------------------------------------------------------------
// Actions
// ---------------------------------------------------------------------

// dispatch carries out a request. answered is false when no response is
// to be sent. Tasks added to after run once the response is sent.
func (s *Server) dispatch(
	connected *client, request map[string]any, after *[]func(),
) (result any, err *failure, answered bool) {
	action, isText := request["action"].(string)
	if !isText {
		return nil, fail("invalid_request", "The field 'action' is missing."), true
	}
	switch request["id"].(type) {
	case nil, string, float64:
	default:
		return nil, fail(
			"invalid_request", "'id' must be a string or a number."), true
	}
	if s.ignored[action] > 0 {
		s.ignored[action]--
		return nil, nil, false
	}
	if action == "authenticate" {
		result, err = s.authenticate(connected, request)
		return result, err, true
	}
	if !connected.authenticated {
		return nil, fail("not_authenticated", "Authenticate first."), true
	}
	if failures := s.failures[action]; len(failures) > 0 {
		s.failures[action] = failures[1:]
		return nil, failures[0], true
	}
	if results := s.results[action]; len(results) > 0 {
		s.results[action] = results[1:]
		return results[0], nil, true
	}
	switch action {
	case "status":
		result = s.status()
	case "ping":
		result = Object{{"time", time.Now().UnixMilli()}}
	case "conversations.list":
		result, err = s.conversationsList(request)
	case "messages.list":
		result, err = s.messagesList(request)
	case "messages.get":
		result, err = s.findMessage(request)
	case "messages.status":
		result, err = s.messagesStatus(request)
	case "messages.delete":
		result, err = s.messagesDelete(request, after)
	case "conversations.delete":
		result, err = s.conversationsDelete(request, after)
	case "conversations.markRead":
		result, err = s.conversationsMarkRead(request)
	case "contacts.list":
		result, err = s.contactsList(request)
	case "messages.send":
		result, err = s.messagesSend(request)
	case "messages.sendGroup":
		result, err = s.messagesSendGroup(request)
	case "subscribe":
		result, err = s.subscribe(connected, request)
	case "unsubscribe":
		result, err = s.unsubscribe(connected, request)
	default:
		err = fail("unknown_action", "Unknown action '"+action+"'.")
	}
	return result, err, true
}

func (s *Server) authenticate(
	connected *client, request map[string]any,
) (any, *failure) {
	s.loginCount++
	// Assumption, as in the app: the licence is looked at before the
	// password, so a login without licence never counts as wrong.
	if s.licenceEnforced && !s.licensed {
		refusal := fail(
			"licence_required", "The API needs the full licence of SMS Dock.")
		refusal.closeCode = 4403
		return nil, refusal
	}
	if remaining := time.Until(s.lockedUntil); remaining > 0 {
		seconds := int(math.Ceil(remaining.Seconds()))
		refusal := fail(
			"locked_out",
			fmt.Sprintf("Too many attempts. Try again in %d seconds.", seconds),
			Field{"retryAfter", seconds},
		)
		refusal.closeCode = 4401
		return nil, refusal
	}
	if request["password"] != s.options.Password {
		s.failedLogins++
		if s.failedLogins >= 5 {
			seconds := min(30*(1<<s.lockCount), 900)
			s.lockCount++
			s.failedLogins = 0
			s.lockedUntil = time.Now().Add(time.Duration(seconds) * time.Second)
		}
		refusal := fail("invalid_password", "Wrong password.")
		refusal.closeCode = 4401
		return nil, refusal
	}
	s.failedLogins, s.lockCount = 0, 0
	connected.authenticated = true
	return Object{
		{"protocolVersion", s.protocolVersion},
		{"app", s.app()},
		{"latestSeq", s.sequence},
		{"epoch", s.epoch},
	}, nil
}

func (s *Server) app() Object {
	return Object{
		{"name", "SMS Dock"}, {"version", "1.0.0"},
		{"protocolVersion", s.protocolVersion},
	}
}

func (s *Server) status() any {
	return Object{
		{"app", s.app()},
		{"sims", s.sims},
		{"permissions", Object{
			{"readSms", true}, {"sendSms", true}, {"receiveSms", true},
			{"contacts", true}, {"phoneState", true}, {"notifications", true},
		}},
		{"defaultSmsApp", s.deleteMode == "provider"},
		{"deleteMode", s.deleteMode},
		{"licence", Object{
			{"enforced", s.licenceEnforced},
			{"licensed", s.licensed},
			{"dailyLimit", 10},
			{"sentToday", len(s.sent)},
			{"remaining", nil},
		}},
		{"popup", true},
	}
}

func (s *Server) conversationsList(request map[string]any) (any, *failure) {
	query, err := optionalText(request, "q")
	if err != nil {
		return nil, err
	}
	unread, err := optionalBool(request, "unread")
	if err != nil {
		return nil, err
	}
	simFilter, err := optionalInt(request, "sim")
	if err != nil {
		return nil, err
	}
	items := s.conversations()
	keep := func(test func(conversation) bool) {
		var kept []conversation
		for _, item := range items {
			if test(item) {
				kept = append(kept, item)
			}
		}
		items = kept
	}
	if query != nil && *query != "" {
		needle := strings.ToLower(*query)
		matching := make(map[int64]bool)
		for _, message := range s.messages {
			if strings.Contains(strings.ToLower(message.Body), needle) {
				matching[message.ConversationID] = true
			}
		}
		keep(func(item conversation) bool {
			name := ""
			if item.Contact != nil {
				name = *item.Contact
			}
			return matching[item.ID] ||
				strings.Contains(strings.ToLower(item.Address), needle) ||
				strings.Contains(strings.ToLower(name), needle)
		})
	}
	if unread {
		keep(func(item conversation) bool { return item.UnreadCount > 0 })
	}
	if simFilter != nil {
		keep(func(item conversation) bool {
			return item.Sim != nil && int64(*item.Sim) == *simFilter
		})
	}
	keys := make([]string, len(items))
	for index, item := range items {
		keys[index] = fmt.Sprintf("%d:%d", item.Timestamp, item.ID)
	}
	return page(items, keys, request)
}

func (s *Server) messagesList(request map[string]any) (any, *failure) {
	address, err := optionalText(request, "address")
	if err != nil {
		return nil, err
	}
	conversationID, err := optionalInt(request, "conversationId")
	if err != nil {
		return nil, err
	}
	direction, err := optionalText(request, "direction")
	if err != nil {
		return nil, err
	}
	query, err := optionalText(request, "q")
	if err != nil {
		return nil, err
	}
	start, err := optionalInt(request, "from")
	if err != nil {
		return nil, err
	}
	end, err := optionalInt(request, "to")
	if err != nil {
		return nil, err
	}
	simFilter, err := optionalInt(request, "sim")
	if err != nil {
		return nil, err
	}
	unread, err := optionalBool(request, "unread")
	if err != nil {
		return nil, err
	}
	wanted := "all"
	if direction != nil && *direction != "" {
		wanted = *direction
	}
	if wanted != "in" && wanted != "out" && wanted != "all" {
		return nil, fail(
			"invalid_request", "'direction' must be 'in', 'out' or 'all'.")
	}
	sorted := append([]*Message{}, s.messages...)
	sort.SliceStable(sorted, func(a, b int) bool {
		if sorted[a].Timestamp != sorted[b].Timestamp {
			return sorted[a].Timestamp > sorted[b].Timestamp
		}
		return sorted[a].ID > sorted[b].ID
	})
	var items []*Message
	for _, message := range sorted {
		switch {
		case conversationID != nil:
			// conversationId wins if address is given too.
			if message.ConversationID != *conversationID {
				continue
			}
		case address != nil:
			if !samePeer(message.Address, *address) {
				continue
			}
		}
		if wanted != "all" && message.Direction != wanted {
			continue
		}
		if query != nil && *query != "" && !strings.Contains(
			strings.ToLower(message.Body), strings.ToLower(*query)) {
			continue
		}
		if start != nil && message.Timestamp < *start {
			continue
		}
		if end != nil && message.Timestamp > *end {
			continue
		}
		if simFilter != nil &&
			(message.Sim == nil || int64(*message.Sim) != *simFilter) {
			continue
		}
		if unread && message.Read {
			continue
		}
		items = append(items, message)
	}
	keys := make([]string, len(items))
	for index, item := range items {
		keys[index] = fmt.Sprintf("%d:%d", item.Timestamp, item.ID)
	}
	return page(items, keys, request)
}

func (s *Server) findMessage(request map[string]any) (*Message, *failure) {
	identifier, err := requiredInt(request, "messageId")
	if err != nil {
		return nil, err
	}
	for _, message := range s.messages {
		if message.ID == identifier {
			return message, nil
		}
	}
	return nil, fail(
		"not_found", fmt.Sprintf("No message with the id %d.", identifier))
}

func (s *Server) messagesStatus(request map[string]any) (any, *failure) {
	requestID, err := optionalText(request, "requestId")
	if err != nil {
		return nil, err
	}
	if requestID == nil {
		return nil, fail("invalid_request", "The field 'requestId' is missing.")
	}
	entry := s.outbox[*requestID]
	if entry == nil {
		return nil, fail(
			"not_found", "No message with the requestId "+*requestID+".")
	}
	copied := *entry
	return &copied, nil
}

func (s *Server) messagesDelete(
	request map[string]any, after *[]func(),
) (any, *failure) {
	message, err := s.findMessage(request)
	if err != nil {
		return nil, err
	}
	s.removeMessages(func(candidate *Message) bool { return candidate == message })
	mode := s.deleteMode
	*after = append(*after, func() {
		s.emit("sms.deleted", Object{
			{"id", message.ID},
			{"conversationId", message.ConversationID},
			{"address", message.Address},
			{"mode", mode},
		})
	})
	return Object{{"deleted", 1}, {"mode", mode}}, nil
}

func (s *Server) removeMessages(doomed func(*Message) bool) int {
	var kept []*Message
	for _, message := range s.messages {
		if !doomed(message) {
			kept = append(kept, message)
		}
	}
	removed := len(s.messages) - len(kept)
	s.messages = kept
	return removed
}

func (s *Server) conversationsDelete(
	request map[string]any, after *[]func(),
) (any, *failure) {
	identifier, err := requiredInt(request, "conversationId")
	if err != nil {
		return nil, err
	}
	var newest *Message
	for _, message := range s.messages {
		if message.ConversationID == identifier && newer(message, newest) {
			newest = message
		}
	}
	if newest == nil {
		return nil, fail("not_found", "This conversation does not exist.")
	}
	removed := s.removeMessages(func(candidate *Message) bool {
		return candidate.ConversationID == identifier
	})
	mode := s.deleteMode
	*after = append(*after, func() {
		s.emit("sms.deleted", Object{
			{"id", nil},
			{"conversationId", identifier},
			{"address", newest.Address},
			{"mode", mode},
		})
	})
	return Object{{"deleted", removed}, {"mode", mode}}, nil
}

func (s *Server) conversationsMarkRead(
	request map[string]any,
) (any, *failure) {
	// API.md names no order of the two errors. Assumption, as in the
	// app: the default SMS app is checked first.
	identifier, err := requiredInt(request, "conversationId")
	if err != nil {
		return nil, err
	}
	if s.deleteMode != "provider" {
		return nil, fail(
			"not_default_app",
			"Only the default SMS app can mark messages as read.")
	}
	found := false
	for _, message := range s.messages {
		if message.ConversationID == identifier {
			message.Read = true
			found = true
		}
	}
	if !found {
		return nil, fail("not_found", "This conversation does not exist.")
	}
	return Object{{"ok", true}}, nil
}

func (s *Server) contactsList(request map[string]any) (any, *failure) {
	query, err := optionalText(request, "q")
	if err != nil {
		return nil, err
	}
	sorted := append([]contact{}, s.contacts...)
	sort.SliceStable(sorted, func(a, b int) bool {
		return strings.ToLower(sorted[a].Name) < strings.ToLower(sorted[b].Name)
	})
	var items []contact
	for _, candidate := range sorted {
		if query != nil && *query != "" {
			needle := strings.ToLower(*query)
			matches := strings.Contains(strings.ToLower(candidate.Name), needle)
			for _, number := range candidate.Numbers {
				matches = matches || strings.Contains(number.Number, needle)
			}
			if !matches {
				continue
			}
		}
		items = append(items, candidate)
	}
	return page(items, nil, request)
}

func (s *Server) messagesSend(request map[string]any) (any, *failure) {
	to, err := optionalText(request, "to")
	if err != nil {
		return nil, err
	}
	if to == nil {
		return nil, fail("invalid_request", "The field 'to' is missing.")
	}
	body, err := optionalText(request, "body")
	if err != nil {
		return nil, err
	}
	if body == nil {
		return nil, fail("invalid_request", "The field 'body' is missing.")
	}
	simChoice, err := optionalInt(request, "sim")
	if err != nil {
		return nil, err
	}
	requestID, err := optionalText(request, "requestId")
	if err != nil {
		return nil, err
	}
	if requestID != nil && !requestIDPattern.MatchString(*requestID) {
		return nil, fail(
			"invalid_request",
			"'requestId' must be 1 to 64 characters of A-Z a-z 0-9 . _ -.")
	}
	number := NormalizeNumber(*to)
	if !numberPattern.MatchString(number) {
		return nil, fail("invalid_number", "'to' is not a phone number.")
	}
	if *body == "" {
		return nil, fail("invalid_request", "'body' is empty.")
	}
	parts := CountParts(*body)
	if parts > 10 {
		return nil, fail("message_too_long", fmt.Sprintf(
			"The message needs %d SMS parts; at most 10 are allowed.", parts))
	}
	if requestID != nil {
		if known := s.outbox[*requestID]; known != nil {
			// A repeat: never sent twice, answered with the current
			// status.
			if s.bodies[known.RequestID] != *body ||
				!samePeer(known.To, number) {
				return nil, fail(
					"invalid_request",
					"'requestId' was already used for another message.")
			}
			return Object{
				{"requestId", known.RequestID}, {"to", known.To},
				{"parts", known.Parts}, {"status", known.Status},
			}, nil
		}
	}
	chosen, err := s.subscription(simChoice)
	if err != nil {
		return nil, err
	}
	identifier := requestIDOrRandom(requestID)
	entry := s.accept(identifier, number, *body, parts, chosen, "")
	return sendResult(entry), nil
}

// subscription returns the SIM to send with; the default one without a
// choice.
func (s *Server) subscription(simChoice *int64) (int, *failure) {
	chosen := 0
	if simChoice == nil {
		for _, candidate := range s.sims {
			if candidate.IsDefault {
				chosen = candidate.SubscriptionID
			}
		}
		return chosen, nil
	}
	for _, candidate := range s.sims {
		if int64(candidate.SubscriptionID) == *simChoice {
			chosen = candidate.SubscriptionID
		}
	}
	if chosen == 0 {
		return 0, fail("sim_unavailable", fmt.Sprintf(
			"No SIM with the subscription id %d.", *simChoice))
	}
	return chosen, nil
}

func requestIDOrRandom(requestID *string) string {
	if requestID != nil {
		return *requestID
	}
	random := make([]byte, 16)
	rand.Read(random)
	return hex.EncodeToString(random)
}

// accept queues one message that passed the checks and starts sending
// it. The entry comes back failed when the phone refuses the number at
// once (RefuseNumbers).
func (s *Server) accept(
	identifier, number, body string, parts, sim int, group string,
) *OutboxEntry {
	entry := &OutboxEntry{
		RequestID: identifier, To: number, Parts: parts, Status: "queued",
		Sim: &sim, Source: "api", CreatedAt: time.Now().UnixMilli(),
	}
	s.outbox[identifier] = entry
	s.bodies[identifier] = body
	s.sent = append(s.sent, SentMessage{
		RequestID: identifier, To: number, Body: body, Sim: sim, Group: group,
	})
	s.emit("sms.queued", Object{
		{"requestId", identifier}, {"to", number}, {"parts", parts},
		{"sim", sim}, {"source", "api"},
	})
	if s.refuseNumbers[number] {
		entry.Status = "failed"
		s.emit("sms.failed", Object{
			{"requestId", identifier}, {"id", nil}, {"conversationId", nil},
			{"to", number}, {"errorCode", nil}, {"reason", "send_refused"},
		})
		return entry
	}
	go s.transmit(entry, body)
	return entry
}

func sendResult(entry *OutboxEntry) Object {
	return Object{
		{"requestId", entry.RequestID}, {"to", entry.To},
		{"parts", entry.Parts}, {"status", entry.Status},
	}
}

var statusOrder = []string{"queued", "sending", "sent", "delivered"}

// groupResult builds the GroupSendResult of members as the app does.
func groupResult(identifier string, members []*OutboxEntry) Object {
	status := "failed"
	rank := len(statusOrder)
	numbers := make([]string, 0, len(members))
	results := make([]Object, 0, len(members))
	for _, member := range members {
		numbers = append(numbers, member.To)
		results = append(results, sendResult(member))
		if member.Status == "failed" {
			continue
		}
		for index, candidate := range statusOrder {
			if candidate == member.Status && index < rank {
				status, rank = candidate, index
			}
		}
	}
	return Object{
		{"requestId", identifier}, {"to", numbers},
		{"parts", members[0].Parts}, {"status", status},
		{"messages", results},
	}
}

// knownGroup returns the members of a group sent before, oldest first.
func (s *Server) knownGroup(identifier string) []*OutboxEntry {
	var members []*OutboxEntry
	for index := 1; ; index++ {
		member := s.outbox[fmt.Sprintf("%s.%d", identifier, index)]
		if member == nil {
			return members
		}
		members = append(members, member)
	}
}

func (s *Server) messagesSendGroup(request map[string]any) (any, *failure) {
	rawTo, present := request["to"]
	if !present || rawTo == nil {
		return nil, fail("invalid_request", "The field 'to' is missing.")
	}
	items, isList := rawTo.([]any)
	if !isList {
		return nil, fail("invalid_request", "The field 'to' is malformed.")
	}
	if len(items) == 0 {
		return nil, fail("invalid_request", "'to' is missing.")
	}
	body, err := optionalText(request, "body")
	if err != nil {
		return nil, err
	}
	if body == nil {
		return nil, fail("invalid_request", "The field 'body' is missing.")
	}
	simChoice, err := optionalInt(request, "sim")
	if err != nil {
		return nil, err
	}
	requestID, err := optionalText(request, "requestId")
	if err != nil {
		return nil, err
	}
	var numbers []string
	for _, item := range items {
		text, isText := item.(string)
		if !isText {
			return nil, fail("invalid_request", "'to' must contain strings.")
		}
		number := NormalizeNumber(text)
		if !numberPattern.MatchString(number) {
			return nil, fail("invalid_number", fmt.Sprintf(
				"'to' contains '%s', which is not a phone number.", text))
		}
		known := false
		for _, other := range numbers {
			known = known || samePeer(other, number)
		}
		if !known {
			numbers = append(numbers, number)
		}
	}
	if len(numbers) > 20 {
		return nil, fail("invalid_request",
			"A group message goes to at most 20 recipients.")
	}
	if *body == "" {
		return nil, fail("invalid_request", "'body' is empty.")
	}
	parts := CountParts(*body)
	if parts > 10 {
		return nil, fail("message_too_long", fmt.Sprintf(
			"The message needs %d SMS parts; at most 10 are allowed.", parts))
	}
	if requestID != nil &&
		(!requestIDPattern.MatchString(*requestID) || len(*requestID) > 61) {
		return nil, fail("invalid_request",
			"'requestId' of a group must be 1 to 61 characters of "+
				"A-Z a-z 0-9 . _ -.")
	}
	identifier := requestIDOrRandom(requestID)
	if known := s.knownGroup(identifier); len(known) > 0 {
		same := len(known) == len(numbers)
		for _, member := range known {
			same = same && s.bodies[member.RequestID] == *body
		}
		for _, number := range numbers {
			found := false
			for _, member := range known {
				found = found || samePeer(member.To, number)
			}
			same = same && found
		}
		if !same {
			return nil, fail("invalid_request",
				"'requestId' was already used for another message.")
		}
		return groupResult(identifier, known), nil
	}
	if total := parts * len(numbers); total > 20 {
		return nil, fail("invalid_request", fmt.Sprintf(
			"This group needs %d SMS parts; at most 20 can be sent per "+
				"minute. Send to fewer recipients or shorten the text.", total))
	}
	chosen, err := s.subscription(simChoice)
	if err != nil {
		return nil, err
	}
	members := make([]*OutboxEntry, 0, len(numbers))
	for index, number := range numbers {
		members = append(members, s.accept(
			fmt.Sprintf("%s.%d", identifier, index+1), number, *body, parts,
			chosen, identifier))
	}
	return groupResult(identifier, members), nil
}

func (s *Server) transmit(entry *OutboxEntry, body string) {
	delay := s.options.SendDelay
	s.mu.Lock()
	number := entry.To
	fails := s.failNumbers[number]
	long := entry.Parts > 1
	s.mu.Unlock()
	if long && !fails {
		// Some parts are sent, some are left.
		time.Sleep(delay / 2)
		s.mu.Lock()
		entry.Status, entry.PartsSent = "sending", 1
		s.mu.Unlock()
		time.Sleep(delay / 2)
	} else {
		time.Sleep(delay)
	}
	s.mu.Lock()
	if s.failNumbers[number] {
		// The phone has not stored the message: no id, no conversation.
		code := 1
		entry.Status, entry.ErrorCode = "failed", &code
		s.emit("sms.failed", Object{
			{"requestId", entry.RequestID}, {"id", nil},
			{"conversationId", nil}, {"to", number},
			{"errorCode", 1}, {"reason", "generic_failure"},
		})
		s.mu.Unlock()
		return
	}
	message := s.addMessage(NewMessage{
		Address: number, Body: body, Direction: "out", Sim: *entry.Sim,
		RequestID: entry.RequestID,
	})
	entry.Status, entry.PartsSent = "sent", entry.Parts
	entry.MessageID = &message.ID
	s.emit("sms.sent", Object{
		{"requestId", entry.RequestID},
		{"conversationId", message.ConversationID},
		{"to", number}, {"message", message},
	})
	s.mu.Unlock()
	if !s.options.DeliveryReports {
		return
	}
	time.Sleep(delay)
	s.mu.Lock()
	defer s.mu.Unlock()
	entry.Status = "delivered"
	message.Status = "delivered"
	s.emit("sms.delivered", Object{
		{"requestId", entry.RequestID}, {"id", message.ID},
		{"conversationId", message.ConversationID}, {"to", number},
	})
}

func (s *Server) subscribe(
	connected *client, request map[string]any,
) (any, *failure) {
	// subscribe adds the names; its address replaces the filter, and
	// without address (or with null) the filter is removed.
	names, err := eventNames(request)
	if err != nil {
		return nil, err
	}
	address, err := optionalText(request, "address")
	if err != nil {
		return nil, err
	}
	for _, name := range names {
		connected.events[name] = true
	}
	connected.address = address
	return subscription(connected), nil
}

func (s *Server) unsubscribe(
	connected *client, request map[string]any,
) (any, *failure) {
	names, err := eventNames(request)
	if err != nil {
		return nil, err
	}
	for _, name := range names {
		delete(connected.events, name)
	}
	if len(connected.events) == 0 {
		connected.address = nil
	}
	return subscription(connected), nil
}

// ---------------------------------------------------------------------
// Data
// ---------------------------------------------------------------------

func (s *Server) addMessage(added NewMessage) *Message {
	timestamp := added.Timestamp
	if timestamp == 0 {
		timestamp = time.Now().UnixMilli()
	}
	direction := added.Direction
	if direction == "" {
		direction = "in"
	}
	incoming := direction == "in"
	subscriptionID := added.Sim
	if subscriptionID == 0 {
		subscriptionID = 1
	}
	s.nextMessageID++
	message := &Message{
		ID:             s.nextMessageID,
		ConversationID: s.conversationID(added.Address),
		Address:        added.Address,
		Contact:        s.contactName(added.Address),
		Direction:      direction,
		Body:           added.Body,
		Timestamp:      timestamp,
		Read:           !added.Unread,
		Status:         "sent",
		Sim:            &subscriptionID,
	}
	if incoming {
		sentAt := timestamp - 2000
		message.SentTimestamp = &sentAt
		message.Status = "received"
	}
	if added.RequestID != "" {
		message.RequestID = &added.RequestID
	}
	s.messages = append(s.messages, message)
	return message
}

func (s *Server) emit(name string, data any) {
	s.sequence++
	encoded := compact(data)
	frame := compact(Object{
		{"event", name}, {"seq", s.sequence},
		{"data", json.RawMessage(encoded)},
	})
	var fields map[string]any
	json.Unmarshal(encoded, &fields)
	for _, connected := range s.clients {
		if connected.authenticated && wants(connected, name, fields) {
			s.send(connected, frame)
		}
	}
}

func (s *Server) conversationID(address string) int64 {
	for index, known := range s.conversationKeys {
		if samePeer(known, address) {
			return int64(170 + index)
		}
	}
	s.conversationKeys = append(s.conversationKeys, address)
	return int64(170 + len(s.conversationKeys) - 1)
}

func (s *Server) contactName(address string) *string {
	for _, candidate := range s.contacts {
		for _, number := range candidate.Numbers {
			if samePeer(number.Number, address) {
				name := candidate.Name
				return &name
			}
		}
	}
	return nil
}

func newer(message, than *Message) bool {
	if than == nil {
		return true
	}
	if message.Timestamp != than.Timestamp {
		return message.Timestamp > than.Timestamp
	}
	return message.ID > than.ID
}

func (s *Server) conversations() []conversation {
	newest := make(map[int64]*Message)
	counts := make(map[int64]int)
	unread := make(map[int64]int)
	var order []int64
	for _, message := range s.messages {
		identifier := message.ConversationID
		if _, known := newest[identifier]; !known {
			order = append(order, identifier)
		}
		if newer(message, newest[identifier]) {
			newest[identifier] = message
		}
		counts[identifier]++
		if !message.Read {
			unread[identifier]++
		}
	}
	items := make([]conversation, 0, len(order))
	for _, identifier := range order {
		latest := newest[identifier]
		snippet := []rune(latest.Body)
		if len(snippet) > 160 {
			snippet = snippet[:160]
		}
		items = append(items, conversation{
			ID:           identifier,
			Address:      latest.Address,
			Contact:      latest.Contact,
			Snippet:      string(snippet),
			Timestamp:    latest.Timestamp,
			Direction:    latest.Direction,
			MessageCount: counts[identifier],
			UnreadCount:  unread[identifier],
			Sim:          latest.Sim,
		})
	}
	sort.SliceStable(items, func(a, b int) bool {
		if items[a].Timestamp != items[b].Timestamp {
			return items[a].Timestamp > items[b].Timestamp
		}
		return items[a].ID > items[b].ID
	})
	return items
}

// NormalizeNumber removes the characters that are ignored in phone
// numbers: spaces, dashes, dots, slashes and brackets.
func NormalizeNumber(number string) string {
	return ignoredInNumbers.ReplaceAllString(strings.TrimSpace(number), "")
}

func isNumber(address string) bool {
	return digitsPattern.MatchString(NormalizeNumber(address))
}

// addressKey returns a key that is equal for equal addresses where that
// is known: the international form for numbers written with + or 00, the
// bare digits for other numbers, lower case for sender names.
func addressKey(address string) string {
	if !isNumber(address) {
		return strings.ToLower(strings.TrimSpace(address))
	}
	digits := NormalizeNumber(address)
	if strings.HasPrefix(digits, "00") && len(digits) > 4 {
		return "+" + digits[2:]
	}
	return digits
}

// samePeer reports whether two addresses mean the same peer ("Phone
// numbers" in API.md).
func samePeer(first, second string) bool {
	a, b := addressKey(first), addressKey(second)
	if a == b {
		return true
	}
	if !isNumber(first) || !isNumber(second) {
		return false
	}
	international := func(key string) bool { return strings.HasPrefix(key, "+") }
	if international(a) && international(b) {
		return false
	}
	ends := func(full, national string) bool {
		significant := strings.TrimLeft(national, "0")
		return len(significant) >= 7 && strings.HasSuffix(full, significant)
	}
	switch {
	case international(a):
		return ends(a, b)
	case international(b):
		return ends(b, a)
	}
	return ends(a, b) || ends(b, a)
}

// CountParts returns the number of SMS parts of a text (simplified
// alphabet test).
func CountParts(body string) int {
	single, multi := 160, 153
	for _, character := range body {
		if character >= 128 {
			single, multi = 70, 67
			break
		}
	}
	length := utf8.RuneCountInString(body)
	if length <= single {
		return 1
	}
	return (length + multi - 1) / multi
}

func malformed(name string) *failure {
	return fail("invalid_request", "The field '"+name+"' is malformed.")
}

func optionalText(request map[string]any, name string) (*string, *failure) {
	switch value := request[name].(type) {
	case nil:
		return nil, nil
	case string:
		return &value, nil
	}
	return nil, malformed(name)
}

func optionalBool(request map[string]any, name string) (bool, *failure) {
	switch value := request[name].(type) {
	case nil:
		return false, nil
	case bool:
		return value, nil
	}
	return false, malformed(name)
}

func optionalInt(request map[string]any, name string) (*int64, *failure) {
	switch value := request[name].(type) {
	case nil:
		return nil, nil
	case float64:
		if value == math.Trunc(value) {
			whole := int64(value)
			return &whole, nil
		}
	}
	return nil, malformed(name)
}

func requiredInt(request map[string]any, name string) (int64, *failure) {
	value, err := optionalInt(request, name)
	if err != nil {
		return 0, err
	}
	if value == nil {
		return 0, fail("invalid_request", "The field '"+name+"' is missing.")
	}
	return *value, nil
}

// eventNames returns the event names of a request, with * replaced.
func eventNames(request map[string]any) ([]string, *failure) {
	list, isList := request["events"].([]any)
	if !isList || len(list) == 0 {
		return nil, fail("invalid_request", "'events' is missing.")
	}
	names := make([]string, 0, len(list))
	every := false
	for _, entry := range list {
		name, isText := entry.(string)
		if !isText {
			return nil, fail("invalid_request", "'events' is missing.")
		}
		every = every || name == "*"
		names = append(names, name)
	}
	if every {
		return EventNames, nil
	}
	for _, name := range names {
		if !contains(EventNames, name) {
			return nil, fail("invalid_request", "Unknown event '"+name+"'.")
		}
	}
	return names, nil
}

func contains(list []string, wanted string) bool {
	for _, entry := range list {
		if entry == wanted {
			return true
		}
	}
	return false
}

func subscription(connected *client) Object {
	events := []string{}
	for _, name := range EventNames {
		if connected.events[name] {
			events = append(events, name)
		}
	}
	return Object{{"events", events}, {"address", connected.address}}
}

// wants reports whether a client subscribed to an event and its filter
// lets it pass.
//
// The filter applies to sms.* events that name a peer. Events whose
// number is not known are always delivered.
func wants(connected *client, name string, data map[string]any) bool {
	if !connected.events[name] {
		return false
	}
	if connected.address == nil || !strings.HasPrefix(name, "sms.") {
		return true
	}
	peer := ""
	if message, isObject := data["message"].(map[string]any); isObject {
		peer, _ = message["address"].(string)
	}
	if peer == "" {
		peer, _ = data["to"].(string)
	}
	if peer == "" {
		peer, _ = data["address"].(string)
	}
	if peer == "" {
		return true
	}
	return samePeer(peer, *connected.address)
}

// page cuts one page out of a list. With keys the cursor is the key of
// the last item of the previous page, without keys its index.
func page[T any](
	items []T, keys []string, request map[string]any,
) (any, *failure) {
	limit := 50
	if raw, present := request["limit"]; present {
		value, isNumber := raw.(float64)
		if !isNumber || value != math.Trunc(value) || value < 1 || value > 200 {
			return nil, fail(
				"invalid_request", "'limit' must be between 1 and 200.")
		}
		limit = int(value)
	}
	cursor, err := optionalText(request, "cursor")
	if err != nil {
		return nil, err
	}
	start := 0
	if cursor != nil && keys == nil {
		index, parseErr := strconv.Atoi(*cursor)
		if parseErr != nil || index < 0 {
			return nil, fail("invalid_request", "'cursor' is invalid.")
		}
		start = index
	} else if cursor != nil {
		start = -1
		for index, key := range keys {
			if key == *cursor {
				start = index + 1
				break
			}
		}
		if start < 0 {
			return nil, fail("invalid_request", "'cursor' is invalid.")
		}
	}
	start = min(start, len(items))
	end := min(start+limit, len(items))
	chunk := items[start:end]
	if chunk == nil {
		chunk = []T{}
	}
	var next any
	if len(chunk) > 0 && start+limit < len(items) {
		if keys == nil {
			next = strconv.Itoa(end)
		} else {
			next = keys[end-1]
		}
	}
	return Object{{"items", chunk}, {"nextCursor", next}}, nil
}
