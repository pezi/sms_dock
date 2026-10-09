package main

import (
	"regexp"
	"strings"
	"time"
)

const (
	cooldownMinutes   = 10
	maxRepliesPerHour = 20
)

// phoneNumber matches a phone number: optional +, then 7 to 15 digits.
// Shorter ones are short codes; anything with letters is a sender name
// that cannot be answered.
var phoneNumber = regexp.MustCompile(`^\+?[0-9]{7,15}$`)

var ignoredInNumbers = regexp.MustCompile(`[\s\-.()]`)

// replyGuard decides whether a sender may get an answer now.
type replyGuard struct {
	cooldown       time.Duration
	repliesPerHour int
	lastReply      map[string]time.Time
	recent         []time.Time
}

func newReplyGuard(cooldown time.Duration, repliesPerHour int) *replyGuard {
	return &replyGuard{
		cooldown:       cooldown,
		repliesPerHour: repliesPerHour,
		lastReply:      make(map[string]time.Time),
	}
}

// refusal returns why this sender gets no answer, or "" if it gets one.
func (g *replyGuard) refusal(address string, now time.Time) string {
	number := ignoredInNumbers.ReplaceAllString(address, "")
	if !phoneNumber.MatchString(number) {
		return "not a phone number"
	}
	if last, answered := g.lastReply[number]; answered &&
		now.Sub(last) < g.cooldown {
		return "answered a short time ago"
	}
	for len(g.recent) > 0 && now.Sub(g.recent[0]) >= time.Hour {
		g.recent = g.recent[1:]
	}
	if len(g.recent) >= g.repliesPerHour {
		return "too many answers in the last hour"
	}
	// Count the answer before it is sent: a failure must not open the
	// door for a loop.
	g.lastReply[number] = now
	g.recent = append(g.recent, now)
	return ""
}

// answerFor returns the answer to a message text.
func answerFor(text string) string {
	if strings.ToUpper(strings.TrimSpace(text)) == "STATUS" {
		return "All systems running."
	}
	return "Commands: STATUS"
}
