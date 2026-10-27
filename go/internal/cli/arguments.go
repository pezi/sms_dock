package cli

import (
	"fmt"
	"io"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"

	smsdock "github.com/pezi/sms_dock/go"
)

// kind tells how the value of an option is read.
type kind int

const (
	flag kind = iota
	text
	integer
	float
	positive
	timestamp
	choice
	list
)

// option is one option of a command.
type option struct {
	name     string
	kind     kind
	metavar  string
	help     string
	choices  []string
	required bool
	// initial is the value without the option; nil means none.
	initial any
}

// command is one command of the tool.
type command struct {
	name        string
	help        string
	description string
	// positional is the name of the one positional argument.
	positional     string
	positionalHelp string
	options        []option
	// exclusive names options of which exactly one must be given.
	exclusive []string
	run       func(*App, *session) error
}

// arguments are the values of a parsed command line.
type arguments struct {
	command    *command
	positional string
	values     map[string]any
}

func (a *arguments) has(name string) bool {
	_, present := a.values[name]
	return present
}

func (a *arguments) flag(name string) bool {
	value, _ := a.values[name].(bool)
	return value
}

func (a *arguments) text(name string) string {
	value, _ := a.values[name].(string)
	return value
}

func (a *arguments) integer(name string) int {
	value, _ := a.values[name].(int)
	return value
}

func (a *arguments) int64(name string) int64 {
	value, _ := a.values[name].(int64)
	return value
}

func (a *arguments) float(name string) float64 {
	value, _ := a.values[name].(float64)
	return value
}

func (a *arguments) list(name string) []string {
	value, _ := a.values[name].([]string)
	return value
}

// parseFailure is a command line that cannot be read. It is reported
// with the usage line, as argparse does.
type parseFailure struct {
	command *command
	message string
}

func (f *parseFailure) Error() string { return f.message }

// helpRequest asks for the help text instead of a run.
type helpRequest struct {
	command *command
}

func (h *helpRequest) Error() string { return "help" }

var negativeNumber = regexp.MustCompile(`^-\d+$|^-\d*\.\d+$`)

// looksLikeOption reports whether a word is an option and not a value.
// As in argparse, negative numbers and words with spaces are values.
func looksLikeOption(word string) bool {
	if len(word) < 2 || word[0] != '-' {
		return false
	}
	return !negativeNumber.MatchString(word) && !strings.Contains(word, " ")
}

