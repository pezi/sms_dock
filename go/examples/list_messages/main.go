// Command list_messages prints the last 10 incoming SMS.
//
// Usage:
//
//	export SMSDOCK_PASSWORD='your password'
//	export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//	go run ./examples/list_messages wss://192.168.1.50:9443/ws
package main

import (
	"context"
	"fmt"
	"os"

	smsdock "github.com/pezi/sms_dock/go"
)

const usage = `Print the last 10 incoming SMS.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    go run ./examples/list_messages wss://192.168.1.50:9443/ws
`

func run() (int, error) {
	if len(os.Args) != 2 {
		fmt.Print(usage)
		return 2, nil
	}
	ctx := context.Background()

	// The password comes from SMSDOCK_PASSWORD, the fingerprint from
	// SMSDOCK_FINGERPRINT. It pins the self-signed certificate of the phone.
	dock, err := smsdock.Dial(ctx, os.Args[1], nil)
	if err != nil {
		return 1, err
	}
	defer dock.Close()

	page, err := dock.Messages(
		ctx, smsdock.MessageFilter{Direction: "in"},
		smsdock.PageOptions{Limit: 10})
	if err != nil {
		return 1, err
	}
	for _, message := range page.Items {
		// Timestamps are milliseconds since the epoch; Time() shows
		// local time. Sender() is the contact or, without one, the number.
		fmt.Printf("%s  %s: %s\n",
			message.Time().Format("2006-01-02 15:04"), message.Sender(),
			message.Body)
	}

	// page.NextCursor leads to the next 10; see IterMessages for a loop
	// that follows it.
	return 0, nil
}

func main() {
	code, err := run()
	if err != nil {
		fmt.Fprintf(os.Stderr, "list_messages: %v\n", err)
	}
	os.Exit(code)
}
