package smsdock

import (
	"context"
	"crypto/tls"
	"errors"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

const (
	// keepAliveInterval is the time between two pings of the client.
	keepAliveInterval = 20 * time.Second
	// idleTimeout closes a connection on which nothing arrived for so
	// long, not even a ping or the answer to one.
	idleTimeout = 45 * time.Second
	// controlTimeout is the time to write a ping, pong or close frame.
	controlTimeout = 5 * time.Second
	// inboundBuffer is the number of frames read ahead of the caller.
	inboundBuffer = 256
	// closeCodeWait is the time to wait for the close frame of the server
	// after a write failed.
	closeCodeWait = time.Second
)

// errReceiveTimeout says that no frame arrived in time. The connection
// stays usable.
var errReceiveTimeout = errors.New("timed out")

// inbound is one frame read from the WebSocket.
type inbound struct {
	text bool
	data []byte
}

// connection is one open WebSocket.
//
// A goroutine reads frames into a channel, so that the caller can wait
// for a frame with a timeout (a gorilla connection is unusable after a
// read deadline passed) and pings of the server are answered while the
// caller does something else. A second goroutine sends pings. Only the
// goroutine of the caller writes data frames.
type connection struct {
	socket *websocket.Conn
	frames chan inbound
	// done is closed by close, dead by the reading goroutine when it
	// ends; readErr is valid after that.
	done      chan struct{}
	dead      chan struct{}
	readErr   error
	closeOnce sync.Once
}

// dial opens the WebSocket. With a pinned digest the certificate is
// compared during the TLS handshake; a mismatch is returned as a
// CertificateMismatchError before the WebSocket request is written.
func dial(
	ctx context.Context, url string, pinned []byte, timeout time.Duration,
) (*connection, error) {
	var mismatch *CertificateMismatchError
	dialer := websocket.Dialer{
		// No proxy: the phone is in the local network.
		Proxy:            nil,
		HandshakeTimeout: timeout,
		TLSClientConfig:  tlsConfig(pinned, &mismatch),
	}
	socket, response, err := dialer.DialContext(ctx, url, nil)
	if response != nil && response.Body != nil {
		response.Body.Close()
	}
	if mismatch != nil {
		if socket != nil {
			socket.Close()
		}
		return nil, mismatch
	}
	if err != nil {
		if contextErr := ctx.Err(); contextErr != nil {
			return nil, contextErr
		}
		return nil, &transportError{err: err}
	}
	socket.SetReadLimit(MaxIncomingBytes)
	link := &connection{
		socket: socket,
		frames: make(chan inbound, inboundBuffer),
		done:   make(chan struct{}),
		dead:   make(chan struct{}),
	}
	extend := func() {
		socket.SetReadDeadline(time.Now().Add(idleTimeout))
	}
	socket.SetPongHandler(func(string) error {
		extend()
		return nil
	})
	socket.SetPingHandler(func(data string) error {
		extend()
		// A pong that cannot be written shows up as a failed read or
		// write soon enough.
		socket.WriteControl(
			websocket.PongMessage, []byte(data),
			time.Now().Add(controlTimeout),
		)
		return nil
	})
	extend()
	go link.read(extend)
	go link.keepAlive()
	return link, nil
}

// read moves frames from the WebSocket into the channel until the
// connection fails or is closed.
func (l *connection) read(extend func()) {
	defer close(l.dead)
	for {
		kind, data, err := l.socket.ReadMessage()
		if err != nil {
			l.readErr = err
			return
		}
		extend()
		frame := inbound{text: kind == websocket.TextMessage, data: data}
		select {
		case l.frames <- frame:
			// The caller may have been slow to take the frame; that is
			// no silence of the server.
			extend()
		case <-l.done:
			l.readErr = errors.New("the connection is closed")
			return
		}
	}
}

// keepAlive sends a ping every keepAliveInterval. The answer extends the
// read deadline; without one the reading goroutine fails after
// idleTimeout.
func (l *connection) keepAlive() {
	ticker := time.NewTicker(keepAliveInterval)
	defer ticker.Stop()
	for {
		select {
		case <-ticker.C:
			l.socket.WriteControl(
				websocket.PingMessage, nil, time.Now().Add(controlTimeout),
			)
		case <-l.done:
			return
		case <-l.dead:
			return
		}
	}
}

// tlsState returns the state of the TLS connection below the WebSocket.
func (l *connection) tlsState() (tls.ConnectionState, bool) {
	secure, isTLS := l.socket.UnderlyingConn().(*tls.Conn)
	if !isTLS {
		return tls.ConnectionState{}, false
	}
	return secure.ConnectionState(), true
}

// send writes one text frame.
func (l *connection) send(frame []byte, timeout time.Duration) error {
	l.socket.SetWriteDeadline(time.Now().Add(timeout))
	err := l.socket.WriteMessage(websocket.TextMessage, frame)
	if err == nil {
		return nil
	}
	// The write may have failed because the server closed the
	// connection. Its close frame tells why.
	select {
	case <-l.dead:
		return l.failure()
	case <-time.After(closeCodeWait):
		return &transportError{err: err}
	}
}

// receive returns the next frame. It waits until the deadline (a zero
// deadline means without limit) and returns errReceiveTimeout after it, a
// transportError when the connection failed and the error of the context
// when that is done.
func (l *connection) receive(
	ctx context.Context, deadline time.Time,
) (inbound, error) {
	select {
	case frame := <-l.frames:
		return frame, nil
	default:
	}
	var expired <-chan time.Time
	if !deadline.IsZero() {
		wait := time.Until(deadline)
		if wait <= 0 {
			return inbound{}, errReceiveTimeout
		}
		timer := time.NewTimer(wait)
		defer timer.Stop()
		expired = timer.C
	}
	select {
	case frame := <-l.frames:
		return frame, nil
	case <-l.dead:
		// Frames that arrived before the failure come first.
		select {
		case frame := <-l.frames:
			return frame, nil
		default:
		}
		return inbound{}, l.failure()
	case <-expired:
		return inbound{}, errReceiveTimeout
	case <-ctx.Done():
		return inbound{}, ctx.Err()
	}
}

// failure returns the error of the reading goroutine as a transportError
// with the close frame of the server, if there was one. Call it only
// after dead is closed.
func (l *connection) failure() *transportError {
	failure := &transportError{err: l.readErr}
	var closed *websocket.CloseError
	if errors.As(l.readErr, &closed) {
		switch closed.Code {
		case websocket.CloseAbnormalClosure, websocket.CloseNoStatusReceived:
			// No close frame, or one without a code.
		default:
			failure.closeCode = closed.Code
			failure.closeReason = closed.Text
		}
	}
	return failure
}

// close ends the connection with the close code 1000.
func (l *connection) close() {
	l.closeOnce.Do(func() {
		close(l.done)
		l.socket.WriteControl(
			websocket.CloseMessage,
			websocket.FormatCloseMessage(websocket.CloseNormalClosure, ""),
			time.Now().Add(time.Second),
		)
		l.socket.Close()
	})
}
