package cli

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/pezi/sms_dock/go/internal/fakeserver"
)

const (
	password = "secret123"
	baseTime = int64(1790672531000)
	anna     = "+4369912345678"
	ben      = "+4369087654321"
	someURL  = "wss://192.168.1.50:9443/ws"
)

var someFingerprint = strings.TrimSuffix(strings.Repeat("AB:", 32), ":")

// environment gives every test the password and no fingerprint.
func environment(t *testing.T) {
	t.Helper()
	t.Setenv("SMSDOCK_PASSWORD", password)
	t.Setenv("SMSDOCK_FINGERPRINT", "")
}

// startServer starts a fake server with 29 messages in three
// conversations.
func startServer(t *testing.T, options *fakeserver.Options) *fakeserver.Server {
	t.Helper()
	environment(t)
	server, err := fakeserver.Start(options)
	if err != nil {
		t.Fatalf("The fake server did not start: %v", err)
	}
	t.Cleanup(server.Stop)
	server.AddContact("Anna Example", anna, "Mobile")
	server.AddContact("Ben Example", ben, "Work")
	for index := 0; index < 25; index++ {
		direction := "in"
		if index%2 == 1 {
			direction = "out"
		}
		server.AddMessage(fakeserver.NewMessage{
			Address: anna, Body: fmt.Sprintf("Anna %d", index),
			Direction: direction, Timestamp: baseTime + int64(index)*1000,
			Unread: index >= 22,
		})
	}
	server.AddMessage(fakeserver.NewMessage{
		Address: ben, Body: "Your invoice is attached", Direction: "out",
		Timestamp: baseTime - 5000, Sim: 2,
	})
	server.AddMessage(fakeserver.NewMessage{
		Address: ben, Body: "Thanks for the invoice",
		Timestamp: baseTime - 4000, Sim: 2,
	})
	server.AddMessage(fakeserver.NewMessage{
		Address: ben, Body: "See you", Timestamp: baseTime - 3000, Sim: 2,
	})
	server.AddMessage(fakeserver.NewMessage{
		Address: "A1", Body: "Your bill is ready",
		Timestamp: baseTime - 9000, Unread: true,
	})
	return server
}

// result is what one run of the tool left behind.
type result struct {
	code int
	out  string
	err  string
}

// runWith runs the command line with this standard input.
func runWith(t *testing.T, app *App, words ...string) result {
	t.Helper()
	var out, errors bytes.Buffer
	app.Stdout, app.Stderr = &out, &errors
	if app.Stdin == nil {
		app.Stdin = strings.NewReader("")
	}
	if app.StdinIsTerminal == nil {
		app.StdinIsTerminal = func() bool { return false }
	}
	code := app.Run(context.Background(), words)
	return result{code, out.String(), errors.String()}
}

func run(t *testing.T, words ...string) result {
	t.Helper()
	return runWith(t, &App{}, words...)
}

// commandLine adds URL, fingerprint and a short timeout to a command.
func commandLine(server *fakeserver.Server, name string, options ...string) []string {
	words := []string{
		name, server.URL(), "--fingerprint", server.Fingerprint(),
		"--timeout", "5",
	}
	return append(words, options...)
}

func waitUntil(condition func() bool) bool {
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if condition() {
			return true
		}
		time.Sleep(5 * time.Millisecond)
	}
	return condition()
}

func equal[T comparable](t *testing.T, got, want T) {
	t.Helper()
	if got != want {
		t.Fatalf("got %#v, want %#v", got, want)
	}
}

func wantPart(t *testing.T, text, part string) {
	t.Helper()
	if !strings.Contains(text, part) {
		t.Fatalf("%q is missing in %q", part, text)
	}
}

func lines(text string) []string {
	return strings.Split(strings.TrimSuffix(text, "\n"), "\n")
}

func fields(t *testing.T, line string, want ...string) {
	t.Helper()
	got := strings.Fields(line)
	if strings.Join(got, "|") != strings.Join(want, "|") {
		t.Fatalf("got %q, want %q", got, want)
	}
}

func decodeJSON[T any](t *testing.T, text string) T {
	t.Helper()
	var value T
	if err := json.Unmarshal([]byte(text), &value); err != nil {
		t.Fatalf("no JSON: %v in %q", err, text)
	}
	return value
}

// ----------------------------------------------------------------------
// Arguments
// ----------------------------------------------------------------------

