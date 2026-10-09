// Package smsdock is a synchronous, reconnecting client for the WebSocket
// API of the SMS Dock app, protocol version 1.
//
// The protocol is described in API.md at the top of the repository. The
// client pins the certificate of the phone, reconnects after a lost
// connection, tells what it missed meanwhile and never sends a message
// twice.
//
//	dock, err := smsdock.Dial(ctx, "wss://192.168.1.50:9443/ws", nil)
//	if err != nil {
//		return err
//	}
//	defer dock.Close()
//	status, err := dock.Status(ctx)
//
// Password and fingerprint come from the environment variables
// SMSDOCK_PASSWORD and SMSDOCK_FINGERPRINT unless [Options] names them.
//
// A [Client] is not safe for concurrent use; use one per goroutine.
package smsdock