// parse reads the command line. Options may come before or after the
// positional argument.
func parse(commands []*command, words []string) (*arguments, error) {
	if len(words) == 0 {
		return nil, &parseFailure{
			message: "the following arguments are required: command",
		}
	}
	if words[0] == "-h" || words[0] == "--help" {
		return nil, &helpRequest{}
	}
	var chosen *command
	names := make([]string, len(commands))
	for index, candidate := range commands {
		names[index] = candidate.name
		if candidate.name == words[0] {
			chosen = candidate
		}
	}
	if chosen == nil {
		if looksLikeOption(words[0]) {
			return nil, &parseFailure{
				message: "unrecognized arguments: " + strings.Join(words, " "),
			}
		}
		return nil, &parseFailure{message: fmt.Sprintf(
			"argument command: invalid choice: '%s' (choose from %s)",
			words[0], strings.Join(names, ", "))}
	}
	result := &arguments{command: chosen, values: make(map[string]any)}
	failure := func(format string, values ...any) error {
		return &parseFailure{
			command: chosen, message: fmt.Sprintf(format, values...),
		}
	}
	for _, candidate := range chosen.options {
		if candidate.initial != nil {
			result.values[candidate.name] = candidate.initial
		}
	}
	given := make(map[string]bool)
	var positionals, unrecognized []string
	words = words[1:]
	for index := 0; index < len(words); index++ {
		word := words[index]
		if word == "--" {
			rest := words[index+1:]
			if len(positionals) == 0 && len(rest) > 0 {
				positionals, rest = rest[:1], rest[1:]
			}
			unrecognized = append(unrecognized, rest...)
			break
		}
		if word == "-h" || word == "--help" {
			return nil, &helpRequest{command: chosen}
		}
		if !looksLikeOption(word) {
			if len(positionals) > 0 {
				unrecognized = append(unrecognized, word)
			} else {
				positionals = append(positionals, word)
			}
			continue
		}
		name, inline, hasInline := strings.Cut(word, "=")
		found := chosen.find(name)
		if found == nil {
			unrecognized = append(unrecognized, word)
			continue
		}
		for _, other := range chosen.exclusive {
			if other != name && given[other] && contains(chosen.exclusive, name) {
				return nil, failure(
					"argument %s: not allowed with argument %s", name, other)
			}
		}
		repeated := given[name]
		given[name] = true
		if found.kind == flag {
			if hasInline {
				return nil, failure(
					"argument %s: ignored explicit argument '%s'", name, inline)
			}
			result.values[name] = true
			continue
		}
		var raw []string
		switch {
		case hasInline:
			raw = []string{inline}
		case found.kind == list:
			for index+1 < len(words) && !looksLikeOption(words[index+1]) {
				index++
				raw = append(raw, words[index])
			}
			if len(raw) == 0 {
				return nil, failure(
					"argument %s: expected at least one argument", name)
			}
		default:
			if index+1 >= len(words) || looksLikeOption(words[index+1]) {
				return nil, failure("argument %s: expected one argument", name)
			}
			index++
			raw = []string{words[index]}
		}
		value, problem := found.read(raw)
		if problem != "" {
			return nil, failure("argument %s: %s", name, problem)
		}
		if found.kind == list && repeated {
			// A repeated list option adds to the words given before.
			previous, _ := result.values[name].([]string)
			value = append(append([]string{}, previous...), value.([]string)...)
		}
		result.values[name] = value
	}
	var missing []string
	if len(positionals) == 0 {
		missing = append(missing, chosen.positional)
	} else {
		result.positional = positionals[0]
	}
	for _, candidate := range chosen.options {
		if candidate.required && !given[candidate.name] {
			missing = append(missing, candidate.name)
		}
	}
	if len(missing) > 0 {
		return nil, failure(
			"the following arguments are required: %s",
			strings.Join(missing, ", "))
	}
	if len(chosen.exclusive) > 0 {
		one := false
		for _, name := range chosen.exclusive {
			one = one || given[name]
		}
		if !one {
			return nil, failure(
				"one of the arguments %s is required",
				strings.Join(chosen.exclusive, " "))
		}
	}
	if len(unrecognized) > 0 {
		return nil, failure(
			"unrecognized arguments: %s", strings.Join(unrecognized, " "))
	}
	return result, nil
}

func contains(words []string, wanted string) bool {
	for _, word := range words {
		if word == wanted {
			return true
		}
	}
	return false
}

func (c *command) find(name string) *option {
	for index := range c.options {
		if c.options[index].name == name {
			return &c.options[index]
		}
	}
	return nil
}

// read converts the words of an option; problem is "" when it worked.
func (o *option) read(raw []string) (value any, problem string) {
	word := raw[0]
	switch o.kind {
	case text:
		return word, ""
	case integer:
		number, err := strconv.Atoi(strings.TrimSpace(word))
		if err != nil {
			return nil, "invalid int value: '" + word + "'"
		}
		return number, ""
	case float:
		number, err := strconv.ParseFloat(strings.TrimSpace(word), 64)
		if err != nil {
			return nil, "invalid float value: '" + word + "'"
		}
		return number, ""
	case positive:
		number, err := strconv.Atoi(strings.TrimSpace(word))
		if err != nil || number < 1 {
			return nil, "must be a number from 1"
		}
		return number, ""
	case timestamp:
		milliseconds, valid := readTimestamp(word)
		if !valid {
			return nil, "must be a date such as 2026-09-29 or " +
				"2026-09-29T08:00, or milliseconds since the epoch"
		}
		return milliseconds, ""
	case choice:
		if !contains(o.choices, word) {
			return nil, o.invalidChoice(word)
		}
		return word, ""
	case list:
		// Without choices any words are taken.
		for _, entry := range raw {
			if len(o.choices) > 0 && !contains(o.choices, entry) {
				return nil, o.invalidChoice(entry)
			}
		}
		return raw, ""
	}
	return nil, "cannot be read"
}