func TestParserReadsAllFlags(t *testing.T) {
	environment(t)
	args, err := parse(allCommands(), []string{
		"messages", someURL, "--fingerprint", someFingerprint,
		"--address", anna, "--direction", "in", "--search", "invoice",
		"--limit", "10", "--unread", "--sim", "2", "--json", "--timeout", "7",
		"--since", "1790672531000", "--until", "2026-09-29T08:00",
		"--conversation", "179", "--reconnect-attempts", "2",
	})
	if err != nil {
		t.Fatal(err)
	}
	equal(t, args.command.name, "messages")
	equal(t, args.positional, someURL)
	equal(t, args.text("--fingerprint"), someFingerprint)
	equal(t, args.text("--address"), anna)
	equal(t, args.text("--direction"), "in")
	equal(t, args.text("--search"), "invoice")
	equal(t, args.integer("--limit"), 10)
	equal(t, args.flag("--unread"), true)
	equal(t, args.integer("--sim"), 2)
	equal(t, args.flag("--json"), true)
	equal(t, args.float("--timeout"), 7.0)
	equal(t, args.int64("--since"), 1790672531000)
	local := time.Date(2026, 9, 29, 8, 0, 0, 0, time.Local)
	equal(t, args.int64("--until"), local.UnixMilli())
	equal(t, args.integer("--conversation"), 179)
	equal(t, args.integer("--reconnect-attempts"), 2)
	equal(t, args.flag("--insecure"), false)
}

func TestParserDefaults(t *testing.T) {
	environment(t)
	args, _ := parse(allCommands(), []string{"messages", someURL})
	equal(t, args.text("--direction"), "all")
	equal(t, args.integer("--limit"), 50)
	equal(t, args.float("--timeout"), 30.0)
	equal(t, args.integer("--reconnect-attempts"), 5)
	equal(t, args.flag("--json") || args.flag("--unread") ||
		args.flag("--insecure"), false)
	equal(t, args.has("--fingerprint"), false)
	equal(t, args.text("--password-env"), "SMSDOCK_PASSWORD")
	listen, _ := parse(allCommands(), []string{"listen", someURL})
	equal(t, strings.Join(listen.list("--events"), ","), "sms.received")
	equal(t, listen.has("--address"), false)
	send, _ := parse(allCommands(), []string{"send", someURL, "--to", anna})
	equal(t, send.has("--text"), false)
	equal(t, send.flag("--wait"), false)

	t.Setenv("SMSDOCK_FINGERPRINT", someFingerprint)
	status, _ := parse(allCommands(), []string{"status", someURL})
	equal(t, status.text("--fingerprint"), someFingerprint)
}

func TestOptionsBeforeAndAfterTheURL(t *testing.T) {
	environment(t)
	for _, words := range [][]string{
		{"messages", someURL, "--direction", "in", "--limit", "10"},
		{"messages", "--direction", "in", "--limit", "10", someURL},
		{"messages", "--direction=in", someURL, "--limit=10"},
		{"messages", "--limit", "10", "--direction", "in", "--", someURL},
	} {
		args, err := parse(allCommands(), words)
		if err != nil {
			t.Fatalf("%v: %v", words, err)
		}
		equal(t, args.positional, someURL)
		equal(t, args.text("--direction"), "in")
		equal(t, args.integer("--limit"), 10)
	}
	args, err := parse(allCommands(), []string{
		"listen", "--events", "sms.sent", "*", "--count", "2", someURL,
		"--reconnect-attempts", "-1",
	})
	if err != nil {
		t.Fatal(err)
	}
	equal(t, strings.Join(args.list("--events"), ","), "sms.sent,*")
	equal(t, args.integer("--count"), 2)
	equal(t, args.integer("--reconnect-attempts"), -1)
	// A text that begins with a dash is a value when it has a space.
	args, err = parse(allCommands(), []string{
		"send", someURL, "--to", anna, "--text", "-5 degrees",
	})
	if err != nil {
		t.Fatal(err)
	}
	equal(t, args.text("--text"), "-5 degrees")
}

func TestUsageErrors(t *testing.T) {
	environment(t)
	for _, words := range [][]string{
		{},
		{"unknown"},
		{"status"},
		{"status", someURL, "--password", "secret123"},
		{"status", someURL, "--fingerp", someFingerprint},
		{"status", someURL, "extra"},
		{"status", someURL, "--json=yes"},
		{"status", someURL, "--timeout"},
		{"status", someURL, "--timeout", "soon"},
		{"messages", someURL, "--direction", "sideways"},
		{"messages", someURL, "--limit", "0"},
		{"messages", someURL, "--limit", "many"},
		{"messages", someURL, "--since", "yesterday"},
		{"messages", someURL, "--sim", "first"},
		{"send", someURL, "--text", "no recipient"},
		{"delete", someURL},
		{"delete", someURL, "--message", "1", "--conversation", "2"},
		{"mark-read", someURL},
		{"mark-read", someURL, "--conversation", "first"},
		{"listen", someURL, "--events", "sms.unknown"},
		{"listen", someURL, "--events"},
		{"fingerprint"},
	} {
		got := run(t, words...)
		if got.code != 2 || got.out != "" ||
			!strings.HasPrefix(got.err, "usage: smsdock") ||
			!strings.Contains(got.err, "error: ") {
			t.Fatalf("%v: got %+v", words, got)
		}
	}
}

func TestHelp(t *testing.T) {
	environment(t)
	got := run(t, "--help")
	equal(t, got.code, 0)
	for _, name := range []string{
		"fingerprint", "status", "conversations", "messages", "send",
		"delete", "mark-read", "contacts", "listen", "SMSDOCK_PASSWORD",
	} {
		wantPart(t, got.out, name)
	}
	got = run(t, "send", "--help")
	equal(t, got.code, 0)
	wantPart(t, got.out, "--request-id ID")
	wantPart(t, got.out, "standard input")
	if strings.Contains(got.out, "--password ") {
		t.Fatal("there is an option for the password")
	}
	equal(t, run(t, "-h").code, 0)
}

