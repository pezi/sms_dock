# SMS Dock Dart client

This directory contains the Dart package `smsdock` and the command-line
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

The package is pure Dart and uses `dart:io`. It runs on the Dart VM and in
Flutter apps on Android, iOS, macOS, Windows and Linux, but not in a
browser.

## Requirements and installation

- Dart 3.5 or newer
- The SMS Dock app with the full licence, its server running
- The phone and the computer in the same network

```bash
dart pub get
dart run smsdock --help
```

`dart run smsdock` works inside this directory. To get a command `smsdock`
that works everywhere, compile it and put the file on your `PATH`:

```bash
dart compile exe bin/smsdock.dart -o smsdock
```

or let `dart pub` install it:

```bash
dart pub global activate --source path .
```

To use the library in your own package or Flutter app, add it to the
`pubspec.yaml` of that project:

```yaml
dependencies:
  smsdock:
    path: ../smscenter_github/dart
```

The only dependency is [`crypto`](https://pub.dev/packages/crypto), for
the SHA-256 of the certificate.

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

Commands, options, output and exit codes are those of the Python tool in
[../python](../python). Every command has `--help`.

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
[listen_incoming.dart](example/listen_incoming.dart) reads the missed
messages after a reconnect.

## Library usage

```dart
import 'dart:io';

import 'package:smsdock/smsdock.dart';

Future<void> main() async {
  // Password and fingerprint come from SMSDOCK_PASSWORD and
  // SMSDOCK_FINGERPRINT.
  final dock = SmsDockClient('wss://192.168.1.50:9443/ws');
  try {
    stdout.writeln((await dock.status()).sims);

    // The last 10 incoming SMS
    final page = await dock.messages(direction: 'in', limit: 10);
    for (final message in page.items) {
      stdout.writeln(
        '${message.timestamp} ${message.address} ${message.body}',
      );
    }

    // Send and wait for the result
    final result = await dock.send('+4369912345678', 'All systems running.');
    final outcome = await dock.waitForResult(
      result.requestId,
      timeout: const Duration(seconds: 60),
    );
    stdout.writeln(outcome.status); // "sent" or "failed"
  } finally {
    await dock.close();
  }
}
```

All methods that talk to the phone return futures. The first request
opens the connection; `connect()` does it ahead of time and makes exactly
one attempt. Always call `close()`: an open connection keeps the process
alive.

Password and fingerprint can also be passed. A Flutter app does that,
because it has no environment variables:

```dart
final dock = SmsDockClient(
  'wss://192.168.1.50:9443/ws',
  password: Platform.environment['OFFICE_PHONE_PASSWORD'],
  fingerprint:
      'A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:'
      'A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90',
  timeout: const Duration(seconds: 30),
);
```

Do not write the password into the source code.

Timeouts and delays are `Duration` objects. Timestamps of messages are
milliseconds since the epoch, as the phone sends them.

### Constructor

`SmsDockClient(url, {password, fingerprint, insecure = false, timeout = 30 s, connectTimeout = 10 s, reconnectAttempts = 5, reconnectDelay = 1 s, maxReconnectDelay = 10 s, eventQueueSize = 1000, rateLimitRetries = 2, onReconnect, environment})`

| Argument | Meaning |
| --- | --- |
| `url` | `wss://` URL shown by the app. Anything else throws `ArgumentError`. |
| `password` | Password set in the app; default is `SMSDOCK_PASSWORD` |
| `fingerprint` | SHA-256 fingerprint of the certificate; default is `SMSDOCK_FINGERPRINT` |
| `insecure` | `true` skips the check of the certificate and prints a warning to standard error. For tests only. |
| `timeout` | Time to wait for a response |
| `connectTimeout` | Time to open the connection |
| `reconnectAttempts` | Reconnect attempts after a lost connection; `0` for none, `null` for no limit |
| `reconnectDelay` | Time before the first attempt; doubled for each further one |
| `maxReconnectDelay` | Upper limit of that delay |
| `eventQueueSize` | Events kept while nobody reads them; then the oldest are dropped |
| `rateLimitRetries` | How often a request is repeated after the error `rate_limited` |
| `onReconnect` | Function called with the client after every reconnect; may be `async` |
| `environment` | Replaces the environment variables of the process; for tests |

### Methods

Results are the JSON objects exactly as the server sent them. Their keys
are described in [../API.md](../API.md#objects). The types `Message`,
`Conversation`, `Contact`, `Status`, `SendResult`, `OutboxEntry`, `Page`,
`SmsEvent` and so on are extension types on `Map<String, dynamic>`: they
add getters such as `message.body` and cost nothing at run time. Every
result is still a map, so `message['body']` and `jsonEncode(message)` work
too.

| Method | Result |
| --- | --- |
| `connect()`, `close()` | Open and close the connection |
| `status()` | `Status` |
| `ping()` | `PingResult` with `time` in milliseconds |
| `conversations({q, unread = false, sim, limit = 50, cursor})` | `Page<Conversation>` |
| `iterConversations({q, unread = false, sim, pageSize = 50, maxItems})` | `Stream<Conversation>` |
| `messages({address, conversationId, direction = 'all', q, since, until, sim, unread = false, limit = 50, cursor})` | `Page<Message>` |
| `iterMessages({…, pageSize = 50, maxItems})` | `Stream<Message>`; same filters as `messages` |
| `getMessage(messageId)` | `Message` |
| `send(to, body, {sim, requestId})` | `SendResult` |
| `messageStatus(requestId)` | `OutboxEntry` |
| `waitForResult(requestId, {timeout, delivered = false, pollInterval = 1 s})` | `OutboxEntry` with the status `sent`, `delivered` or `failed` |
| `deleteMessage(messageId)` | `DeleteResult`: `{"deleted": 1, "mode": "hidden"}` |
| `deleteConversation(conversationId)` | `DeleteResult`: `{"deleted": 12, "mode": "hidden"}` |
| `markRead(conversationId)` | `MarkReadResult`: `{"ok": true}`; needs SMS Dock as default SMS app |
| `contacts({q, limit = 50, cursor})` | `Page<Contact>` |
| `iterContacts({q, pageSize = 50, maxItems})` | `Stream<Contact>` |
| `subscribe(events, {address})` | `Subscription` now in force |
| `unsubscribe(events)` | `Subscription` now in force |
| `events({timeout})` | `Stream<SmsEvent>` |
| `waitForEvent(names, {timeout, predicate})` | The next `SmsEvent` with one of these names |
| `request(action, {fields, retry = true, timeout})` | Result of any action |
| `sendRequest(action, {fields})`, `waitForResponse(requestId, {timeout})` | Several requests at once, see below |

All methods except the `Stream` ones return futures. `events` and `names`
are lists of event names: `['sms.received']`, or `['*']` for all.

| Property | Meaning |
| --- | --- |
| `connected` | Whether a connection is open |
| `serverInfo` | `AppInfo` of the app: `name`, `version`, `protocolVersion` |
| `subscription` | The subscription in force |
| `epoch` | Identifies one run of the app; from the last login |
| `latestSeq` | Sequence number of the newest event the client knows of |
| `serverRestarted` | Whether the app was restarted before the last reconnect |
| `missedEvents` | Events of the phone during the last interruption; `null` after a restart |
| `pendingEvents` | Events that wait in the queue |
| `droppedEvents` | Events dropped because the queue was full |
| `connectionCount` | Connections opened so far |

`address` compares numbers the way the phone does: `+43 699 1234567`,
`0043 699 1234567` and `0699 1234567` are the same. A number without
country code matches every number that ends with its digits (at least 7,
leading zeros not counted). Sender names such as `MyBank` are compared as
text, ignoring case. With both `address` and `conversationId`, the
conversation wins.

A page has `items` and `nextCursor`. `nextCursor` is `null` on the last
page; pass it as `cursor` to get the next page. The streams do that for
you:

```dart
await for (final message in dock.iterMessages(
  address: '+4369912345678',
  q: 'invoice',
)) {
  stdout.writeln(message.body);
}
```

`since` and `until` take milliseconds since the epoch (`int`) or
`DateTime` objects:

```dart
final yesterday = DateTime.now().subtract(const Duration(days: 1));
final page = await dock.messages(direction: 'in', since: yesterday);
```

### Sending without duplicates

`send()` completes as soon as the phone has queued the message. The
outcome arrives later; `waitForResult()` waits for it and returns the
outbox entry. Check its `status`: a failed message throws nothing.

The phone sends a message with a given `requestId` only once and
remembers the id for 30 days, also across restarts of the app. `send()`
creates a random `requestId` for every call. If the connection drops after
the request was sent and before the response arrived, the client
reconnects and repeats the request with the same `requestId`; the phone
does not send again.

Pass your own `requestId` when your program may run again after a crash:

```dart
final result = await dock.send(
  '+4369912345678',
  'Order 4711 shipped.',
  requestId: 'order-4711-shipped',
);
stdout.writeln(result.status); // "queued" the first time, later "sent"
```

A repeat returns the result of the first request with the status of now.
It never sends again, also not after the first attempt failed: the status
is `failed` then. To try again, use a new `requestId`. One `requestId`
stands for one message: using it for another recipient or text throws
`ApiError` with the code `invalid_request`.

The status of a message is `queued` (handed to the phone), `sending`
(some parts are left), `sent`, `delivered` or `failed`.

### Events and hooks

A new connection gets no events. Subscribe, then read them:

```dart
final dock = SmsDockClient(
  'wss://192.168.1.50:9443/ws',
  reconnectAttempts: null,
);
await dock.subscribe(['sms.received']);
await for (final event in dock.events()) {
  final message = event.message!;
  stdout.writeln('${message.address} ${message.body}');
}
```

| Event | `data` |
| --- | --- |
| `sms.received` | `{"message": Message}` |
| `sms.queued` | `{"requestId", "to", "parts", "sim", "source"}` |
| `sms.sent` | `{"requestId", "conversationId", "to", "message"}` |
| `sms.delivered` | `{"requestId", "id", "conversationId", "to"}` |
| `sms.failed` | `{"requestId", "id", "conversationId", "to", "errorCode", "reason"}` |
| `sms.deleted` | `{"id", "conversationId", "address", "mode"}` |
| `conversations.changed` | `{}` |

An `SmsEvent` has the getters `name`, `seq`, `data` and `message` (the
message of `sms.received` and `sms.sent`, else `null`).

- `subscribe()` adds names to the subscription, `unsubscribe()` removes
  them. `subscribe(['*'])` asks for all events; the result lists their
  names.
- `subscribe(['sms.received'], address: '+4369912345678')` limits the
  `sms.*` events to one number. Each `subscribe()` sets this limit anew:
  without `address` it is removed. Events that name no number always
  arrive.
- `id` and `conversationId` of `sms.sent`, `sms.delivered` and
  `sms.failed` are `null` as long as the phone has not stored the
  message.
- You can send requests inside the loop. Events that arrive meanwhile are
  queued and delivered by `events()` or `waitForEvent()` afterwards, in
  the order of their arrival.
- `events(timeout: const Duration(seconds: 5))` ends when no event
  arrived for 5 seconds. `close()` ends a running stream quietly, which
  suits a handler for Ctrl+C.
- Every event arrives in the queue first; while nobody reads, at most
  `eventQueueSize` are kept. Each event is handed to one reader: run one
  `events()` loop per client. Inside the body of `await for` the stream is
  paused and events stay in the queue.
- The phone does not send events again. What happens while the
  connection is down is lost; see the next section.
- `seq` counts all events of the phone. A client that subscribed to some
  of them sees gaps.

### Reconnecting

When the connection is lost, the client reconnects, logs in again and
subscribes again to what was subscribed. It waits 1, 2, 4, 8 and then
10 seconds between the attempts and gives up after 5 attempts with
`ConnectionInterrupted`. `reconnectAttempts` changes the number; `null`
means without limit, which suits a program that listens for days.

After every reconnect the client knows what it missed:

| Property | After a reconnect |
| --- | --- |
| `missedEvents` | Number of events the phone counted while the connection was down: all events, subscribed or not. `null` if the app was restarted. |
| `serverRestarted` | `true` if the app was restarted (`epoch` changed). Sequence numbers began at 1 again; the number of missed events is not known. |

`onReconnect` is called with the client after every reconnect, when the
subscription is in force again. It may be an `async` function; the client
waits for it before it repeats the interrupted request or hands out the
next event. Requests made inside the hook work. Use it to read what was
missed:

```dart
var newest = 0; // timestamp of the newest message handled
final seen = <int?>{}; // ids of the messages handled

void handle(Message message) {
  if (!seen.add(message.id)) return;
  if (message.timestamp > newest) newest = message.timestamp;
  stdout.writeln('${message.address} ${message.body}');
}

Future<void> catchUp(SmsDockClient dock) async {
  if (dock.serverRestarted || dock.missedEvents != 0) {
    final missed = await dock
        .iterMessages(direction: 'in', since: newest)
        .toList();
    missed.reversed.forEach(handle); // oldest first
  }
}

final dock = SmsDockClient(
  'wss://192.168.1.50:9443/ws',
  reconnectAttempts: null,
  onReconnect: catchUp,
);
newest = (await dock.ping()).time;
await dock.subscribe(['sms.received']);
await for (final event in dock.events()) {
  handle(event.message!);
}
```

Requests are repeated after a reconnect. That is safe for all actions of
the API: reading changes nothing, and `send()` carries a `requestId`. One
case is visible: if the connection drops after the phone deleted a message
and before the response arrived, the repeated `deleteMessage()` fails
with `not_found`.

A request that gets no response within `timeout` counts as a lost
connection: the client drops the connection, reconnects and repeats it.

The client does not reconnect when the server closed the connection for a
reason that a new attempt cannot cure: wrong or changed password (close
codes 4401 and 4410), missing licence (4403), protocol error (4400),
frame larger than 64 KiB (1009). It also never repeats a login that
failed, because every wrong password counts towards the lock of the phone.

### Rate limit

The phone accepts 20 requests per second per connection. A request over
the limit is answered with the error `rate_limited` and `retryAfter`, the
seconds to wait. The client waits that long and repeats the request, at
most twice (`rateLimitRetries`); then it throws `ApiError`. A client that
ignores the error and keeps sending is disconnected with close code 4429.

The phone also sends at most 20 SMS parts per minute. `send()` over this
limit fails with `rate_limited` without `retryAfter`; the client throws
`ApiError` at once. Wait a minute, or spread your messages.

### Several requests at once

Dart runs one thing at a time, but several requests may be on their way
at once. That simply works: every request carries its own id, the server
may answer in any order, and the client matches responses by `id`.

```dart
final (conversations, contacts) = await (
  dock.conversations(limit: 20),
  dock.contacts(),
).wait;
```

Requests that are awaited together share one connection and, after a lost
connection, one reconnect; each of them is then repeated. They also share
the limit of 20 requests per second.

`sendRequest()` and `waitForResponse()` give control of each frame:
`sendRequest()` sends a request without waiting and completes with its
id, `waitForResponse()` fetches the response.

```dart
final first = await dock.sendRequest(
  'conversations.list',
  fields: {'limit': 20},
);
final second = await dock.sendRequest('contacts.list');
final contacts = await dock.waitForResponse(second);
final conversations = await dock.waitForResponse(first);
```

These two methods do not reconnect and do not repeat requests that were
refused with `rate_limited`.

### Errors

```
SmsDockError
├── ApiError                  the server answered with an error
│   ├── AuthenticationError   invalid_password, locked_out, not_authenticated
│   └── LicenceRequiredError  licence_required
├── ProtocolError             the server sent something unexpected
├── ConnectionInterrupted     the connection failed and could not be restored
└── CertificateMismatchError  the certificate has another fingerprint
```

All of them implement `Exception`; `toString()` returns the text of the
error without a class name.

| Property | Of | Meaning |
| --- | --- | --- |
| `code` | `ApiError` | Error code of [API.md](../API.md#errors), for example `invalid_number` |
| `message` | `ApiError` | Text of the server |
| `details` | `ApiError` | Other fields of the error, for example `{"retryAfter": 30}` |
| `retryAfter` | `ApiError` | Seconds to wait (`locked_out`, `rate_limited`), else `null` |
| `permission` | `ApiError` | Missing Android permission (`permission_missing`), else `null` |
| `closeCode`, `closeReason` | `ConnectionInterrupted` | Close frame of the server, if there was one |
| `expected`, `actual` | `CertificateMismatchError` | The pinned fingerprint and the one of the server |

`ArgumentError` is thrown for wrong arguments before anything is sent.
`TimeoutException` of `dart:async` is thrown by `waitForEvent()`,
`waitForResult()` and `waitForResponse()` when the time is over.

Error messages never contain the password or the text of a message.

```dart
final dock = SmsDockClient('wss://192.168.1.50:9443/ws');
try {
  await dock.send('+4369912345678', 'Hello');
} on AuthenticationError catch (error) {
  if (error.code == 'locked_out') {
    stdout.writeln('Locked. Try again in ${error.retryAfter} seconds.');
  } else {
    stdout.writeln('Wrong password.');
  }
} on ApiError catch (error) {
  stdout.writeln('The phone refused: ${error.code}: ${error.message}');
} on SmsDockError catch (error) {
  stdout.writeln('No connection: $error');
} finally {
  await dock.close();
}
```

## Examples

The directory [example/](example) contains four short programs. Each
takes the URL as its first argument and reads `SMSDOCK_PASSWORD` and
`SMSDOCK_FINGERPRINT`.

| File | What it shows |
| --- | --- |
| [list_messages.dart](example/list_messages.dart) | The last 10 incoming SMS |
| [send_sms.dart](example/send_sms.dart) | Send one SMS and wait for the result |
| [listen_incoming.dart](example/listen_incoming.dart) | Hook: call a function for every incoming SMS, also for those that arrived while the connection was down |
| [auto_reply.dart](example/auto_reply.dart) | Hook that answers incoming SMS, with guards against reply loops |

```bash
dart run example/list_messages.dart wss://192.168.1.50:9443/ws
dart run example/send_sms.dart wss://192.168.1.50:9443/ws +4369912345678 'Hello'
dart run example/listen_incoming.dart wss://192.168.1.50:9443/ws
dart run example/auto_reply.dart wss://192.168.1.50:9443/ws
```

A program that answers SMS can end up in a loop with another machine that
answers SMS. `auto_reply.dart` answers a number at most once in 10
minutes, ignores senders that are not ordinary phone numbers (names such
as `A1`, short codes such as `8810`) and sends at most 20 answers per
hour. Keep such guards when you write your own hook.

## How the certificate is checked

1. The client opens the TLS connection itself, with a security context
   that trusts no certificate authority. `dart:io` then asks the client
   about every certificate, whoever signed it. Host name and chain play
   no part, because the certificate of the phone has neither a host name
   nor an issuer that could be checked.
2. Inside the TLS handshake the client computes the SHA-256 of the
   certificate the server presented and compares it with the pinned
   fingerprint in constant time. If they differ, the handshake is refused
   and `CertificateMismatchError` is thrown. At that point nothing was
   sent, not even the WebSocket request.
3. After the handshake and before the connection is handed to the
   WebSocket code, the client reads the certificate once more from the
   connection it is going to use and compares again. Only then the
   WebSocket request and, later, the password are written.

The pinned certificate is the only one accepted; the certificate store of
the system plays no part.

## Tests

The tests need no phone. They run against a fake server
([test/fake_server.dart](test/fake_server.dart)) that speaks the protocol
of API.md, over real TLS with a throwaway certificate.

```bash
dart pub get
dart test
```

The certificate is created with the `openssl` command in a temporary
directory that is deleted afterwards. Without `openssl` the tests that
need a server are skipped.

The fake server also runs on its own, to try the tool and the examples
without a phone. It prints its URL, fingerprint and password:

```bash
dart run test/fake_server.dart --port 9443 --incoming-every 15
```

```bash
export SMSDOCK_PASSWORD='secret123'
export SMSDOCK_FINGERPRINT='<fingerprint printed by the fake server>'
smsdock listen wss://127.0.0.1:9443/ws
```

See [../API.md](../API.md) for the protocol and
[../asyncapi.yaml](../asyncapi.yaml) for its machine-readable form.
