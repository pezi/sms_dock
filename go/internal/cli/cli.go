// Package cli is the command-line tool smsdock for the SMS Dock WebSocket
// API.
//
// Exit codes: 0 success, 1 the server answered with an error, 2 wrong
// usage, 3 connection or TLS problem, 130 stopped with Ctrl+C.
package cli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/url"
	"os"
	"sort"
	"strconv"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"golang.org/x/term"

	smsdock "github.com/pezi/sms_dock/go"
)

// Exit codes of the tool.
const (
	ExitOK          = 0
	ExitAPIError    = 1
	ExitUsage       = 2
	ExitConnection  = 3
	ExitInterrupted = 130
)

// textWidth is the number of characters of a message shown in a table.
const textWidth = 60

// usageError says that the command line cannot be carried out as given.
type usageError struct {
	message string
}

func (e *usageError) Error() string { return e.message }

// App is the command-line tool with its surroundings, so that tests can
// replace them.
type App struct {
	// Stdin is read for the text of "send" without --text.
	Stdin io.Reader
	// Stdout takes the results, Stderr messages and errors.
	Stdout io.Writer
	Stderr io.Writer
	// StdinIsTerminal reports whether a person types the input.
	StdinIsTerminal func() bool
	// ReadPassword asks for the password without showing it.
	ReadPassword func(prompt string) (string, error)
}

// session is one run of a command: the arguments and the connection.
type session struct {
	ctx    context.Context
	args   *arguments
	client *smsdock.Client
}

// Main runs the tool with the surroundings of the process and returns
// the exit code.
func Main(ctx context.Context, words []string) int {
	app := &App{
		Stdin:  os.Stdin,
		Stdout: os.Stdout,
		Stderr: os.Stderr,
		StdinIsTerminal: func() bool {
			return term.IsTerminal(int(os.Stdin.Fd()))
		},
		ReadPassword: func(prompt string) (string, error) {
			fmt.Fprint(os.Stderr, prompt)
			password, err := term.ReadPassword(int(os.Stdin.Fd()))
			fmt.Fprintln(os.Stderr)
			return string(password), err
		},
	}
	return app.Run(ctx, words)
}

// Run carries out the command line (without the program name) and
// returns the exit code.
func (a *App) Run(ctx context.Context, words []string) int {
	commands := allCommands()
	args, err := parse(commands, words)
	var help *helpRequest
	if errors.As(err, &help) {
		printHelp(a.Stdout, commands, help.command)
		return ExitOK
	}
	var failure *parseFailure
	if errors.As(err, &failure) {
		fmt.Fprintln(a.Stderr, usage(commands, failure.command))
		name := "smsdock"
		if failure.command != nil {
			name += " " + failure.command.name
		}
		fmt.Fprintf(a.Stderr, "%s: error: %s\n", name, failure.message)
		return ExitUsage
	}
	err = a.execute(ctx, args)
	if err == nil {
		return ExitOK
	}
	var silent *exit
	if errors.As(err, &silent) {
		return silent.code
	}
	if ctx.Err() != nil && errors.Is(err, context.Canceled) {
		return ExitInterrupted
	}
	fmt.Fprintf(a.Stderr, "smsdock: %v\n", err)
	var refused *smsdock.APIError
	var wrong *usageError
	var argument *smsdock.ArgumentError
	switch {
	case errors.As(err, &refused):
		return ExitAPIError
	case errors.As(err, &wrong), errors.As(err, &argument):
		return ExitUsage
	}
	return ExitConnection
}

// exit ends the run with a code after the command has reported itself.
type exit struct {
	code int
}

func (e *exit) Error() string { return "exit " + strconv.Itoa(e.code) }

func (a *App) execute(ctx context.Context, args *arguments) error {
	if args.command.name == "fingerprint" {
		return a.fingerprint(ctx, args)
	}
	client, err := a.client(args)
	if err != nil {
		return err
	}
	defer client.Close()
	if err := client.Connect(ctx); err != nil {
		return err
	}
	return args.command.run(a, &session{ctx: ctx, args: args, client: client})
}