func TestThePasswordIsNoArgument(t *testing.T) {
	environment(t)
	got := run(t, "status", someURL, "--fingerprint", someFingerprint,
		"--password", password)
	equal(t, got.code, 2)
	wantPart(t, got.err, "unrecognized arguments: --password "+password)
}

func TestRefusesOtherURLs(t *testing.T) {
	environment(t)
	for _, address := range []string{
		"ws://192.168.1.50:9443/ws", "https://192.168.1.50:8443/",
		"192.168.1.50",
	} {
		got := run(t, "status", address, "--fingerprint", someFingerprint)
		equal(t, got.code, 2)
		equal(t, got.out, "")
		wantPart(t, got.err, "wss://")
	}
}

func TestNeedsFingerprintAndPassword(t *testing.T) {
	environment(t)
	got := run(t, "status", someURL)
	equal(t, got.code, 2)
	wantPart(t, got.err, "--fingerprint")
	wantPart(t, got.err, "SMSDOCK_FINGERPRINT")

	got = run(t, "status", someURL, "--fingerprint", "AB:CD")
	equal(t, got.code, 2)
	wantPart(t, got.err, "SHA-256")

	t.Setenv("SMSDOCK_PASSWORD", "")
	got = run(t, "status", someURL, "--fingerprint", someFingerprint)
	equal(t, got.code, 2)
	equal(t, got.err,
		"smsdock: No password. Set the environment variable SMSDOCK_PASSWORD.\n")

	got = run(t, "status", someURL, "--fingerprint", someFingerprint,
		"--reconnect-attempts", "-2")
	equal(t, got.code, 2)
	got = run(t, "status", someURL, "--fingerprint", someFingerprint,
		"--timeout", "0")
	equal(t, got.code, 2)
}

func TestAsksForThePasswordOnATerminal(t *testing.T) {
	server := startServer(t, nil)
	t.Setenv("SMSDOCK_PASSWORD", "")
	prompts := []string{}
	app := &App{
		StdinIsTerminal: func() bool { return true },
		ReadPassword: func(prompt string) (string, error) {
			prompts = append(prompts, prompt)
			return password, nil
		},
	}
	got := runWith(t, app, commandLine(server, "status", "--json")...)
	equal(t, got.code, 0)
	equal(t, decodeJSON[map[string]any](t, got.out)["deleteMode"], any("hidden"))
	equal(t, strings.Join(prompts, "|"), "SMS Dock password: ")
	if strings.Contains(got.out+got.err, password) {
		t.Fatal("the password is shown")
	}
}

func TestOtherPasswordVariable(t *testing.T) {
	server := startServer(t, nil)
	t.Setenv("SMSDOCK_PASSWORD", "wrong-password")
	t.Setenv("OFFICE_PHONE", password)
	got := run(t, commandLine(server, "status", "--password-env", "OFFICE_PHONE")...)
	equal(t, got.code, 0)
}

func TestHostAndPort(t *testing.T) {
	for word, want := range map[string]string{
		"192.168.1.50":               "192.168.1.50 9443",
		"192.168.1.50:9555":          "192.168.1.50 9555",
		"phone.local:9443":           "phone.local 9443",
		"wss://192.168.1.50:9555/ws": "192.168.1.50 9555",
		"wss://192.168.1.50/ws":      "192.168.1.50 9443",
		"[fe80::1]:9555":             "fe80::1 9555",
		"fe80::1":                    "fe80::1 9443",
	} {
		host, port, err := hostAndPort(word)
		if err != nil {
			t.Fatalf("%s: %v", word, err)
		}
		equal(t, host+" "+strconv.Itoa(port), want)
	}
	for _, word := range []string{"", ":9443", "192.168.1.50:port"} {
		if _, _, err := hostAndPort(word); err == nil {
			t.Fatalf("%q: no error", word)
		}
	}
}

func TestPythonJSON(t *testing.T) {
	raw := json.RawMessage(
		`{"a":1,"b":[1,2.50,{"c":null}],"d":{},"e":[],"f":"Grüße \"x\"\n<\/>","g":true}`)
	flat, err := pythonJSON(raw, false)
	if err != nil {
		t.Fatal(err)
	}
	equal(t, flat, `{"a": 1, "b": [1, 2.50, {"c": null}], "d": {}, "e": [], `+
		`"f": "Grüße \"x\"\n</>", "g": true}`)
	indented, _ := pythonJSON(raw, true)
	equal(t, indented, `{
  "a": 1,
  "b": [
    1,
    2.50,
    {
      "c": null
    }
  ],
  "d": {},
  "e": [],
  "f": "Grüße \"x\"\n</>",
  "g": true
}`)
	_, err = pythonJSON(json.RawMessage(`{"a":`), true)
	equal(t, err != nil, true)
}