func (o *option) invalidChoice(word string) string {
	return "invalid choice: '" + word + "' (choose from " +
		strings.Join(o.choices, ", ") + ")"
}

var digitsOnly = regexp.MustCompile(`^[0-9]+$`)

// readTimestamp reads milliseconds since the epoch or an ISO 8601 date
// (local time unless it names an offset).
func readTimestamp(word string) (int64, bool) {
	if digitsOnly.MatchString(word) {
		milliseconds, err := strconv.ParseInt(word, 10, 64)
		return milliseconds, err == nil
	}
	for _, layout := range []string{
		"2006-01-02",
		"2006-01-02T15:04",
		"2006-01-02T15:04:05",
		"2006-01-02T15:04:05.999999999",
		"2006-01-02 15:04",
		"2006-01-02 15:04:05",
		"2006-01-02 15:04:05.999999999",
	} {
		moment, err := time.ParseInLocation(layout, word, time.Local)
		if err == nil {
			return moment.UnixMilli(), true
		}
	}
	for _, layout := range []string{
		time.RFC3339Nano, "2006-01-02T15:04Z07:00", "2006-01-02 15:04:05Z07:00",
	} {
		moment, err := time.Parse(layout, word)
		if err == nil {
			return moment.UnixMilli(), true
		}
	}
	return 0, false
}

// usage returns the usage line of the tool or of one command.
func usage(commands []*command, chosen *command) string {
	if chosen == nil {
		names := make([]string, len(commands))
		for index, candidate := range commands {
			names[index] = candidate.name
		}
		return "usage: smsdock [-h] {" + strings.Join(names, ",") + "} ..."
	}
	words := []string{"usage: smsdock", chosen.name, "[-h]"}
	for _, candidate := range chosen.options {
		word := candidate.name
		if candidate.kind == list {
			word += " " + candidate.metavar + " [" + candidate.metavar + " ...]"
		} else if candidate.kind != flag {
			word += " " + candidate.placeholder()
		}
		if !candidate.required {
			word = "[" + word + "]"
		}
		words = append(words, word)
	}
	words = append(words, chosen.positional)
	return strings.Join(words, " ")
}

func (o *option) placeholder() string {
	if o.kind == choice {
		return "{" + strings.Join(o.choices, ",") + "}"
	}
	if o.metavar != "" {
		return o.metavar
	}
	return strings.ToUpper(strings.ReplaceAll(strings.TrimLeft(o.name, "-"), "-", "_"))
}

// printHelp writes the help text of the tool or of one command.
func printHelp(output io.Writer, commands []*command, chosen *command) {
	entry := func(name, help string) {
		if len(name) > 21 {
			fmt.Fprintf(output, "  %s\n%24s%s\n", name, "", help)
			return
		}
		fmt.Fprintf(output, "  %-22s%s\n", name, help)
	}
	fmt.Fprintln(output, usage(commands, chosen))
	fmt.Fprintln(output)
	if chosen == nil {
		fmt.Fprintln(output, "Read, send and watch SMS through the SMS Dock app.")
		fmt.Fprintln(output)
		fmt.Fprintln(output, "commands:")
		for _, candidate := range commands {
			entry(candidate.name, candidate.help)
		}
		fmt.Fprintln(output)
		fmt.Fprintln(output, "options:")
		entry("-h, --help", "show this help message and exit")
		fmt.Fprintln(output)
		fmt.Fprintln(output, "The password is read from the environment "+
			"variable "+smsdock.PasswordEnv+", never from the command line.")
		return
	}
	description := chosen.description
	if description == "" {
		description = strings.ToUpper(chosen.help[:1]) + chosen.help[1:] + "."
	}
	fmt.Fprintln(output, description)
	fmt.Fprintln(output)
	fmt.Fprintln(output, "positional arguments:")
	entry(chosen.positional, chosen.positionalHelp)
	fmt.Fprintln(output)
	fmt.Fprintln(output, "options:")
	entry("-h, --help", "show this help message and exit")
	for _, candidate := range chosen.options {
		name := candidate.name
		if candidate.kind == list {
			name += " " + candidate.metavar + " [" + candidate.metavar + " ...]"
		} else if candidate.kind != flag {
			name += " " + candidate.placeholder()
		}
		entry(name, candidate.help)
	}
}