func (a *App) client(args *arguments) (*smsdock.Client, error) {
	if !args.flag("--insecure") && args.text("--fingerprint") == "" {
		return nil, &usageError{
			"No certificate fingerprint. Pass --fingerprint or set " +
				smsdock.FingerprintEnv + ". 'smsdock fingerprint HOST' " +
				"prints the fingerprint of a server."}
	}
	attempts := args.integer("--reconnect-attempts")
	switch {
	case attempts < -1:
		return nil, &usageError{"--reconnect-attempts cannot be less than -1"}
	case attempts == -1:
		attempts = smsdock.ReconnectForever
	case attempts == 0:
		attempts = smsdock.NoReconnect
	}
	password, err := a.password(args.text("--password-env"))
	if err != nil {
		return nil, err
	}
	seconds := args.float("--timeout")
	if !(seconds > 0) {
		return nil, &usageError{"Timeouts must be positive"}
	}
	return smsdock.New(args.positional, &smsdock.Options{
		Password:          password,
		Fingerprint:       args.text("--fingerprint"),
		Insecure:          args.flag("--insecure"),
		Timeout:           duration(seconds),
		ReconnectAttempts: attempts,
	})
}

func duration(seconds float64) time.Duration {
	return time.Duration(seconds * float64(time.Second))
}

func (a *App) password(variable string) (string, error) {
	if password := os.Getenv(variable); password != "" {
		return password, nil
	}
	if a.StdinIsTerminal != nil && a.StdinIsTerminal() && a.ReadPassword != nil {
		password, err := a.ReadPassword("SMS Dock password: ")
		if err == nil && password != "" {
			return password, nil
		}
	}
	return "", &usageError{
		"No password. Set the environment variable " + variable + "."}
}

func (a *App) fingerprint(ctx context.Context, args *arguments) error {
	host, port, err := hostAndPort(args.positional)
	if err != nil {
		return err
	}
	fingerprint, err := smsdock.FetchFingerprint(
		ctx, host, port, duration(args.float("--timeout")))
	if err != nil {
		return err
	}
	fmt.Fprintln(a.Stdout, fingerprint)
	fmt.Fprintln(a.Stderr,
		"Compare this fingerprint with the Server screen of the app. "+
			"Use it only if both are the same.")
	return nil
}

func (a *App) status(run *session) error {
	status, err := run.client.Status(run.ctx)
	if err != nil {
		return err
	}
	if run.args.flag("--json") {
		return a.printJSON(status.Raw)
	}
	var missing []string
	for name, granted := range status.Permissions {
		if !granted {
			missing = append(missing, name)
		}
	}
	sort.Strings(missing)
	licence := fmt.Sprintf(
		"free version, %d of %d SMS sent today",
		status.Licence.SentToday, status.Licence.DailyLimit)
	if status.Licence.Licensed {
		licence = "full licence"
	} else if !status.Licence.Enforced {
		licence = "not enforced"
	}
	permissions := "all granted"
	if len(missing) > 0 {
		permissions = "missing: " + strings.Join(missing, ", ")
	}
	for _, line := range [][2]string{
		{"App", fmt.Sprintf("%s %s (protocol %d)", status.App.Name,
			status.App.Version, status.App.ProtocolVersion)},
		{"Default SMS app", yesNo(status.DefaultSmsApp)},
		{"Delete mode", status.DeleteMode},
		{"Licence", licence},
		{"Permissions", permissions},
	} {
		fmt.Fprintf(a.Stdout, "%-17s%s\n", line[0]+":", line[1])
	}
	if len(status.Sims) > 0 {
		fmt.Fprintln(a.Stdout)
		rows := make([][]string, len(status.Sims))
		for index, sim := range status.Sims {
			rows[index] = []string{
				strconv.Itoa(sim.SubscriptionID), strconv.Itoa(sim.Slot),
				sim.Name, orEmpty(sim.Number), yesNo(sim.IsDefault),
			}
		}
		a.printTable(
			[]string{"SIM", "SLOT", "NAME", "NUMBER", "DEFAULT"}, rows)
	}
	return nil
}