// ----------------------------------------------------------------------
// Commands
// ----------------------------------------------------------------------

func TestFingerprint(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, "fingerprint", "127.0.0.1:"+strconv.Itoa(server.Port()))
	equal(t, got.code, 0)
	equal(t, got.out, server.Fingerprint()+"\n")
	wantPart(t, got.err, "Compare")
	got = run(t, "fingerprint", server.URL())
	equal(t, got.code, 0)
	equal(t, got.out, server.Fingerprint()+"\n")
	equal(t, len(server.Requests()), 0)

	server.Stop()
	got = run(t, "fingerprint", "127.0.0.1:"+strconv.Itoa(server.Port()),
		"--timeout", "2")
	equal(t, got.code, 3)
	equal(t, got.out, "")
	equal(t, strings.HasPrefix(got.err, "smsdock: "), true)
	equal(t, run(t, "fingerprint", "host:port").code, 2)
}

func TestStatus(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "status")...)
	equal(t, got.code, 0)
	equal(t, got.err, "")
	equal(t, got.out, `App:             SMS Dock 1.0.0 (protocol 1)
Default SMS app: no
Delete mode:     hidden
Licence:         not enforced
Permissions:     all granted

SIM  SLOT  NAME  NUMBER          DEFAULT
1    0     Play                  yes
2    1     Work  +4369087654321  no
`)

	got = run(t, commandLine(server, "status", "--json")...)
	equal(t, got.code, 0)
	// The JSON is the one of the server, fields in their order.
	equal(t, strings.HasPrefix(got.out,
		"{\n  \"app\": {\n    \"name\": \"SMS Dock\",\n    \"version\": \"1.0.0\","), true)
	status := decodeJSON[map[string]any](t, got.out)
	equal(t, len(status["sims"].([]any)), 2)

	server.SetLicence(true, true)
	got = run(t, commandLine(server, "status")...)
	wantPart(t, got.out, "Licence:         full licence")
	server.AnswerNext("status", fakeserver.Obj(
		"app", fakeserver.Obj("name", "SMS Dock", "version", "1.0.0",
			"protocolVersion", 1),
		"sims", []any{},
		"permissions", fakeserver.Obj("sendSms", false, "readSms", true,
			"contacts", false),
		"defaultSmsApp", true, "deleteMode", "provider",
		"licence", fakeserver.Obj("enforced", true, "licensed", false,
			"dailyLimit", 10, "sentToday", 3, "remaining", 7),
		"popup", true))
	got = run(t, commandLine(server, "status")...)
	equal(t, got.out, `App:             SMS Dock 1.0.0 (protocol 1)
Default SMS app: yes
Delete mode:     provider
Licence:         free version, 3 of 10 SMS sent today
Permissions:     missing: contacts, sendSms
`)
}

func TestConversations(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "conversations")...)
	equal(t, got.code, 0)
	rows := lines(got.out)
	equal(t, len(rows), 4)
	fields(t, rows[0], "ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT")
	when := time.UnixMilli(baseTime + 24000).Format("2006-01-02 15:04")
	equal(t, rows[1],
		"170  "+when+"  +4369912345678  Anna Example  25    3       in   Anna 24")
	wantPart(t, rows[3], "A1")

	got = run(t, commandLine(server, "conversations", "--json", "--unread",
		"--search", "bill", "--limit", "5")...)
	equal(t, got.code, 0)
	found := decodeJSON[[]map[string]any](t, got.out)
	equal(t, len(found), 1)
	equal(t, found[0]["address"], any("A1"))
	got = run(t, commandLine(server, "conversations", "--sim", "2", "--json")...)
	found = decodeJSON[[]map[string]any](t, got.out)
	equal(t, found[0]["address"], any(ben))
}

func TestLastTenIncomingMessages(t *testing.T) {
	server := startServer(t, nil)
	// Options after the URL, as in the documentation.
	got := run(t, "messages", server.URL(), "--direction", "in", "--limit",
		"10", "--fingerprint", server.Fingerprint())
	equal(t, got.code, 0)
	rows := lines(got.out)
	equal(t, len(rows), 11)
	fields(t, rows[0], "ID", "TIME", "DIR", "NUMBER", "NAME", "STATUS", "TEXT")
	for _, row := range rows[1:] {
		wantPart(t, row, " in ")
	}
	equal(t, strings.HasSuffix(rows[1], "received  Anna 24"), true)
	equal(t, strings.HasSuffix(rows[10], "received  Anna 6"), true)
}

