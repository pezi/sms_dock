// Command send_group_sms sends one text to several numbers, one SMS each,
// and waits for the results.
//
// Usage:
//
//	export SMSDOCK_PASSWORD='your password'
//	export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//	go run ./examples/send_group_sms wss://192.168.1.50:9443/ws "Hello all" +4369912345678 +436641112223
package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"time"

	smsdock "github.com/pezi/sms_dock/go"
)

const usage = `Send one text to several numbers, one SMS each, and wait for the results.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    go run ./examples/send_group_sms wss://192.168.1.50:9443/ws "Hello all" \
        +4369912345678 +436641112223
`

func run() (int, error) {
	if len(os.Args) < 4 {
		fmt.Print(usage)
		return 2, nil
	}
	address, text, numbers := os.Args[1], os.Args[2], os.Args[3:]
	ctx := context.Background()

	// Password and fingerprint come from the environment.
	dock, err := smsdock.Dial(ctx, address, nil)
	if err != nil {
		return 1, err
	}
	defer dock.Close()

	// SendGroup returns as soon as the phone has queued one SMS per
	// recipient. The phone checks the whole group first: one bad number
	// refuses all of them, nothing is sent then. The client adds a random
	// request id; the members get the ids requestId.1, requestId.2 and so
	// on.
	result, err := dock.SendGroup(ctx, numbers, text, nil)
	var refused *smsdock.APIError
	if errors.As(err, &refused) {
		// For example invalid_number, message_too_long, quota_exceeded.
		fmt.Printf("Not sent: %s: %s\n", refused.Code, refused.Message)
		return 1, nil
	}
	if err != nil {
		return 1, err
	}
	fmt.Printf("%d recipient(s), %d part(s) each\n", len(result.To), result.Parts)

	// Wait for every member; a member the phone refused at once is
	// already failed and needs no waiting.
	failed := 0
	for _, member := range result.Messages {
		if member.Status == "failed" {
			fmt.Printf("%s: refused by the phone\n", member.To)
			failed++
			continue
		}
		outcome, err := dock.WaitForResult(
			ctx, member.RequestID, &smsdock.WaitOptions{Timeout: time.Minute})
		if err != nil {
			return 1, err
		}
		switch {
		case outcome.Status == "failed":
			code := "unknown"
			if outcome.ErrorCode != nil {
				code = fmt.Sprint(*outcome.ErrorCode)
			}
			fmt.Printf("%s: failed, error code %s\n", member.To, code)
			failed++
		case outcome.MessageID == nil:
			fmt.Printf("%s: %s\n", member.To, outcome.Status)
		default:
			fmt.Printf("%s: %s, message id %d\n",
				member.To, outcome.Status, *outcome.MessageID)
		}
	}
	if failed > 0 {
		return 1, nil
	}
	return 0, nil
}

func main() {
	code, err := run()
	if err != nil {
		fmt.Fprintf(os.Stderr, "send_group_sms: %v\n", err)
	}
	os.Exit(code)
}