// connectionOptions are the options of all commands except fingerprint.
func connectionOptions() []option {
	var pinned any
	if value := os.Getenv(smsdock.FingerprintEnv); value != "" {
		pinned = value
	}
	return []option{
		{name: "--fingerprint", kind: text, metavar: "HEX", initial: pinned,
			help: "SHA-256 fingerprint of the certificate as shown by the " +
				"app (default: environment variable " +
				smsdock.FingerprintEnv + ")"},
		{name: "--insecure", kind: flag,
			help: "do not check the certificate (for tests only)"},
		{name: "--password-env", kind: text, metavar: "NAME",
			initial: smsdock.PasswordEnv,
			help: "environment variable with the password (default: " +
				smsdock.PasswordEnv + ")"},
		{name: "--timeout", kind: float, metavar: "SECONDS", initial: 30.0,
			help: "seconds to wait for a response (default: 30)"},
		{name: "--reconnect-attempts", kind: integer, metavar: "N", initial: 5,
			help: "reconnect attempts after a lost connection; -1 means " +
				"without limit (default: 5)"},
		{name: "--json", kind: flag, help: "print JSON instead of text"},
	}
}

// allCommands returns the commands of the tool.
func allCommands() []*command {
	const urlHelp = "wss:// URL shown by the app, e.g. wss://192.168.1.50:9443/ws"
	connected := func(definition command) *command {
		definition.positional = "url"
		definition.positionalHelp = urlHelp
		definition.options = append(connectionOptions(), definition.options...)
		return &definition
	}
	eventChoices := append(append([]string{}, smsdock.EventNames...), "*")
	return []*command{
		{
			name: "fingerprint",
			help: "print the certificate fingerprint of a server",
			description: "Print the SHA-256 fingerprint of the certificate " +
				"the server presents. Compare it with the Server screen of " +
				"the app before you use it.",
			positional: "HOST[:PORT]",
			positionalHelp: fmt.Sprintf(
				"address of the phone; the default port is %d",
				smsdock.DefaultAPIPort),
			options: []option{
				{name: "--timeout", kind: float, metavar: "SECONDS",
					initial: 10.0, help: "seconds to wait (default: 10)"},
			},
		},
		connected(command{
			name: "status",
			help: "show phone, SIMs, permissions and licence",
			run:  (*App).status,
		}),
		connected(command{
			name: "conversations",
			help: "list conversations, newest first",
			run:  (*App).conversations,
			options: []option{
				{name: "--search", kind: text, metavar: "TEXT",
					help: "search text, numbers and contact names"},
				{name: "--unread", kind: flag,
					help: "only conversations with unread messages"},
				{name: "--sim", kind: integer, metavar: "ID",
					help: "only this subscription id"},
				{name: "--limit", kind: positive, metavar: "N", initial: 50,
					help: "number of conversations (default: 50)"},
			},
		}),
		connected(command{
			name: "messages",
			help: "list messages, newest first",
			run:  (*App).messages,
			options: []option{
				{name: "--address", kind: text, metavar: "NUMBER",
					help: "only messages from or to this number"},
				{name: "--conversation", kind: integer, metavar: "ID",
					help: "only this conversation"},
				{name: "--direction", kind: choice, initial: "all",
					choices: []string{"in", "out", "all"},
					help:    "incoming, outgoing or both"},
				{name: "--search", kind: text, metavar: "TEXT",
					help: "search the text"},
				{name: "--since", kind: timestamp, metavar: "TIME",
					help: "not older than this: 2026-09-29, " +
						"2026-09-29T08:00 or milliseconds"},
				{name: "--until", kind: timestamp, metavar: "TIME",
					help: "not newer than this"},
				{name: "--unread", kind: flag, help: "only unread messages"},
				{name: "--sim", kind: integer, metavar: "ID",
					help: "only this subscription id"},
				{name: "--limit", kind: positive, metavar: "N", initial: 50,
					help: "number of messages (default: 50)"},
			},
		}),
		connected(command{
			name: "send",
			help: "send an SMS",
			description: "Send an SMS. Without --text the text is read " +
				"from standard input.",
			run: (*App).send,
			options: []option{
				{name: "--to", kind: text, metavar: "NUMBER", required: true,
					help: "phone number of the recipient"},
				{name: "--text", kind: text, help: "text of the message"},
				{name: "--sim", kind: integer, metavar: "ID",
					help: "subscription id of the SIM"},
				{name: "--request-id", kind: text, metavar: "ID",
					help: "your own id; the same id is sent only once"},
				{name: "--wait", kind: flag,
					help: "wait until the message was sent or failed"},
				{name: "--wait-timeout", kind: float, metavar: "SECONDS",
					initial: 60.0,
					help:    "longest time to wait with --wait (default: 60)"},
			},
		}),
		connected(command{
			name: "send-group",
			help: "send one text to several numbers, one SMS each",
			description: "Send the same text to several numbers as separate " +
				"SMS. Without --text the text is read from standard input.",
			run: (*App).sendGroup,
			options: []option{
				{name: "--to", kind: list, metavar: "NUMBER", required: true,
					help: "phone numbers of the recipients (at most 20); " +
						"the option may be repeated"},
				{name: "--text", kind: text, help: "text of the message"},
				{name: "--sim", kind: integer, metavar: "ID",
					help: "subscription id of the SIM"},
				{name: "--request-id", kind: text, metavar: "ID",
					help: "your own id of the group; the same id is sent " +
						"only once"},
				{name: "--wait", kind: flag,
					help: "wait until every message was sent or failed"},
				{name: "--wait-timeout", kind: float, metavar: "SECONDS",
					initial: 60.0,
					help:    "longest time to wait with --wait (default: 60)"},
			},
		}),
		connected(command{
			name:      "delete",
			help:      "delete a message or a conversation",
			run:       (*App).delete,
			exclusive: []string{"--message", "--conversation"},
			options: []option{
				{name: "--message", kind: integer, metavar: "ID",
					help: "id of the message"},
				{name: "--conversation", kind: integer, metavar: "ID",
					help: "id of the conversation"},
			},
		}),
		connected(command{
			name: "mark-read",
			help: "mark a conversation as read",
			description: "Mark all messages of a conversation as read. " +
				"SMS Dock must be the default SMS app of the phone.",
			run: (*App).markRead,
			options: []option{
				{name: "--conversation", kind: integer, metavar: "ID",
					required: true, help: "id of the conversation"},
			},
		}),
		connected(command{
			name: "contacts",
			help: "list the contacts of the phone",
			run:  (*App).contacts,
			options: []option{
				{name: "--search", kind: text, metavar: "TEXT",
					help: "search names and numbers"},
				{name: "--limit", kind: positive, metavar: "N", initial: 50,
					help: "number of contacts (default: 50)"},
			},
		}),
		connected(command{
			name:        "listen",
			help:        "print incoming SMS as they arrive",
			description: "Print events as they arrive until Ctrl+C.",
			run:         (*App).listen,
			options: []option{
				{name: "--address", kind: text, metavar: "NUMBER",
					help: "only SMS events of this number"},
				{name: "--events", kind: list, metavar: "EVENT",
					choices: eventChoices, initial: []string{"sms.received"},
					help: "events to print (default: sms.received); * " +
						"means all"},
				{name: "--count", kind: positive, metavar: "N",
					help: "stop after this many events"},
			},
		}),
	}
}