func TestMessagesJSONAndFilters(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "messages", "--json", "--address", ben,
		"--search", "invoice", "--direction", "out", "--sim", "2")...)
	equal(t, got.code, 0)
	messages := decodeJSON[[]map[string]any](t, got.out)
	equal(t, len(messages), 1)
	equal(t, messages[0]["body"], any("Your invoice is attached"))
	// Fields that do not apply are null, as the server sent them.
	wantPart(t, got.out, "\"sentTimestamp\": null,\n")
	request := server.RequestsOf("messages.list")[0]
	equal(t, request["address"], any(ben))
	equal(t, request["q"], any("invoice"))
	equal(t, request["direction"], any("out"))
	equal(t, request["sim"], any(2.0))

	got = run(t, commandLine(server, "messages", "--json", "--unread")...)
	equal(t, len(decodeJSON[[]any](t, got.out)), 4)
	got = run(t, commandLine(server, "messages", "--json", "--since", "1790672553000")...)
	equal(t, len(decodeJSON[[]any](t, got.out)), 3)
	got = run(t, commandLine(server, "messages", "--json", "--until",
		"1790672522000", "--conversation", "172")...)
	equal(t, len(decodeJSON[[]any](t, got.out)), 1)
}

func TestMessagesLimitOverOnePage(t *testing.T) {
	server := startServer(t, nil)
	for index := 0; index < 230; index++ {
		server.AddMessage(fakeserver.NewMessage{
			Address: anna, Body: fmt.Sprintf("More %d", index),
		})
	}
	got := run(t, commandLine(server, "messages", "--json", "--limit", "220")...)
	equal(t, got.code, 0)
	equal(t, len(decodeJSON[[]any](t, got.out)), 220)
	requests := server.RequestsOf("messages.list")
	equal(t, len(requests), 2)
	equal(t, requests[0]["limit"], any(200.0))
	equal(t, requests[1]["limit"], any(20.0))
}

func TestNothingFound(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "messages", "--search", "no such text")...)
	equal(t, got, result{0, "Nothing found.\n", ""})
	got = run(t, commandLine(server, "messages", "--search", "no such text", "--json")...)
	equal(t, got, result{0, "[]\n", ""})
}

func TestLongTextIsCutInTables(t *testing.T) {
	server := startServer(t, nil)
	server.AddMessage(fakeserver.NewMessage{
		Address: anna, Body: "First line\nsecond line " + strings.Repeat("ä", 100),
	})
	got := run(t, commandLine(server, "messages", "--limit", "1")...)
	equal(t, got.code, 0)
	row := lines(got.out)[1]
	wantPart(t, row, "First line second line äää")
	equal(t, strings.HasSuffix(row, "ä…"), true)
	text := row[strings.Index(row, "First"):]
	equal(t, len([]rune(text)), 60)
	got = run(t, commandLine(server, "messages", "--limit", "1", "--json")...)
	body := decodeJSON[[]map[string]any](t, got.out)[0]["body"].(string)
	equal(t, strings.HasPrefix(body, "First line\nsecond line"), true)
}

func TestSend(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "send", "--to", ben, "--text",
		"All systems running.")...)
	equal(t, got.code, 0)
	sent := server.Sent()
	equal(t, got.out,
		"Queued: "+ben+", 1 part(s), requestId "+sent[0].RequestID+"\n")
	equal(t, sent[0].Body, "All systems running.")
}

func TestSendGroup(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "send-group", "--to", ben, "--to", anna,
		"--text", "Hello all", "--request-id", "news-1", "--sim", "2")...)
	equal(t, got, result{0, "Group news-1: 2 recipient(s), 1 part(s) each\n" +
		"  Queued: " + ben + ", requestId news-1.1\n" +
		"  Queued: " + anna + ", requestId news-1.2\n", ""})
	sent := server.Sent()
	equal(t, len(sent), 2)
	equal(t, sent[0], fakeserver.SentMessage{
		RequestID: "news-1.1", To: ben, Body: "Hello all", Sim: 2, Group: "news-1",
	})
	// Both numbers after one --to work too.
	got = run(t, commandLine(server, "send-group", "--to", ben, anna,
		"--text", "Hello all", "--request-id", "news-1")...)
	equal(t, got.code, 0)
	equal(t, len(server.Sent()), 2)
}

func TestSendGroupAndWait(t *testing.T) {
	server := startServer(t, nil)
	server.FailNumbers(anna)
	got := run(t, commandLine(server, "send-group", "--to", ben, "--to", anna,
		"--text", "Hello all", "--request-id", "news-2", "--wait")...)
	equal(t, got, result{1, "Group news-2: 2 recipient(s), 1 part(s) each\n" +
		"  Sent: " + ben + ", requestId news-2.1\n" +
		"  Failed: " + anna + ", error code 1, requestId news-2.2\n",
		"smsdock: 1 of 2 message(s) failed\n"})
}

func TestSendGroupJSON(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "send-group", "--to", ben, "--text", "Hello",
		"--request-id", "news-3", "--json")...)
	equal(t, got.code, 0)
	printed := decodeJSON[map[string]any](t, got.out)
	equal(t, printed["requestId"], any("news-3"))
	equal(t, printed["status"], any("queued"))
	members := printed["messages"].([]any)
	equal(t, len(members), 1)
	equal(t, members[0].(map[string]any)["requestId"], any("news-3.1"))

	got = run(t, commandLine(server, "send-group", "--to", ben, "--text", "Hello",
		"--request-id", "news-4", "--json", "--wait")...)
	equal(t, got.code, 0)
	members = decodeJSON[map[string]any](t, got.out)["messages"].([]any)
	equal(t, members[0].(map[string]any)["status"], any("sent"))
	equal(t, members[0].(map[string]any)["partsSent"], any(1.0))
}