// iterOptions reads --limit: the pages are as large as they may be.
func iterOptions(args *arguments) smsdock.IterOptions {
	limit := args.integer("--limit")
	return smsdock.IterOptions{
		PageSize: min(limit, smsdock.MaxPageSize), MaxItems: limit,
	}
}

func optionalInt(args *arguments, name string) *int {
	if !args.has(name) {
		return nil
	}
	return smsdock.Ptr(args.integer(name))
}

func (a *App) conversations(run *session) error {
	args := run.args
	filter := smsdock.ConversationFilter{
		Query:  args.text("--search"),
		Unread: args.flag("--unread"),
		Sim:    optionalInt(args, "--sim"),
	}
	var raws []json.RawMessage
	var rows [][]string
	for item, err := range run.client.IterConversations(
		run.ctx, filter, iterOptions(args)) {
		if err != nil {
			return err
		}
		raws = append(raws, item.Raw)
		rows = append(rows, []string{
			strconv.FormatInt(item.ID, 10), formatTime(item.Timestamp, false),
			item.Address, orEmpty(item.Contact),
			strconv.Itoa(item.MessageCount), strconv.Itoa(item.UnreadCount),
			item.Direction, oneLine(item.Snippet),
		})
	}
	if args.flag("--json") {
		return a.printJSONList(raws)
	}
	a.printTable([]string{
		"ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT",
	}, rows)
	return nil
}

func (a *App) messages(run *session) error {
	args := run.args
	filter := smsdock.MessageFilter{
		Address:   args.text("--address"),
		Direction: args.text("--direction"),
		Query:     args.text("--search"),
		Sim:       optionalInt(args, "--sim"),
		Unread:    args.flag("--unread"),
	}
	if args.has("--conversation") {
		filter.ConversationID = smsdock.Ptr(int64(args.integer("--conversation")))
	}
	if args.has("--since") {
		filter.Since = time.UnixMilli(args.int64("--since"))
	}
	if args.has("--until") {
		filter.Until = time.UnixMilli(args.int64("--until"))
	}
	var raws []json.RawMessage
	var rows [][]string
	for item, err := range run.client.IterMessages(
		run.ctx, filter, iterOptions(args)) {
		if err != nil {
			return err
		}
		raws = append(raws, item.Raw)
		identifier := ""
		if item.ID != nil {
			identifier = strconv.FormatInt(*item.ID, 10)
		}
		rows = append(rows, []string{
			identifier, formatTime(item.Timestamp, false), item.Direction,
			item.Address, orEmpty(item.Contact), item.Status,
			oneLine(item.Body),
		})
	}
	if args.flag("--json") {
		return a.printJSONList(raws)
	}
	a.printTable([]string{
		"ID", "TIME", "DIR", "NUMBER", "NAME", "STATUS", "TEXT",
	}, rows)
	return nil
}

// messageText returns the text to send: --text or standard input.
func (a *App) messageText(args *arguments) (string, error) {
	message := args.text("--text")
	if !args.has("--text") {
		if a.StdinIsTerminal != nil && a.StdinIsTerminal() {
			return "", &usageError{
				"No text. Pass --text or write it to standard input."}
		}
		input, err := io.ReadAll(a.Stdin)
		if err != nil {
			return "", err
		}
		message = strings.TrimSuffix(string(input), "\n")
	}
	if message == "" {
		return "", &usageError{"The text is empty."}
	}
	return message, nil
}

