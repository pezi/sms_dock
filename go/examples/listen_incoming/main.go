// Command listen_incoming is a hook for incoming SMS: it calls a function
// for every SMS that arrives.
//
// Usage:
//
//	export SMSDOCK_PASSWORD='your password'
//	export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
//	go run ./examples/listen_incoming wss://192.168.1.50:9443/ws
//
// Stop with Ctrl+C.
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

const usage = `Hook for incoming SMS: call a function for every SMS that arrives.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    go run ./examples/listen_incoming wss://192.168.1.50:9443/ws

Stop with Ctrl+C.
`

// Ids of the messages the hook has seen, and the time of the newest one.
var (
	seen   = make(map[int64]bool)
	newest int64
)

// onSMS is your hook. message is a Message object of API.md.
func onSMS(message *smsdock.Message) {
	// Timestamps are milliseconds since the epoch; Time() shows local
	// time.
	fmt.Printf("%s  %s: %s\n",
		message.Time().Format("15:04:05"), message.Sender(), message.Body)
}

// handle calls the hook once per message.
func handle(message *smsdock.Message) {
	if message == nil {
		return
	}
	if message.ID != nil {
		if seen[*message.ID] {
			return
		}
		seen[*message.ID] = true
	}
	newest = max(newest, message.Timestamp)
	onSMS(message)
}

// catchUp reads what arrived while the connection was down.
//
// The phone does not send events again. The client calls this function
// after every reconnect, when the subscription is in force again.
func catchUp(ctx context.Context, dock *smsdock.Client) {
	missedCount, known := dock.MissedEvents()
	if !dock.ServerRestarted() && known && missedCount == 0 {
		return
	}
	// Messages of the same millisecond come again; handle knows them.
	var missed []smsdock.Message
	for message, err := range dock.IterMessages(ctx, smsdock.MessageFilter{
		Direction: "in", Since: time.UnixMilli(newest),
	}, smsdock.IterOptions{}) {
		if err != nil {
			fmt.Fprintf(os.Stderr, "listen_incoming: %v\n", err)
			return
		}
		missed = append(missed, message)
	}
	for index := len(missed) - 1; index >= 0; index-- { // oldest first
		handle(&missed[index])
	}
}

func run(ctx context.Context) (int, error) {
	if len(os.Args) != 2 {
		fmt.Print(usage)
		return 2, nil
	}

	// Password and fingerprint come from the environment.
	dock, err := smsdock.Dial(ctx, os.Args[1], &smsdock.Options{
		// Keep trying when the phone is out of reach for a while.
		ReconnectAttempts: smsdock.ReconnectForever,
		OnReconnect: func(dock *smsdock.Client) {
			catchUp(ctx, dock)
		},
	})
	if err != nil {
		return 1, err
	}
	defer dock.Close()

	// Only messages from now on: remember the time of the phone.
	now, err := dock.Ping(ctx)
	if err != nil {
		return 1, err
	}
	newest = now.Time
	// A new connection gets no events. Ask for the ones you need.
	if _, err := dock.Subscribe(ctx, "sms.received"); err != nil {
		return 1, err
	}
	fmt.Println("Waiting for SMS. Press Ctrl+C to stop.")

	// Events yields forever. After a lost connection it reconnects, logs
	// in and subscribes again, then calls catchUp.
	for event, err := range dock.Events(ctx, 0) {
		if err != nil {
			return 1, err
		}
		handle(event.Data.Message)
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
		fmt.Fprintf(os.Stderr, "listen_incoming: %v\n", err)
	}
	os.Exit(code)
}