func TestSendGroupNeedsRecipients(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "send-group", "--text", "Hello")...)
	equal(t, got.code, 2)
	wantPart(t, got.err, "--to")
	equal(t, len(server.Sent()), 0)
}

func TestSendFromStandardInput(t *testing.T) {
	server := startServer(t, nil)
	app := &App{Stdin: strings.NewReader("Line one\nLine two\n")}
	got := runWith(t, app, commandLine(server, "send", "--to", ben, "--sim", "2",
		"--request-id", "cron-1", "--json")...)
	equal(t, got.code, 0)
	equal(t, got.out, `{
  "requestId": "cron-1",
  "to": "+4369087654321",
  "parts": 1,
  "status": "queued"
}
`)
	equal(t, server.Sent()[0], fakeserver.SentMessage{
		RequestID: "cron-1", To: ben, Body: "Line one\nLine two", Sim: 2,
	})
}

func TestSendNeedsAText(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "send", "--to", ben)...)
	equal(t, got.code, 2)
	wantPart(t, got.err, "empty")
	terminal := &App{StdinIsTerminal: func() bool { return true }}
	got = runWith(t, terminal, commandLine(server, "send", "--to", ben)...)
	equal(t, got.code, 2)
	wantPart(t, got.err, "--text")
	equal(t, len(server.Sent()), 0)
}

func TestSendAndWait(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "send", "--to", ben, "--text", "Hello", "--wait")...)
	equal(t, got.code, 0)
	equal(t, strings.HasPrefix(got.out, "Sent: "+ben+", 1 part(s), requestId "), true)

	got = run(t, commandLine(server, "send", "--to", ben, "--text", "Hello",
		"--wait", "--json")...)
	equal(t, got.code, 0)
	entry := decodeJSON[map[string]any](t, got.out)
	equal(t, entry["status"], any("sent"))
	equal(t, entry["partsSent"], any(1.0))
}

func TestSendAndWaitFailed(t *testing.T) {
	server := startServer(t, nil)
	server.FailNumbers("+43123456")
	got := run(t, commandLine(server, "send", "--to", "+43123456", "--text",
		"Hello", "--wait", "--request-id", "job-0")...)
	equal(t, got, result{1, "",
		"smsdock: Sending to +43123456 failed (error code 1, requestId job-0)\n"})

	got = run(t, commandLine(server, "send", "--to", "+43123456", "--text",
		"Hello", "--wait", "--json")...)
	equal(t, got.code, 1)
	equal(t, decodeJSON[map[string]any](t, got.out)["status"], any("failed"))
}

func TestSendAgainTellsTheOldOutcome(t *testing.T) {
	server := startServer(t, nil)
	server.FailNumbers("+43123456")
	words := commandLine(server, "send", "--to", "+43123456", "--text", "Hello",
		"--request-id", "job-1")
	got := run(t, words...)
	equal(t, got, result{0, "Queued: +43123456, 1 part(s), requestId job-1\n", ""})
	if !waitUntil(func() bool { return server.Outbox("job-1").Status == "failed" }) {
		t.Fatal("the message did not fail")
	}
	server.FailNumbers()
	// The repeat sends nothing and reports the failure of the first
	// attempt.
	got = run(t, words...)
	equal(t, got, result{1, "",
		"smsdock: Sending to +43123456 failed (requestId job-1)\n"})
	equal(t, len(server.Sent()), 1)

	fine := commandLine(server, "send", "--to", ben, "--text", "Hello",
		"--request-id", "job-2")
	equal(t, run(t, append(fine, "--wait")...).code, 0)
	got = run(t, fine...)
	equal(t, got, result{0, "Sent: " + ben + ", 1 part(s), requestId job-2\n", ""})
	equal(t, len(server.Sent()), 2)
}

func TestSendAndWaitTimesOut(t *testing.T) {
	server := startServer(t, &fakeserver.Options{SendDelay: 30 * time.Second})
	got := run(t, commandLine(server, "send", "--to", ben, "--text", "Hello",
		"--wait", "--wait-timeout", "0.3")...)
	equal(t, got.code, 3)
	wantPart(t, got.err, "No result")
}

func TestDelete(t *testing.T) {
	server := startServer(t, nil)
	newest := server.Messages()[24]
	got := run(t, commandLine(server, "delete", "--message",
		strconv.FormatInt(newest.ID, 10))...)
	equal(t, got, result{0,
		"Deleted 1 message(s): hidden in SMS Dock, still on the phone\n", ""})
	equal(t, len(server.Messages()), 28)

	server.SetDeleteMode("provider")
	conversation := strconv.FormatInt(newest.ConversationID, 10)
	got = run(t, commandLine(server, "delete", "--conversation", conversation)...)
	equal(t, got, result{0, "Deleted 24 message(s): removed from the phone\n", ""})
	other := strconv.FormatInt(server.Messages()[0].ConversationID, 10)
	got = run(t, commandLine(server, "delete", "--conversation", other, "--json")...)
	equal(t, got.out, "{\n  \"deleted\": 3,\n  \"mode\": \"provider\"\n}\n")
}

