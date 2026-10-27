# SMS Dock Go client

This directory contains the Go package `smsdock` and the command-line
tool `smsdock` for the WebSocket API of the SMS Dock app, protocol
version 1. The protocol is described in [../API.md](../API.md).

With it you can:

- list conversations, messages and contacts, filtered and paged,
- send SMS and wait for the result,
- delete messages and conversations, mark conversations as read,
- run your own code for every incoming SMS.

The client pins the certificate of the phone, reconnects after a lost
connection, tells what it missed meanwhile and never sends a message
twice.

## Requirements and installation

- Go 1.23 or newer
- The SMS Dock app with the full licence, its server running
- The phone and the computer in the same network

Build the command-line tool in this directory:

```bash
go build -o smsdock ./cmd/smsdock
```

The examples below call it as `smsdock`; put it into a directory of your
`PATH` or call it as `./smsdock`.

Or install it without a clone:

```bash
go install github.com/pezi/sms_dock/go/cmd/smsdock@latest
```

To use the package in your own module, add it with:

```bash
go get github.com/pezi/sms_dock/go
```

The package name is `smsdock`.

The dependencies are
[`github.com/gorilla/websocket`](https://pkg.go.dev/github.com/gorilla/websocket)
and, for the password prompt of the tool,
[`golang.org/x/term`](https://pkg.go.dev/golang.org/x/term).

## Password and fingerprint

Set the password without exposing it in process arguments:

```bash
export SMSDOCK_PASSWORD='your password'
```

The command-line tool has no option for the password. If the variable is
not set and the tool runs in a terminal, it asks for the password without
showing it.

The certificate of the phone is self-signed and names no host. The client
therefore checks the certificate by its SHA-256 fingerprint. Print the
fingerprint of the server:

```bash
smsdock fingerprint 192.168.1.50
```

```
A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90
```

This command trusts whoever answers at that address. **Compare its output
with the fingerprint on the Server screen of the app** and use it only if
both are the same. Then store it:

```bash
export SMSDOCK_FINGERPRINT='A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90'
```

The fingerprint may be written with or without colons, in upper or lower
case. `smsdock fingerprint` takes `HOST`, `HOST:PORT` or the `wss://` URL;
the default port is 9443.

## Commands

```
smsdock <command> <wss-url> [options]
```

Options may stand before or after the URL.

| Command | What it does |
| --- | --- |
| `fingerprint HOST[:PORT]` | Print the certificate fingerprint of a server |
| `status` | Show phone, SIMs, permissions and licence |
| `conversations` | List conversations, newest first |
| `messages` | List messages, newest first |
| `send` | Send an SMS |
| `delete` | Delete a message or a conversation |
| `mark-read` | Mark a conversation as read |
| `contacts` | List the contacts of the phone |
| `listen` | Print incoming SMS as they arrive |

Options of all commands except `fingerprint`:

| Option | Default | Meaning |
| --- | --- | --- |
| `--fingerprint HEX` | `SMSDOCK_FINGERPRINT` | SHA-256 fingerprint of the certificate |
| `--insecure` | off | Do not check the certificate. For tests only; prints a warning. |
| `--password-env NAME` | `SMSDOCK_PASSWORD` | Environment variable with the password |
| `--timeout SECONDS` | `30` | Time to wait for a response |
| `--reconnect-attempts N` | `5` | Reconnect attempts after a lost connection; `-1` means without limit |
| `--json` | off | Print JSON instead of text |

Exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | The server answered with an error, or the message failed (`send --wait`, or `send` with a request id used before) |
| 2 | Wrong usage |
| 3 | Connection or TLS problem, for example a certificate with another fingerprint |
| 130 | Stopped with Ctrl+C |

The tool prints the same text and the same JSON as the Python tool in
[../python](../python).

### status

```bash
smsdock status wss://192.168.1.50:9443/ws
```

```
App:             SMS Dock 1.0.0 (protocol 1)
Default SMS app: no
Delete mode:     hidden
Licence:         full licence
Permissions:     all granted

SIM  SLOT  NAME  NUMBER          DEFAULT
1    0     Play                  yes
2    1     Work  +4369087654321  no
```

The column `SIM` is the subscription id that the option `--sim` expects.

### conversations

```bash
smsdock conversations wss://192.168.1.50:9443/ws
smsdock conversations wss://192.168.1.50:9443/ws --unread
smsdock conversations wss://192.168.1.50:9443/ws --search invoice --limit 20
```

| Option | Meaning |
| --- | --- |
| `--search TEXT` | Search text, numbers and contact names |
| `--unread` | Only conversations with unread messages |
| `--sim ID` | Only this subscription id |
| `--limit N` | Number of conversations, default 50 |

### messages

The last 10 incoming SMS:

```bash
smsdock messages wss://192.168.1.50:9443/ws --direction in --limit 10
```

```
ID    TIME              DIR  NUMBER          NAME          STATUS    TEXT
2807  2026-09-29 12:50  in   +436641234567                 received  STATUS
2805  2026-09-29 09:45  in   +4369087654321  Ben Example   received  Thanks!
2803  2026-09-29 08:15  in   +4369912345678  Anna Example  received  Great, see you then.
```

All messages exchanged with one number that contain a word, as JSON:

```bash
smsdock messages wss://192.168.1.50:9443/ws \
  --address +4369912345678 --search friday --json
```

| Option | Meaning |
| --- | --- |
| `--address NUMBER` | Only messages from or to this number. `+43 699 1234567`, `0043 699 1234567` and `0699 1234567` are the same number. |
| `--conversation ID` | Only this conversation; wins if `--address` is given too |
| `--direction in\|out\|all` | Incoming, outgoing or both (default) |
| `--search TEXT` | Search the text |
| `--since TIME`, `--until TIME` | Time range: `2026-09-29`, `2026-09-29T08:00` (local time) or milliseconds since the epoch |
| `--unread` | Only unread messages |
| `--sim ID` | Only this subscription id |
| `--limit N` | Number of messages, default 50. More than 200 are read page by page. |

Tables show the text on one line, cut to 60 characters. `--json` prints
the complete objects.

### send

```bash
smsdock send wss://192.168.1.50:9443/ws \
  --to +4369912345678 --text 'All systems running.'
```

```
Queued: +4369912345678, 1 part(s), requestId e2784e0c831a42aa842da84c43a49da9
```

Spaces, dashes, dots, slashes and brackets in the number are ignored.

Without `--text` the text is read from standard input:

```bash
uptime | smsdock send wss://192.168.1.50:9443/ws --to +4369912345678 --wait
```

| Option | Meaning |
| --- | --- |
| `--to NUMBER` | Phone number of the recipient (required) |
| `--text TEXT` | Text of the message; without it standard input is read |
| `--sim ID` | Subscription id of the SIM; default is the SMS SIM of the phone |
| `--request-id ID` | Your own id, 1 to 64 characters of `A-Z a-z 0-9 . _ -`. The same id is sent only once in 30 days. |
| `--wait` | Wait until the message was sent or failed; a failure ends with exit code 1 |
| `--wait-timeout SECONDS` | Longest time to wait with `--wait`, default 60 |

A script that must not send the same message twice, for example a cron
job that may be started again after an error, passes its own id:

```bash
smsdock send wss://192.168.1.50:9443/ws --to +4369912345678 \
  --text 'Backup finished.' --request-id "backup-$(date +%Y-%m-%d)"
```

The second run sends nothing and prints the status of the first message,
for example `Sent: …`. If the first message failed, the second run fails
too (exit code 1); use a new id to try again. The same id with another
number or text is an error (`invalid_request`).

### send-group

```bash
smsdock send-group wss://192.168.1.50:9443/ws \
  --to +4369912345678 --to +436641112223 --text 'Meeting at 10.'
```

```
Group 3f9c…: 2 recipient(s), 1 part(s) each
  Queued: +4369912345678, requestId 3f9c….1
  Queued: +436641112223, requestId 3f9c….2
```

The same text goes to every number as a separate SMS; nobody sees the
other recipients. The options are those of `send`, with `--to` taking
several numbers or being repeated (at most 20). The phone checks the
whole group before it sends anything: a bad number, a text that is too
long, fewer messages left in the daily limit than recipients, or more
than 20 SMS parts in total (parts × recipients) refuse the group. A
recipient the phone refuses afterwards is printed as `Failed`; the others
are sent. With `--wait` the command waits for every message and ends with
exit code 1 if any of them failed. `--request-id` names the group; the
members get the ids `ID.1`, `ID.2` and so on, which `--json` prints.

### delete

```bash
smsdock delete wss://192.168.1.50:9443/ws --message 2813
smsdock delete wss://192.168.1.50:9443/ws --conversation 179
```

```
Deleted 1 message(s): hidden in SMS Dock, still on the phone
```

The ids are those of the columns `ID` of `messages` and `conversations`.
A message is removed from the phone only while SMS Dock is the phone's
default SMS app; otherwise it is hidden in SMS Dock.

### mark-read

```bash
smsdock mark-read wss://192.168.1.50:9443/ws --conversation 179
```

```
Marked conversation 179 as read
```

Android lets only the default SMS app change messages. If SMS Dock is not
the default SMS app of the phone, the command fails with
`not_default_app`.

### contacts

```bash
smsdock contacts wss://192.168.1.50:9443/ws --search anna
```

### listen

```bash
smsdock listen wss://192.168.1.50:9443/ws
```

```
Listening for sms.received. Press Ctrl+C to stop.
2026-09-29 12:55:30  +4369912345678 (Anna Example): STATUS
```

One JSON object per line, for another program to read:

```bash
smsdock listen wss://192.168.1.50:9443/ws --json --reconnect-attempts -1 \
  | while read -r event; do echo "$event" >> sms.log; done
```

| Option | Meaning |
| --- | --- |
| `--address NUMBER` | Only SMS events of this number |
| `--events EVENT ...` | Events to print, default `sms.received`; `*` means all |
| `--count N` | Stop after this many events |

After a lost connection `listen` reconnects and reports on standard error
what happened meanwhile:

```
smsdock: reconnected; 2 event(s) of the phone were not received
smsdock: reconnected; the app was restarted, events may be missing
```

The phone does not send events again. The example
[listen_incoming](examples/listen_incoming/main.go) reads the missed
messages after a reconnect.

## Library usage

```go
package main

import (
	"context"
	"fmt"
	"log"
	"time"

	smsdock "github.com/pezi/sms_dock/go"
)

func main() {
	ctx := context.Background()

	// Password and fingerprint come from SMSDOCK_PASSWORD and SMSDOCK_FINGERPRINT.
	dock, err := smsdock.Dial(ctx, "wss://192.168.1.50:9443/ws", nil)
	if err != nil {
		log.Fatal(err)
	}
	defer dock.Close()

	status, err := dock.Status(ctx)
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println(status.Sims)

	// The last 10 incoming SMS
	page, err := dock.Messages(ctx,
		smsdock.MessageFilter{Direction: "in"}, smsdock.PageOptions{Limit: 10})
	if err != nil {
		log.Fatal(err)
	}
	for _, message := range page.Items {
		fmt.Println(message.Timestamp, message.Address, message.Body)
	}

	// Send and wait for the result
	result, err := dock.Send(ctx, "+4369912345678", "All systems running.", nil)
	if err != nil {
		log.Fatal(err)
	}
	outcome, err := dock.WaitForResult(ctx, result.RequestID,
		&smsdock.WaitOptions{Timeout: time.Minute})
	if err != nil {
		log.Fatal(err)
	}
	fmt.Println(outcome.Status) // "sent" or "failed"
}
```

Password and fingerprint can also be passed:

```go
dock, err := smsdock.New("wss://192.168.1.50:9443/ws", &smsdock.Options{
	Password: os.Getenv("OFFICE_PHONE_PASSWORD"),
	Fingerprint: "A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:" +
		"A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90",
	Timeout: 30 * time.Second,
})
if err != nil {
	log.Fatal(err)
}
if err := dock.Connect(ctx); err != nil {
	log.Fatal(err)
}
defer dock.Close()
now, err := dock.Ping(ctx)
if err != nil {
	log.Fatal(err)
}
fmt.Println(time.UnixMilli(now.Time))
```

Do not write the password into the source code.

Every method that talks to the phone takes a `context.Context` first.
When the context is done, the method returns its error; a request whose
response is still missing is given up. The timeouts of `Options` apply in
any case.

### Constructor

`smsdock.New(url string, options *Options) (*Client, error)` makes a
client and sends nothing. `smsdock.Dial(ctx, url, options)` also connects
and logs in. `options` may be `nil`. The zero value of every field of
`Options` selects its default.

| Field | Default | Meaning |
| --- | --- | --- |
| `url` (argument) | | `wss://` URL shown by the app. Anything else is an `ArgumentError`. |
| `Password` | `SMSDOCK_PASSWORD` | Password set in the app |
| `Fingerprint` | `SMSDOCK_FINGERPRINT` | SHA-256 fingerprint of the certificate |
| `Insecure` | `false` | `true` skips the check of the certificate and prints a warning to standard error. For tests only. |
| `Timeout` | 30 s | Time to wait for a response |
| `ConnectTimeout` | 10 s | Time to open the connection |
| `ReconnectAttempts` | 5 | Reconnect attempts after a lost connection; `smsdock.NoReconnect` for none, `smsdock.ReconnectForever` for no limit |
| `ReconnectDelay` | 1 s | Time before the first attempt; doubled for each further one |
| `MaxReconnectDelay` | 10 s | Upper limit of that delay |
| `EventQueueSize` | 1000 | Events kept while nobody reads them; then the oldest are dropped |
| `RateLimitRetries` | 2 | How often a request is repeated after the error `rate_limited`; `smsdock.NoRateLimitRetries` for never |
| `OnReconnect` | none | Function called with the client after every reconnect |

### Methods

Results are structs that mirror the objects of
[../API.md](../API.md#objects). Fields that the server sends as `null`
are pointers and `nil` then. Results and list items also carry `Raw`, the
object exactly as the server sent it (`json.RawMessage`), with all its
fields.

| Method | Result |
| --- | --- |
| `Connect(ctx)`, `Close()` | Open and close the connection. `Dial` does the first. |
| `Status(ctx)` | `*Status` |
| `Ping(ctx)` | `*PingResult` with `Time`, in milliseconds |
| `Conversations(ctx, ConversationFilter{Query, Unread, Sim}, PageOptions{Limit, Cursor})` | `*Page[Conversation]` |
| `IterConversations(ctx, ConversationFilter{…}, IterOptions{PageSize, MaxItems})` | Iterator of `Conversation` |
| `Messages(ctx, MessageFilter{Address, ConversationID, Direction, Query, Since, Until, Sim, Unread}, PageOptions{Limit, Cursor})` | `*Page[Message]` |
| `IterMessages(ctx, MessageFilter{…}, IterOptions{PageSize, MaxItems})` | Iterator of `Message`; same filters as `Messages` |
| `GetMessage(ctx, messageID)` | `*Message` |
| `Send(ctx, to, body, *SendOptions{Sim, RequestID})` | `*SendResult` |
| `SendGroup(ctx, []string{to…}, body, *SendOptions{Sim, RequestID})` | `*GroupSendResult`: one SMS per number, at most 20 |
| `MessageStatus(ctx, requestID)` | `*OutboxEntry` |
| `WaitForResult(ctx, requestID, *WaitOptions{Timeout, Delivered, PollInterval})` | `*OutboxEntry` with the status `sent`, `delivered` or `failed` |
| `DeleteMessage(ctx, messageID)` | `*DeleteResult`: `{Deleted: 1, Mode: "hidden"}` |
| `DeleteConversation(ctx, conversationID)` | `*DeleteResult`: `{Deleted: 12, Mode: "hidden"}` |
| `MarkRead(ctx, conversationID)` | `*MarkReadResult`; needs SMS Dock as default SMS app |
| `Contacts(ctx, ContactFilter{Query}, PageOptions{Limit, Cursor})` | `*Page[Contact]` |
| `IterContacts(ctx, ContactFilter{Query}, IterOptions{PageSize, MaxItems})` | Iterator of `Contact` |
| `Subscribe(ctx, events...)`, `SubscribeAddress(ctx, address, events...)` | `*Subscription` now in force |
| `Unsubscribe(ctx, events...)` | `*Subscription` now in force |
| `Events(ctx, timeout)` | Iterator of `Event` |
| `WaitForEvent(ctx, timeout, match, names...)` | The next `Event` with one of these names |
| `Request(ctx, action, fields)` | Result of any action as `json.RawMessage` |
| `RequestOnce(ctx, action, fields)` | The same without a repeat after a lost connection or `rate_limited` |
| `SendRequest(ctx, action, fields)`, `WaitForResponse(ctx, requestID, timeout)` | Several requests at once, see below |

The zero value of a filter or of options selects everything and the
defaults: a page has 50 items, `MaxItems: 0` yields all items, a timeout
of 0 means `Options.Timeout`. Options given as a pointer may be `nil`.
`smsdock.Ptr(2)` makes the pointer that optional numbers such as
`MessageFilter.Sim` need.

| Method | Meaning |
| --- | --- |
| `Connected()` | Whether a connection is open |
| `ServerInfo()` | `*AppInfo`: name, version and protocol version of the app |
| `Subscription()` | The subscription in force, or `nil` |
| `Epoch()` | Identifies one run of the app; from the last login |
| `LatestSeq()` | Sequence number of the newest event the client knows of |
| `ServerRestarted()` | Whether the app was restarted before the last reconnect |
| `MissedEvents()` | Events of the phone during the last interruption; the second result is `false` after a restart |
| `PendingEvents()` | Events that wait in the queue |
| `DroppedEvents()` | Events dropped because the queue was full |
| `ConnectionCount()` | Connections opened so far |
| `SetOnReconnect(hook)` | Set the function called after every reconnect |

`Address` compares numbers the way the phone does: `+43 699 1234567`,
`0043 699 1234567` and `0699 1234567` are the same. A number without
country code matches every number that ends with its digits (at least 7,
leading zeros not counted). Sender names such as `MyBank` are compared as
text, ignoring case. With both `Address` and `ConversationID`, the
conversation wins.

A page has `Items` and `NextCursor`. `NextCursor` is `""` on the last
page; pass it as `Cursor` to get the next page. The iterators do that for
you. They yield an item and an error; after the first error the iteration
ends:

```go
filter := smsdock.MessageFilter{Address: "+4369912345678", Query: "invoice"}
for message, err := range dock.IterMessages(ctx, filter, smsdock.IterOptions{}) {
	if err != nil {
		return err
	}
	fmt.Println(message.Body)
}
```

`Since` and `Until` are `time.Time` values; `time.UnixMilli` converts a
timestamp of the API:

```go
yesterday := time.Now().Add(-24 * time.Hour)
page, err := dock.Messages(ctx,
	smsdock.MessageFilter{Direction: "in", Since: yesterday},
	smsdock.PageOptions{})
```

### Sending without duplicates

`Send()` returns as soon as the phone has queued the message. The outcome
arrives later; `WaitForResult()` waits for it and returns the outbox
entry. Check its `Status`: a failed message is no error.

The phone sends a message with a given `requestId` only once and
remembers the id for 30 days, also across restarts of the app. `Send()`
creates a random `requestId` for every call. If the connection drops after
the request was sent and before the response arrived, the client
reconnects and repeats the request with the same `requestId`; the phone
does not send again.

Pass your own `RequestID` when your program may run again after a crash:

```go
result, err := dock.Send(ctx, "+4369912345678", "Order 4711 shipped.",
	&smsdock.SendOptions{RequestID: "order-4711-shipped"})
if err != nil {
	return err
}
fmt.Println(result.Status) // "queued" the first time, later "sent"
```

A repeat returns the result of the first request with the status of now.
It never sends again, also not after the first attempt failed: the status
is `failed` then. To try again, use a new `RequestID`. One `RequestID`
stands for one message: using it for another recipient or text is an
`APIError` with the code `invalid_request`.

The status of a message is `queued` (handed to the phone), `sending`
(some parts are left), `sent`, `delivered` or `failed`.

### One text to several numbers

`SendGroup()` sends the same text to every number of a list as separate
SMS. The result names the group and holds one `SendResult` per
recipient in `Messages`; their request ids are `<RequestID>.1`,
`<RequestID>.2` and so on, and `WaitForResult()` and `MessageStatus()`
take them like those of single messages.

```go
result, err := dock.SendGroup(ctx, []string{"+4369912345678", "+436641112223"},
	"Meeting at 10.", &smsdock.SendOptions{RequestID: "meeting-1"})
if err != nil {
	return err
}
for _, member := range result.Messages {
	if member.Status == "failed" { // the phone refused this one at once
		fmt.Println(member.To, "refused")
		continue
	}
	outcome, err := dock.WaitForResult(ctx, member.RequestID, nil)
	if err != nil {
		return err
	}
	fmt.Println(member.To, outcome.Status)
}
```

The phone checks the whole group before the first SMS leaves: a number
that is not a phone number, a text that is too long, fewer messages left
in the daily limit of the free version than the group has recipients, or
more SMS parts than the 20 of one minute (parts × recipients) return an
`APIError` and nothing is sent. `Failed()` lists the members the phone
refused. A `RequestID` of a group has at most 61 characters and makes the
group safe to repeat, as for `Send()`.

### Events and hooks

A new connection gets no events. Subscribe, then read them:

```go
dock, err := smsdock.Dial(ctx, "wss://192.168.1.50:9443/ws", &smsdock.Options{
	ReconnectAttempts: smsdock.ReconnectForever,
})
if err != nil {
	return err
}
defer dock.Close()
if _, err := dock.Subscribe(ctx, "sms.received"); err != nil {
	return err
}
for event, err := range dock.Events(ctx, 0) {
	if err != nil {
		return err
	}
	message := event.Data.Message
	fmt.Println(message.Address, message.Body)
}
```

`Event` has `Name`, `Seq`, `Data` and `Raw`. `Data` is one struct for all
events; the fields an event does not have are `nil`:

| Event | Fields of `Data` |
| --- | --- |
| `sms.received` | `Message` |
| `sms.queued` | `RequestID`, `To`, `Parts`, `Sim`, `Source` |
| `sms.sent` | `RequestID`, `ConversationID`, `To`, `Message` |
| `sms.delivered` | `RequestID`, `ID`, `ConversationID`, `To` |
| `sms.failed` | `RequestID`, `ID`, `ConversationID`, `To`, `ErrorCode`, `Reason` |
| `sms.deleted` | `ID`, `ConversationID`, `Address`, `Mode` |
| `conversations.changed` | none |

- `Subscribe()` adds names to the subscription, `Unsubscribe()` removes
  them. `Subscribe(ctx, "*")` asks for all events; the result lists their
  names.
- `SubscribeAddress(ctx, "+4369912345678", "sms.received")` limits the
  `sms.*` events to one number. Each subscribe sets this limit anew:
  `Subscribe()` removes it. Events that name no number always arrive.
- `ID` and `ConversationID` of `sms.sent`, `sms.delivered` and
  `sms.failed` are `nil` as long as the phone has not stored the
  message.
- You can send requests inside the loop. Events that arrive between a
  request and its response are queued and delivered by `Events()` or
  `WaitForEvent()` afterwards, in the order of their arrival.
- `Events(ctx, 5*time.Second)` ends when no event arrived for 5 seconds;
  `Events(ctx, 0)` waits without limit. Cancel the context to end it from
  outside; the iterator then yields the error of the context.
- The phone does not send events again. What happens while the
  connection is down is lost; see the next section.
- `Seq` counts all events of the phone. A client that subscribed to some
  of them sees gaps.
- A `Client` is not safe for concurrent use: do not call its methods from
  several goroutines at once. Use one client per goroutine, or guard it
  with a mutex.

### Reconnecting

When the connection is lost, the client reconnects, logs in again and
subscribes again to what was subscribed. It waits 1, 2, 4, 8 and then
10 seconds between the attempts and gives up after 5 attempts with a
`ConnectionInterruptedError`. `ReconnectAttempts` changes the number;
`smsdock.ReconnectForever` means without limit, which suits a program
that listens for days.

After every reconnect the client knows what it missed:

| Method | After a reconnect |
| --- | --- |
| `MissedEvents()` | Number of events the phone counted while the connection was down: all events, subscribed or not. The second result is `false` if the app was restarted. |
| `ServerRestarted()` | `true` if the app was restarted (`epoch` changed). Sequence numbers began at 1 again; the number of missed events is not known. |

`OnReconnect` is called with the client after every reconnect, when the
subscription is in force again. Use it to read what was missed:

```go
var newest int64            // timestamp of the newest message handled
seen := map[int64]bool{}    // ids of the messages handled

handle := func(message *smsdock.Message) {
	if message.ID != nil {
		if seen[*message.ID] {
			return
		}
		seen[*message.ID] = true
	}
	newest = max(newest, message.Timestamp)
	fmt.Println(message.Address, message.Body)
}

catchUp := func(dock *smsdock.Client) {
	missed, known := dock.MissedEvents()
	if known && missed == 0 {
		return
	}
	var messages []smsdock.Message
	filter := smsdock.MessageFilter{Direction: "in", Since: time.UnixMilli(newest)}
	for message, err := range dock.IterMessages(ctx, filter, smsdock.IterOptions{}) {
		if err != nil {
			return
		}
		messages = append(messages, message)
	}
	for index := len(messages) - 1; index >= 0; index-- { // oldest first
		handle(&messages[index])
	}
}

dock, err := smsdock.Dial(ctx, "wss://192.168.1.50:9443/ws", &smsdock.Options{
	ReconnectAttempts: smsdock.ReconnectForever, OnReconnect: catchUp,
})
if err != nil {
	return err
}
defer dock.Close()
now, err := dock.Ping(ctx)
if err != nil {
	return err
}
newest = now.Time
if _, err := dock.Subscribe(ctx, "sms.received"); err != nil {
	return err
}
for event, err := range dock.Events(ctx, 0) {
	if err != nil {
		return err
	}
	handle(event.Data.Message)
}
```

Requests are repeated after a reconnect. That is safe for all actions of
the API: reading changes nothing, and `Send()` carries a `requestId`. One
case is visible: if the connection drops after the phone deleted a message
and before the response arrived, the repeated `DeleteMessage()` fails
with `not_found`.

The client does not reconnect when the server closed the connection for a
reason that a new attempt cannot cure: wrong or changed password (close
codes 4401 and 4410), missing licence (4403), protocol error (4400),
frame larger than 64 KiB (1009). It also never repeats a login that
failed, because every wrong password counts towards the lock of the phone.

### Rate limit

The phone accepts 20 requests per second per connection. A request over
the limit is answered with the error `rate_limited` and `retryAfter`, the
seconds to wait. The client waits that long and repeats the request, at
most twice (`RateLimitRetries`); then it returns the `APIError`. A client
that ignores the error and keeps sending is disconnected with close code
4429.

The phone also sends at most 20 SMS parts per minute. `Send()` over this
limit fails with `rate_limited` without `retryAfter`; the client returns
the `APIError` at once. Wait a minute, or spread your messages.

### Several requests at once

Requests are normally sent one after the other. `SendRequest()` sends a
request without waiting; `WaitForResponse()` fetches the response. The
server may answer in any order; the client matches responses by `id`.

```go
first, err := dock.SendRequest(ctx, "conversations.list", map[string]any{"limit": 20})
second, err := dock.SendRequest(ctx, "contacts.list", nil)
contacts, err := dock.WaitForResponse(ctx, second, 0)
conversations, err := dock.WaitForResponse(ctx, first, 0)
```

These two methods do not reconnect and do not repeat requests that were
refused with `rate_limited`.

### Errors

All errors of the package are pointers to one of these types; use
`errors.As` to tell them apart. `errors.Is(err, smsdock.ErrSMSDock)` is
true for all of them.

| Type | Meaning |
| --- | --- |
| `*APIError` | The server answered with an error |
| `*ProtocolError` | The server sent something unexpected |
| `*ConnectionInterruptedError` | The connection failed and could not be restored |
| `*CertificateMismatchError` | The certificate has another fingerprint |
| `*ArgumentError` | A wrong argument; returned before anything is sent |
| `*TimeoutError` | The time of `WaitForEvent()`, `WaitForResult()` or `WaitForResponse()` is over |

An `APIError` with the code `invalid_password`, `locked_out` or
`not_authenticated` is an authentication error:
`errors.Is(err, smsdock.ErrAuthentication)` and `IsAuthentication()` are
true. With the code `licence_required`,
`errors.Is(err, smsdock.ErrLicenceRequired)` and `IsLicenceRequired()`
are true.

| Field or method | Of | Meaning |
| --- | --- | --- |
| `Code` | `APIError` | Error code of [API.md](../API.md#errors), for example `invalid_number` |
| `Message` | `APIError` | Text of the server |
| `Details` | `APIError` | Other fields of the error, for example `{"retryAfter": 30}` |
| `RetryAfter()` | `APIError` | Seconds to wait (`locked_out`, `rate_limited`); the second result is `false` without them |
| `Permission()` | `APIError` | Missing Android permission (`permission_missing`), else `""` |
| `CloseCode`, `CloseReason` | `ConnectionInterruptedError` | Close frame of the server, if there was one; else `0` and `""` |
| `Expected`, `Actual` | `CertificateMismatchError` | The pinned fingerprint and the one of the server |

A method whose context is done returns the error of the context
(`context.Canceled` or `context.DeadlineExceeded`).

```go
dock, err := smsdock.Dial(ctx, "wss://192.168.1.50:9443/ws", nil)
if err == nil {
	defer dock.Close()
	_, err = dock.Send(ctx, "+4369912345678", "Hello", nil)
}
var refused *smsdock.APIError
switch {
case err == nil:
case errors.Is(err, smsdock.ErrAuthentication):
	errors.As(err, &refused)
	if seconds, locked := refused.RetryAfter(); locked {
		fmt.Printf("Locked. Try again in %d seconds.\n", seconds)
	} else {
		fmt.Println("Wrong password.")
	}
case errors.As(err, &refused):
	fmt.Printf("The phone refused: %s: %s\n", refused.Code, refused.Message)
default:
	fmt.Printf("No connection: %v\n", err)
}
```

Helper functions: `NormalizeFingerprint()`, `FormatFingerprint()` and
`CertificateFingerprint()` convert fingerprints; `FetchFingerprint(ctx,
host, port, timeout)` reads the fingerprint of a server as `smsdock
fingerprint` does.

## Examples

The directory [examples/](examples) contains five short programs. Each
takes the URL as its first argument and reads `SMSDOCK_PASSWORD` and
`SMSDOCK_FINGERPRINT`.

| Directory | What it shows |
| --- | --- |
| [list_messages](examples/list_messages/main.go) | The last 10 incoming SMS |
| [send_sms](examples/send_sms/main.go) | Send one SMS and wait for the result |
| [send_group_sms](examples/send_group_sms/main.go) | Send one text to several numbers, one SMS each, and wait for every result |
| [listen_incoming](examples/listen_incoming/main.go) | Hook: call a function for every incoming SMS, also for those that arrived while the connection was down |
| [auto_reply](examples/auto_reply/main.go) | Hook that answers incoming SMS, with guards against reply loops |

```bash
go run ./examples/list_messages wss://192.168.1.50:9443/ws
go run ./examples/send_sms wss://192.168.1.50:9443/ws +4369912345678 'Hello'
go run ./examples/send_group_sms wss://192.168.1.50:9443/ws 'Hello all' +4369912345678 +436641112223
go run ./examples/listen_incoming wss://192.168.1.50:9443/ws
go run ./examples/auto_reply wss://192.168.1.50:9443/ws
```

A program that answers SMS can end up in a loop with another machine that
answers SMS. `auto_reply` answers a number at most once in 10 minutes,
ignores senders that are not ordinary phone numbers (names such as `A1`,
short codes such as `8810`) and sends at most 20 answers per hour. Keep
such guards when you write your own hook.

## How the certificate is checked

1. The client opens the TLS connection without host name and chain
   verification (`InsecureSkipVerify`), because the certificate of the
   phone has neither a host name nor an issuer that could be checked.
2. During the TLS handshake (`VerifyConnection`) it takes the certificate
   the server presented, computes its SHA-256 and compares it with the
   pinned fingerprint in constant time. If they differ, the handshake
   fails and the client returns a `CertificateMismatchError`. At that
   point nothing was sent, not even the WebSocket request.
3. After the WebSocket is open and before the password is sent, it reads
   the certificate once more from the connection it is going to use and
   compares again.

The pinned certificate is the only one accepted; the certificate store of
the system plays no part.

## Tests

The tests need no phone. They run against a fake server
([internal/fakeserver](internal/fakeserver/fakeserver.go)) that speaks the
protocol of API.md, over real TLS with a throwaway certificate that is
created in the test process.

```bash
go test ./...
go test -race ./...
```

The fake server also runs on its own, to try the tool and the examples
without a phone. It prints its URL, fingerprint and password:

```bash
go run ./internal/cmd/fakeserver --port 9443 --incoming-every 15
```

```bash
export SMSDOCK_PASSWORD='secret123'
export SMSDOCK_FINGERPRINT='<fingerprint printed by the fake server>'
smsdock listen wss://127.0.0.1:9443/ws
```

See [../API.md](../API.md) for the protocol and
[../asyncapi.yaml](../asyncapi.yaml) for its machine-readable form.
