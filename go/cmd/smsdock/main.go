// Command smsdock reads, sends and watches SMS through the SMS Dock app.
//
//	smsdock <command> <wss-url> [options]
//
// Run "smsdock --help" for the commands. The password is read from the
// environment variable SMSDOCK_PASSWORD, never from the command line.
package main

import (
	"context"
	"os"
	"os/signal"

	"github.com/pezi/sms_dock/go/internal/cli"
)

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	code := cli.Main(ctx, os.Args[1:])
	stop()
	os.Exit(code)
}