func TestMarkRead(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "mark-read", "--conversation", "170")...)
	equal(t, got.code, 1)
	equal(t, got.out, "")
	equal(t, strings.HasPrefix(got.err, "smsdock: not_default_app: "), true)

	server.SetDeleteMode("provider")
	got = run(t, commandLine(server, "mark-read", "--conversation", "170")...)
	equal(t, got, result{0, "Marked conversation 170 as read\n", ""})
	for _, message := range server.Messages() {
		if message.ConversationID == 170 && !message.Read {
			t.Fatal("a message is unread")
		}
	}
	got = run(t, commandLine(server, "mark-read", "--conversation", "170", "--json")...)
	equal(t, got, result{0, "{\n  \"ok\": true\n}\n", ""})
}

func TestContacts(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "contacts")...)
	equal(t, got, result{0, `NAME          NUMBER          LABEL
Anna Example  +4369912345678  Mobile
Ben Example   +4369087654321  Work
`, ""})
	got = run(t, commandLine(server, "contacts", "--search", "ben", "--json")...)
	equal(t, got.out, `[
  {
    "name": "Ben Example",
    "numbers": [
      {
        "number": "+4369087654321",
        "label": "Work"
      }
    ]
  }
]
`)
}

// incomingLater lets messages arrive as soon as a client has subscribed.
func incomingLater(server *fakeserver.Server, messages ...[2]string) {
	go func() {
		if waitUntil(func() bool {
			return len(server.RequestsOf("subscribe")) > 0
		}) {
			for _, message := range messages {
				server.ReceiveSMS(message[0], message[1])
			}
		}
	}()
}

func TestListen(t *testing.T) {
	server := startServer(t, nil)
	incomingLater(server, [2]string{anna, "STATUS"}, [2]string{"A1", "Your bill"})
	got := run(t, commandLine(server, "listen", "--count", "2")...)
	equal(t, got.code, 0)
	rows := lines(got.out)
	equal(t, len(rows), 2)
	today := time.Now().Format("2006-01-02 ")
	equal(t, strings.HasPrefix(rows[0], today), true)
	equal(t, strings.HasSuffix(rows[0], "  "+anna+" (Anna Example): STATUS"), true)
	equal(t, strings.HasSuffix(rows[1], "  A1: Your bill"), true)
	equal(t, got.err, "Listening for sms.received. Press Ctrl+C to stop.\n")
	request := server.RequestsOf("subscribe")[0]
	equal(t, fmt.Sprint(request["events"]), "[sms.received]")
	_, filtered := request["address"]
	equal(t, filtered, false)
}

func TestListenJSONWithAddress(t *testing.T) {
	server := startServer(t, nil)
	incomingLater(server, [2]string{anna, "ignored"}, [2]string{ben, "wanted"})
	got := run(t, commandLine(server, "listen", "--json", "--address", ben,
		"--count", "1")...)
	equal(t, got.code, 0)
	equal(t, got.err, "")
	equal(t, len(lines(got.out)), 1)
	// One line, written as the Python tool writes it.
	equal(t, strings.HasPrefix(got.out,
		`{"event": "sms.received", "seq": 2, "data": {"message": {"id": `), true)
	event := decodeJSON[map[string]any](t, got.out)
	message := event["data"].(map[string]any)["message"].(map[string]any)
	equal(t, message["body"], any("wanted"))
	equal(t, server.RequestsOf("subscribe")[0]["address"], any(ben))
}

func TestListenToOtherEvents(t *testing.T) {
	server := startServer(t, nil)
	go func() {
		if waitUntil(func() bool { return len(server.Requests()) >= 2 }) {
			server.Emit("sms.delivered", fakeserver.Obj(
				"requestId", "r1", "id", 7, "conversationId", 171, "to", ben))
			server.Emit("sms.failed", fakeserver.Obj(
				"requestId", nil, "id", nil, "conversationId", nil, "to", ben,
				"errorCode", 1, "reason", "generic_failure"))
			server.Emit("conversations.changed", fakeserver.Obj())
		}
	}()
	got := run(t, commandLine(server, "listen", "--events", "sms.delivered",
		"sms.failed", "conversations.changed", "--count", "3")...)
	equal(t, got.code, 0)
	rows := lines(got.out)
	equal(t, strings.HasSuffix(rows[0],
		"  sms.delivered requestId=r1 id=7 conversationId=171 to="+ben), true)
	equal(t, strings.HasSuffix(rows[1], "  sms.failed requestId=None id=None "+
		"conversationId=None to="+ben+" errorCode=1 reason=generic_failure"), true)
	equal(t, strings.HasSuffix(rows[2], "  conversations.changed"), true)
	equal(t, got.err, "Listening for sms.delivered, sms.failed, "+
		"conversations.changed. Press Ctrl+C to stop.\n")
}

