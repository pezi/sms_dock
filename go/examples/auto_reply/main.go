// Command auto_reply is a hook that answers incoming SMS.
//
// An SMS with the text STATUS gets the answer "All systems running.";
// everything else gets a short help text.
//
// An answering machine can end up in a loop, for example with another
// answering machine or with a service that answers every SMS. Three
// guards prevent that:
//
//  1. A number gets at most one answer in cooldownMinutes.
//  2. Senders that are not ordinary phone numbers get no answer: names
//     such as "A1" or "Post" and short codes such as 8810.
//  3. At most maxRepliesPerHour answers are sent in total.
//
// Usage:
//
//	export SMSDOCK_PASSWORD='your password'
//	export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//	go run ./examples/auto_reply wss://192.168.1.50:9443/ws
//
// Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.
package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/signal"
	"time"

	smsdock "github.com/pezi/sms_dock/go"
)

const usage = `Hook that answers incoming SMS.

An SMS with the text STATUS gets the answer "All systems running.";
everything else gets a short help text.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    go run ./examples/auto_reply wss://192.168.1.50:9443/ws

Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.
`

func run(ctx context.Context) (int, error) {
	if len(os.Args) != 2 {
		fmt.Print(usage)
		return 2, nil
	}

	guard := newReplyGuard(cooldownMinutes*time.Minute, maxRepliesPerHour)
	dock, err := smsdock.Dial(ctx, os.Args[1], &smsdock.Options{
		ReconnectAttempts: smsdock.ReconnectForever,
	})
	if err != nil {
		return 1, err
	}
	defer dock.Close()
	if _, err := dock.Subscribe(ctx, "sms.received"); err != nil {
		return 1, err
	}
	fmt.Println("Answering SMS. Press Ctrl+C to stop.")

	// SMS that arrive while the connection is down get no answer: the
	// phone does not send events again, and a late answer helps nobody.
	// listen_incoming shows how to read such messages.
	for event, err := range dock.Events(ctx, 0) {
		if err != nil {
			return 1, err
		}
		message := event.Data.Message
		if message == nil {
			continue
		}
		sender := message.Address
		if refusal := guard.refusal(sender, time.Now()); refusal != "" {
			fmt.Printf("%s: no answer (%s)\n", sender, refusal)
			continue
		}
		// Answer on the SIM that received the message.
		result, err := dock.Send(ctx, sender, answerFor(message.Body),
			&smsdock.SendOptions{Sim: message.Sim})
		var refused *smsdock.APIError
		if errors.As(err, &refused) {
			fmt.Printf("%s: not answered, %s: %s\n",
				sender, refused.Code, refused.Message)
			continue
		}
		if err != nil {
			return 1, err
		}
		fmt.Printf("%s: answered, requestId %s\n", sender, result.RequestID)
	}
	return 0, nil
}

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	code, err := run(ctx)
	stop()
	if errors.Is(err, context.Canceled) {
		// Ctrl+C
		os.Exit(0)
	}
	if err != nil {
		fmt.Fprintf(os.Stderr, "auto_reply: %v\n", err)
	}
	os.Exit(code)
}