func (a *App) send(run *session) error {
	args := run.args
	message, err := a.messageText(args)
	if err != nil {
		return err
	}
	sent, err := run.client.Send(
		run.ctx, args.text("--to"), message, &smsdock.SendOptions{
			Sim:       optionalInt(args, "--sim"),
			RequestID: args.text("--request-id"),
		})
	if err != nil {
		return err
	}
	status, to, parts := sent.Status, sent.To, sent.Parts
	requestID, raw := sent.RequestID, sent.Raw
	var errorCode *int
	if args.flag("--wait") {
		entry, err := run.client.WaitForResult(
			run.ctx, sent.RequestID, &smsdock.WaitOptions{
				Timeout: duration(args.float("--wait-timeout")),
			})
		if err != nil {
			return err
		}
		status, to, parts = entry.Status, entry.To, entry.Parts
		requestID, raw, errorCode = entry.RequestID, entry.Raw, entry.ErrorCode
	}
	// Also without --wait: a repeated request id tells the old outcome.
	failed := status == "failed"
	switch {
	case args.flag("--json"):
		if err := a.printJSON(raw); err != nil {
			return err
		}
	case failed:
		cause := ""
		if errorCode != nil {
			cause = fmt.Sprintf("error code %d, ", *errorCode)
		}
		fmt.Fprintf(a.Stderr, "smsdock: Sending to %s failed (%srequestId %s)\n",
			to, cause, requestID)
	default:
		fmt.Fprintf(a.Stdout, "%s: %s, %d part(s), requestId %s\n",
			capitalize(status), to, parts, requestID)
	}
	if failed {
		return &exit{ExitAPIError}
	}
	return nil
}

// groupJSON is the GroupSendResult printed by send-group --json; with
// --wait the members are outbox entries.
type groupJSON struct {
	RequestID string            `json:"requestId"`
	To        []string          `json:"to"`
	Parts     int               `json:"parts"`
	Status    string            `json:"status"`
	Messages  []json.RawMessage `json:"messages"`
}

func (a *App) sendGroup(run *session) error {
	args := run.args
	message, err := a.messageText(args)
	if err != nil {
		return err
	}
	result, err := run.client.SendGroup(
		run.ctx, args.list("--to"), message, &smsdock.SendOptions{
			Sim:       optionalInt(args, "--sim"),
			RequestID: args.text("--request-id"),
		})
	if err != nil {
		return err
	}
	type member struct {
		to, status, requestID string
		errorCode             *int
		raw                   json.RawMessage
	}
	members := make([]member, 0, len(result.Messages))
	deadline := time.Now().Add(duration(args.float("--wait-timeout")))
	for _, sent := range result.Messages {
		current := member{sent.To, sent.Status, sent.RequestID, nil, sent.Raw}
		if args.flag("--wait") && sent.Status != "failed" {
			entry, err := run.client.WaitForResult(
				run.ctx, sent.RequestID, &smsdock.WaitOptions{
					Timeout: max(time.Millisecond, time.Until(deadline)),
				})
			if err != nil {
				return err
			}
			current = member{
				entry.To, entry.Status, entry.RequestID, entry.ErrorCode, entry.Raw,
			}
		}
		members = append(members, current)
	}
	failed := 0
	for _, current := range members {
		if current.status == "failed" {
			failed++
		}
	}
	if args.flag("--json") {
		printed := groupJSON{
			RequestID: result.RequestID, To: result.To, Parts: result.Parts,
			Status: result.Status, Messages: []json.RawMessage{},
		}
		for _, current := range members {
			printed.Messages = append(printed.Messages, current.raw)
		}
		raw, err := json.Marshal(printed)
		if err != nil {
			return err
		}
		if err := a.printJSON(raw); err != nil {
			return err
		}
	} else {
		fmt.Fprintf(a.Stdout, "Group %s: %d recipient(s), %d part(s) each\n",
			result.RequestID, len(members), result.Parts)
		for _, current := range members {
			if current.status == "failed" {
				cause := ""
				if current.errorCode != nil {
					cause = fmt.Sprintf(", error code %d", *current.errorCode)
				}
				fmt.Fprintf(a.Stdout, "  Failed: %s%s, requestId %s\n",
					current.to, cause, current.requestID)
				continue
			}
			fmt.Fprintf(a.Stdout, "  %s: %s, requestId %s\n",
				capitalize(current.status), current.to, current.requestID)
		}
		if failed > 0 {
			fmt.Fprintf(a.Stderr, "smsdock: %d of %d message(s) failed\n",
				failed, len(members))
		}
	}
	if failed > 0 {
		return &exit{ExitAPIError}
	}
	return nil
}

