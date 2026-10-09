package main

import (
	"fmt"
	"testing"
	"time"
)

func TestOnlyPhoneNumbersGetAnAnswer(t *testing.T) {
	guard := newReplyGuard(10*time.Minute, 20)
	now := time.Now()
	for _, sender := range []string{"A1", "Post", "8810", "MyBank", "", "+43"} {
		if got := guard.refusal(sender, now); got != "not a phone number" {
			t.Fatalf("%q: got %q", sender, got)
		}
	}
	for _, sender := range []string{"+4369912345678", "0699 123 45 67", "(0699) 765-43.21"} {
		if got := guard.refusal(sender, now); got != "" {
			t.Fatalf("%q: got %q", sender, got)
		}
	}
}

func TestOneAnswerPerCooldown(t *testing.T) {
	guard := newReplyGuard(10*time.Minute, 20)
	start := time.Now()
	if got := guard.refusal("+4369912345678", start); got != "" {
		t.Fatalf("got %q", got)
	}
	// The same number, written differently, a little later.
	later := start.Add(9 * time.Minute)
	if got := guard.refusal("+43 699 1234 5678", later); got != "answered a short time ago" {
		t.Fatalf("got %q", got)
	}
	if got := guard.refusal("+4369912345678", start.Add(10*time.Minute)); got != "" {
		t.Fatalf("got %q", got)
	}
}

func TestLimitPerHour(t *testing.T) {
	guard := newReplyGuard(10*time.Minute, 3)
	start := time.Now()
	for index := range 3 {
		sender := fmt.Sprintf("+43699000000%d", index)
		if got := guard.refusal(sender, start.Add(time.Duration(index)*time.Minute)); got != "" {
			t.Fatalf("got %q", got)
		}
	}
	if got := guard.refusal("+436990000009", start.Add(5*time.Minute)); got != "too many answers in the last hour" {
		t.Fatalf("got %q", got)
	}
	// A refused sender is not counted: it gets its answer later.
	if got := guard.refusal("+436990000009", start.Add(61*time.Minute)); got != "" {
		t.Fatalf("got %q", got)
	}
}

func TestAnswers(t *testing.T) {
	for text, want := range map[string]string{
		"STATUS": "All systems running.", " status\n": "All systems running.",
		"Hello": "Commands: STATUS", "": "Commands: STATUS",
	} {
		if got := answerFor(text); got != want {
			t.Fatalf("%q: got %q", text, got)
		}
	}
}