func TestListenReportsAReconnect(t *testing.T) {
	server := startServer(t, nil)
	go func() {
		if !waitUntil(func() bool { return len(server.Requests()) >= 2 }) {
			return
		}
		server.DropConnections(0, "")
		waitUntil(func() bool { return server.ClientCount() == 0 })
		server.ReceiveSMS(anna, "lost")
		server.ReceiveSMS(anna, "lost too")
		if waitUntil(func() bool { return len(server.Requests()) >= 4 }) {
			server.Restart()
		}
		if waitUntil(func() bool { return len(server.Requests()) >= 6 }) {
			server.ReceiveSMS(anna, "received")
		}
	}()
	got := run(t, commandLine(server, "listen", "--count", "1")...)
	equal(t, got.code, 0)
	equal(t, strings.HasSuffix(got.out, ": received\n"), true)
	rows := lines(got.err)
	equal(t, len(rows), 3)
	equal(t, rows[1],
		"smsdock: reconnected; 2 event(s) of the phone were not received")
	equal(t, rows[2],
		"smsdock: reconnected; the app was restarted, events may be missing")
}

func TestListenStopsWithCtrlC(t *testing.T) {
	server := startServer(t, nil)
	ctx, cancel := context.WithCancel(context.Background())
	go func() {
		waitUntil(func() bool { return len(server.RequestsOf("subscribe")) > 0 })
		time.Sleep(50 * time.Millisecond)
		cancel()
	}()
	var out, errors bytes.Buffer
	app := &App{Stdin: strings.NewReader(""), Stdout: &out, Stderr: &errors}
	code := app.Run(ctx, commandLine(server, "listen"))
	equal(t, code, ExitInterrupted)
	equal(t, out.String(), "")
	equal(t, errors.String(), "Listening for sms.received. Press Ctrl+C to stop.\n")
}

// ----------------------------------------------------------------------
// Exit codes
// ----------------------------------------------------------------------

func TestExitCode1ForAPIErrors(t *testing.T) {
	server := startServer(t, nil)
	got := run(t, commandLine(server, "delete", "--message", "1")...)
	equal(t, got, result{1, "", "smsdock: not_found: No message with the id 1.\n"})

	got = run(t, commandLine(server, "send", "--to", "abc", "--text", "x")...)
	equal(t, got.code, 1)
	wantPart(t, got.err, "invalid_number")

	server.FailNext("messages.send", "quota_exceeded", "Daily limit reached.", 0)
	got = run(t, commandLine(server, "send", "--to", ben, "--text", "x")...)
	equal(t, got, result{1, "", "smsdock: quota_exceeded: Daily limit reached.\n"})
}

func TestExitCode1ForAWrongPasswordOrLicence(t *testing.T) {
	server := startServer(t, nil)
	t.Setenv("SMSDOCK_PASSWORD", "wrong-password")
	got := run(t, commandLine(server, "status")...)
	equal(t, got, result{1, "", "smsdock: invalid_password: Wrong password.\n"})
	equal(t, server.LoginCount(), 1)

	t.Setenv("SMSDOCK_PASSWORD", password)
	server.SetLicence(true, false)
	got = run(t, commandLine(server, "status")...)
	equal(t, got.code, 1)
	wantPart(t, got.err, "licence_required")
}

func TestExitCode3WhenNothingListens(t *testing.T) {
	server := startServer(t, nil)
	server.Stop()
	got := run(t, "status", server.URL(), "--fingerprint", someFingerprint,
		"--reconnect-attempts", "0")
	equal(t, got.code, 3)
	equal(t, got.out, "")
	wantPart(t, got.err, "smsdock: Cannot connect to "+server.URL())
}

func TestExitCode3ForAnotherCertificate(t *testing.T) {
	server := startServer(t, nil)
	other, err := fakeserver.NewCertificate()
	if err != nil {
		t.Fatal(err)
	}
	got := run(t, "status", server.URL(), "--fingerprint", other.Fingerprint)
	equal(t, got.code, 3)
	equal(t, got.out, "")
	wantPart(t, got.err, "expected: "+other.Fingerprint)
	wantPart(t, got.err, "received: "+server.Fingerprint())
	equal(t, len(server.Requests()), 0)
	equal(t, server.ConnectionCount(), 0)
}

func TestFingerprintFromTheEnvironmentIsUsed(t *testing.T) {
	server := startServer(t, nil)
	t.Setenv("SMSDOCK_FINGERPRINT", strings.ToLower(server.Fingerprint()))
	equal(t, run(t, "status", server.URL()).code, 0)
}

func TestInsecureWarns(t *testing.T) {
	server := startServer(t, nil)
	reader, writer, err := os.Pipe()
	if err != nil {
		t.Fatal(err)
	}
	original := os.Stderr
	os.Stderr = writer
	got := run(t, "status", server.URL(), "--insecure", "--timeout", "5")
	os.Stderr = original
	writer.Close()
	warning, _ := io.ReadAll(reader)
	equal(t, got.code, 0)
	equal(t, strings.HasPrefix(string(warning), "smsdock: WARNING:"), true)
	wantPart(t, string(warning), "not checked")
	wantPart(t, got.out, "App:")
}