func (a *App) delete(run *session) error {
	args := run.args
	var result *smsdock.DeleteResult
	var err error
	if args.has("--message") {
		result, err = run.client.DeleteMessage(
			run.ctx, int64(args.integer("--message")))
	} else {
		result, err = run.client.DeleteConversation(
			run.ctx, int64(args.integer("--conversation")))
	}
	if err != nil {
		return err
	}
	if args.flag("--json") {
		return a.printJSON(result.Raw)
	}
	where := "hidden in SMS Dock, still on the phone"
	if result.Mode == "provider" {
		where = "removed from the phone"
	}
	fmt.Fprintf(a.Stdout, "Deleted %d message(s): %s\n", result.Deleted, where)
	return nil
}

func (a *App) markRead(run *session) error {
	conversation := run.args.integer("--conversation")
	result, err := run.client.MarkRead(run.ctx, int64(conversation))
	if err != nil {
		return err
	}
	if run.args.flag("--json") {
		return a.printJSON(result.Raw)
	}
	fmt.Fprintf(a.Stdout, "Marked conversation %d as read\n", conversation)
	return nil
}

func (a *App) contacts(run *session) error {
	args := run.args
	filter := smsdock.ContactFilter{Query: args.text("--search")}
	var raws []json.RawMessage
	var rows [][]string
	for contact, err := range run.client.IterContacts(
		run.ctx, filter, iterOptions(args)) {
		if err != nil {
			return err
		}
		raws = append(raws, contact.Raw)
		if len(contact.Numbers) == 0 {
			rows = append(rows, []string{contact.Name, "", ""})
		}
		for _, number := range contact.Numbers {
			rows = append(rows, []string{
				contact.Name, number.Number, orEmpty(number.Label),
			})
		}
	}
	if args.flag("--json") {
		return a.printJSONList(raws)
	}
	a.printTable([]string{"NAME", "NUMBER", "LABEL"}, rows)
	return nil
}

func (a *App) listen(run *session) error {
	args := run.args
	run.client.SetOnReconnect(a.reportReconnect)
	events := args.list("--events")
	var err error
	if args.has("--address") {
		_, err = run.client.SubscribeAddress(
			run.ctx, args.text("--address"), events...)
	} else {
		_, err = run.client.Subscribe(run.ctx, events...)
	}
	if err != nil {
		return err
	}
	if !args.flag("--json") {
		fmt.Fprintf(a.Stderr, "Listening for %s. Press Ctrl+C to stop.\n",
			strings.Join(events, ", "))
	}
	count := 0
	for event, err := range run.client.Events(run.ctx, 0) {
		if err != nil {
			return err
		}
		if args.flag("--json") {
			line, err := pythonJSON(event.Raw, false)
			if err != nil {
				return err
			}
			fmt.Fprintln(a.Stdout, line)
		} else {
			fmt.Fprintln(a.Stdout, describe(event))
		}
		count++
		if args.has("--count") && count >= args.integer("--count") {
			break
		}
	}
	return nil
}

// reportReconnect tells on standard error what a lost connection may
// have cost.
func (a *App) reportReconnect(client *smsdock.Client) {
	lost := "no event was missed"
	if missed, known := client.MissedEvents(); client.ServerRestarted() {
		lost = "the app was restarted, events may be missing"
	} else if known && missed > 0 {
		lost = fmt.Sprintf(
			"%d event(s) of the phone were not received", missed)
	}
	fmt.Fprintf(a.Stderr, "smsdock: reconnected; %s\n", lost)
}

// describe returns one event as a line of text.
func describe(event smsdock.Event) string {
	message := event.Data.Message
	if event.Name == "sms.received" {
		if message == nil {
			message = &smsdock.Message{}
		}
		sender := message.Address
		if message.Contact != nil && *message.Contact != "" {
			sender += " (" + *message.Contact + ")"
		}
		return formatTime(message.Timestamp, true) + "  " + sender + ": " +
			message.Body
	}
	now := formatTime(time.Now().UnixMilli(), true)
	if event.Name == "sms.sent" && message != nil {
		return now + "  sent to " + message.Address + ": " + message.Body
	}
	details := strings.Join(scalars(event.Data.Raw), " ")
	return strings.TrimRight(now+"  "+event.Name+" "+details, " ")
}

