// Command fakeserver runs the fake SMS Dock server, to try the tool and
// the examples without a phone:
//
//	go run ./internal/cmd/fakeserver --port 9443 --incoming-every 15
//
// It prints its URL, fingerprint and password.
package main

import (
	"flag"
	"fmt"
	"os"
	"os/signal"
	"time"

	"github.com/pezi/sms_dock/go/internal/fakeserver"
)

func main() {
	host := flag.String("host", "127.0.0.1", "address to listen on")
	port := flag.Int("port", 9443, "port to listen on")
	password := flag.String("password", "secret123",
		"password of the fake server (test value only)")
	every := flag.Float64("incoming-every", 0,
		"let an SMS arrive every so many seconds")
	flag.Parse()

	server, err := fakeserver.Start(&fakeserver.Options{
		Password: *password, Host: *host, Port: *port,
		SendDelay: 500 * time.Millisecond, DeliveryReports: true,
	})
	if err != nil {
		fmt.Fprintln(os.Stderr, "fakeserver:", err)
		os.Exit(1)
	}
	defer server.Stop()
	server.SeedDemoData()
	fmt.Println("URL:        ", server.URL())
	fmt.Println("Fingerprint:", server.Fingerprint())
	fmt.Println("Password:   ", *password)
	fmt.Println("Press Ctrl+C to stop.")

	interrupt := make(chan os.Signal, 1)
	signal.Notify(interrupt, os.Interrupt)
	var arrivals <-chan time.Time
	if *every > 0 {
		ticker := time.NewTicker(time.Duration(*every * float64(time.Second)))
		defer ticker.Stop()
		arrivals = ticker.C
	}
	for number := 1; ; number++ {
		select {
		case <-interrupt:
			fmt.Println()
			return
		case <-arrivals:
			server.ReceiveSMS(
				"+436641234567", fmt.Sprintf("Test message %d", number))
		}
	}
}
