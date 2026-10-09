// Command send_sms sends one SMS and waits for the result.
//
// Usage:
//
//	export SMSDOCK_PASSWORD='your password'
//	export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//	go run ./examples/send_sms wss://192.168.1.50:9443/ws +4369912345678 "Hello"
package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"time"

	smsdock "github.com/pezi/sms_dock/go"
)

const usage = `Send one SMS and wait for the result.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    go run ./examples/send_sms wss://192.168.1.50:9443/ws +4369912345678 "Hello"
`

func run() (int, error) {
	if len(os.Args) != 4 {
		fmt.Print(usage)
		return 2, nil
	}
	address, number, text := os.Args[1], os.Args[2], os.Args[3]
	ctx := context.Background()

	// Password and fingerprint come from the environment.
	dock, err := smsdock.Dial(ctx, address, nil)
	if err != nil {
		return 1, err
	}
	defer dock.Close()

	// Send returns as soon as the phone has queued the message. The
	// client adds a random request id, so a repeat after a lost
	// connection cannot send the message twice. Pass your own
	// SendOptions.RequestID if the whole program may run again.
	result, err := dock.Send(ctx, number, text, nil)
	var refused *smsdock.APIError
	if errors.As(err, &refused) {
		// For example invalid_number, message_too_long, quota_exceeded.
		fmt.Printf("Not sent: %s: %s\n", refused.Code, refused.Message)
		return 1, nil
	}
	if err != nil {
		return 1, err
	}
	fmt.Printf("%d part(s) for %s\n", result.Parts, result.To)

	// Wait until the network accepted the message or refused it.
	outcome, err := dock.WaitForResult(
		ctx, result.RequestID, &smsdock.WaitOptions{Timeout: time.Minute})
	if err != nil {
		return 1, err
	}
	if outcome.Status == "failed" {
		code := "unknown"
		if outcome.ErrorCode != nil {
			code = fmt.Sprint(*outcome.ErrorCode)
		}
		fmt.Printf("Failed, error code %s\n", code)
		return 1, nil
	}
	if outcome.MessageID == nil {
		// The phone has not stored the message yet.
		fmt.Println("Sent")
		return 0, nil
	}
	fmt.Printf("Sent, message id %d\n", *outcome.MessageID)
	return 0, nil
}

func main() {
	code, err := run()
	if err != nil {
		fmt.Fprintf(os.Stderr, "send_sms: %v\n", err)
	}
	os.Exit(code)
}