// scalars returns the fields of an object that are neither objects nor
// lists as name=value, in their order.
func scalars(raw json.RawMessage) []string {
	var pairs []string
	decoder := json.NewDecoder(strings.NewReader(string(raw)))
	decoder.UseNumber()
	if token, err := decoder.Token(); err != nil || token != json.Delim('{') {
		return nil
	}
	for decoder.More() {
		name, err := decoder.Token()
		if err != nil {
			return pairs
		}
		var value json.RawMessage
		if decoder.Decode(&value) != nil {
			return pairs
		}
		var shown string
		switch text := string(value); {
		case text == "null":
			shown = "None"
		case text == "true":
			shown = "True"
		case text == "false":
			shown = "False"
		case strings.HasPrefix(text, `"`):
			json.Unmarshal(value, &shown)
		case strings.HasPrefix(text, "{"), strings.HasPrefix(text, "["):
			continue
		default:
			shown = text
		}
		pairs = append(pairs, fmt.Sprintf("%v=%s", name, shown))
	}
	return pairs
}

// hostAndPort splits HOST, HOST:PORT, [IPv6]:PORT or a wss:// URL.
func hostAndPort(word string) (string, int, error) {
	invalid := &usageError{"'" + word + "' is not HOST or HOST:PORT"}
	value := strings.TrimSpace(word)
	if !strings.Contains(value, "://") {
		if strings.Count(value, ":") > 1 && !strings.HasPrefix(value, "[") {
			value = "[" + value + "]"
		}
		value = "//" + value
	}
	parts, err := url.Parse(value)
	if err != nil || parts.Hostname() == "" {
		return "", 0, invalid
	}
	port := smsdock.DefaultAPIPort
	if parts.Port() != "" {
		port, err = strconv.Atoi(parts.Port())
		if err != nil || port < 1 || port > 65535 {
			return "", 0, invalid
		}
	}
	return parts.Hostname(), port, nil
}

// formatTime shows milliseconds since the epoch in local time.
func formatTime(milliseconds int64, seconds bool) string {
	layout := "2006-01-02 15:04"
	if seconds {
		layout = "2006-01-02 15:04:05"
	}
	return time.UnixMilli(milliseconds).Format(layout)
}

// oneLine puts a text on one line and cuts it to textWidth characters.
func oneLine(value string) string {
	line := []rune(strings.Join(strings.Fields(value), " "))
	if len(line) > textWidth {
		line = append(line[:textWidth-1], '…')
	}
	return string(line)
}

func yesNo(value bool) string {
	if value {
		return "yes"
	}
	return "no"
}

func orEmpty(value *string) string {
	if value == nil {
		return ""
	}
	return *value
}

// capitalize makes the first letter upper case and the others lower
// case.
func capitalize(word string) string {
	first, size := utf8.DecodeRuneInString(word)
	if size == 0 {
		return word
	}
	return string(unicode.ToUpper(first)) + strings.ToLower(word[size:])
}

func (a *App) printJSON(raw json.RawMessage) error {
	formatted, err := pythonJSON(raw, true)
	if err != nil {
		return err
	}
	fmt.Fprintln(a.Stdout, formatted)
	return nil
}

func (a *App) printJSONList(items []json.RawMessage) error {
	var list strings.Builder
	list.WriteByte('[')
	for index, item := range items {
		if index > 0 {
			list.WriteByte(',')
		}
		list.Write(item)
	}
	list.WriteByte(']')
	return a.printJSON(json.RawMessage(list.String()))
}

func (a *App) printTable(headers []string, rows [][]string) {
	if len(rows) == 0 {
		fmt.Fprintln(a.Stdout, "Nothing found.")
		return
	}
	widths := make([]int, len(headers))
	for _, row := range append([][]string{headers}, rows...) {
		for index, cell := range row {
			widths[index] = max(widths[index], utf8.RuneCountInString(cell))
		}
	}
	for _, row := range append([][]string{headers}, rows...) {
		cells := make([]string, len(row))
		for index, cell := range row {
			padding := widths[index] - utf8.RuneCountInString(cell)
			cells[index] = cell + strings.Repeat(" ", padding)
		}
		fmt.Fprintln(a.Stdout,
			strings.TrimRightFunc(strings.Join(cells, "  "), unicode.IsSpace))
	}
}
