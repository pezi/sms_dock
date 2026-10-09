# SMS Dock Java client

This directory contains the Java library `smsdock-client` and the
command-line tool `smsdock` for the WebSocket API of the SMS Dock app,
protocol version 1. The protocol is described in [../API.md](../API.md).

With it you can:

- list conversations, messages and contacts, filtered and paged,
- send SMS and wait for the result,
- delete messages and conversations, mark conversations as read,
- run your own code for every incoming SMS.

The client pins the certificate of the phone, reconnects after a lost
connection, tells what it missed meanwhile and never sends a message
twice.

## Requirements and installation

- Java 17 or newer (JDK) and Maven 3.9
- The SMS Dock app with the full licence, its server running
- The phone and the computer in the same network

```bash
mvn -q package
```

This builds `target/smsdock.jar`, which contains the library, the
command-line tool and the only dependency,
[Gson](https://github.com/google/gson). The WebSocket and TLS come from the
JDK (`java.net.http`). The script `bin/smsdock` runs the tool from that
jar; put the directory `bin` on your `PATH` or call the jar yourself:

```bash
export PATH="$PWD/bin:$PATH"
smsdock --help
java -jar target/smsdock.jar --help      # the same
```

To use the library in another Maven project, install it into your local
repository with `mvn -q install` and add the dependency:

```xml
<dependency>
  <groupId>app.flutterdev</groupId>
  <artifactId>smsdock-client</artifactId>
  <version>0.1.0</version>
</dependency>
```

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

The commands, their options, their output and their exit codes are those
of the Python tool in [../python/](../python/README.md).

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
[ListenIncoming.java](examples/ListenIncoming.java) reads the missed
messages after a reconnect.

## Library usage

```java
import app.flutterdev.smsdock.MessageFilter;
import app.flutterdev.smsdock.SmsDockClient;
import java.time.Duration;
import java.util.List;
import java.util.Map;

// Password and fingerprint come from SMSDOCK_PASSWORD and SMSDOCK_FINGERPRINT.
try (SmsDockClient dock = new SmsDockClient("wss://192.168.1.50:9443/ws").connect()) {
    System.out.println(dock.status().get("sims"));

    // The last 10 incoming SMS
    Map<String, Object> page = dock.messages(new MessageFilter().direction("in"), 10);
    for (Object item : (List<?>) page.get("items")) {
        Map<?, ?> message = (Map<?, ?>) item;
        System.out.println(message.get("timestamp") + " " + message.get("address")
            + " " + message.get("body"));
    }

    // Send and wait for the result
    Map<String, Object> result = dock.send("+4369912345678", "All systems running.");
    Map<String, Object> outcome =
        dock.waitForResult((String) result.get("requestId"), Duration.ofSeconds(60));
    System.out.println(outcome.get("status"));        // "sent" or "failed"
}
```

Password and fingerprint can also be passed:

```java
SmsDockClient dock = SmsDockClient.builder("wss://192.168.1.50:9443/ws")
    .password(System.getenv("OFFICE_PHONE_PASSWORD"))
    .fingerprint("A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90:"
        + "A1:B2:C3:D4:E5:F6:07:18:29:3A:4B:5C:6D:7E:8F:90")
    .timeout(Duration.ofSeconds(30))
    .build();
dock.connect();
try {
    System.out.println(dock.ping());
} finally {
    dock.close();
}
```

Do not write the password into the source code.

All methods block until the answer is there. The client is not
thread-safe: use one client per thread.

### Constructor

`new SmsDockClient(url)` uses the defaults. `SmsDockClient.builder(url)`
returns a builder with one method per option; `build()` creates the client,
`connect()` creates it and connects.

| Option | Default | Meaning |
| --- | --- | --- |
| `url` | | `wss://` URL shown by the app. Anything else raises `IllegalArgumentException`. |
| `password(String)` | `SMSDOCK_PASSWORD` | Password set in the app |
| `fingerprint(String)` | `SMSDOCK_FINGERPRINT` | SHA-256 fingerprint of the certificate |
| `insecure(boolean)` | `false` | `true` skips the check of the certificate and prints a warning to standard error. For tests only. |
| `timeout(Duration)` | 30 s | Time to wait for a response |
| `connectTimeout(Duration)` | 10 s | Time to open the connection |
| `reconnectAttempts(int)` | `5` | Reconnect attempts after a lost connection; `0` for none |
| `reconnectWithoutLimit()` | | Reconnect without limit |
| `reconnectDelay(Duration)` | 1 s | Time before the first attempt; doubled for each further one |
| `maxReconnectDelay(Duration)` | 10 s | Upper limit of that delay |
| `eventQueueSize(int)` | `1000` | Events kept while nobody reads them; then the oldest are dropped |
| `rateLimitRetries(int)` | `2` | How often a request is repeated after the error `rate_limited` |
| `onReconnect(Consumer<SmsDockClient>)` | none | Function called with the client after every reconnect |

### Methods

Results are plain JSON values exactly as the server sent them: an object
is a `Map<String, Object>`, an array a `List<Object>`, an integer a `Long`,
`null` stays `null`. Their keys are described in
[../API.md](../API.md#objects).

| Method | Result |
| --- | --- |
| `connect()`, `close()` | Open and close the connection. `try (…)` closes; the first request connects if `connect()` was not called. |
| `status()` | Status |
| `ping()` | `{"time": milliseconds}` |
| `conversations()`, `conversations(filter, limit, cursor)` | Page of Conversation |
| `iterConversations(filter)`, `(filter, pageSize)`, `(filter, pageSize, maxItems)` | `Iterable` of Conversation |
| `messages()`, `messages(filter, limit)`, `messages(filter, limit, cursor)` | Page of Message |
| `iterMessages(filter)`, `(filter, pageSize)`, `(filter, pageSize, maxItems)` | `Iterable` of Message; same filters as `messages` |
| `getMessage(messageId)` | Message |
| `send(to, body)`, `send(to, body, sim, requestId)` | SendResult |
| `messageStatus(requestId)` | Outbox entry |
| `waitForResult(requestId)`, `(requestId, timeout)`, `(requestId, timeout, delivered, pollInterval)` | Outbox entry with the status `sent`, `delivered` or `failed` |
| `deleteMessage(messageId)` | `{"deleted": 1, "mode": "hidden"}` |
| `deleteConversation(conversationId)` | `{"deleted": 12, "mode": "hidden"}` |
| `markRead(conversationId)` | `{"ok": true}`; needs SMS Dock as default SMS app |
| `contacts()`, `contacts(query, limit, cursor)` | Page of Contact |
| `iterContacts(query)`, `(query, pageSize)`, `(query, pageSize, maxItems)` | `Iterable` of Contact |
| `subscribe(events...)`, `subscribe(events, address)` | Subscription now in force |
| `unsubscribe(events...)` | Subscription now in force |
| `events()`, `events(timeout)` | `Iterable` of events |
| `waitForEvent(name)`, `(name, timeout)`, `(names, timeout, predicate)` | The next event with this name |
| `request(action)`, `(action, fields)`, `(action, fields, retry, timeout)` | Result of any action |
| `sendRequest(action, fields)`, `waitForResponse(requestId, timeout)` | Several requests at once, see below |

Filters are objects with one method per field; `null` stands for no
filter:

| Class | Methods |
| --- | --- |
| `MessageFilter` | `address(String)`, `conversationId(long)`, `direction("in"\|"out"\|"all")`, `query(String)`, `since(long\|Instant)`, `until(long\|Instant)`, `sim(long)`, `unread(boolean)` |
| `ConversationFilter` | `query(String)`, `unread(boolean)`, `sim(long)` |

| Property | Meaning |
| --- | --- |
| `isConnected()` | Whether a connection is open |
| `getServerInfo()` | `{"name", "version", "protocolVersion"}` of the app |
| `getSubscription()` | The subscription in force |
| `getEpoch()` | Identifies one run of the app; from the last login |
| `getLatestSeq()` | Sequence number of the newest event the client knows of |
| `isServerRestarted()` | Whether the app was restarted before the last reconnect |
| `getMissedEvents()` | Events of the phone during the last interruption; `null` after a restart |
| `getPendingEvents()` | Events that wait in the queue |
| `getDroppedEvents()` | Events dropped because the queue was full |
| `getConnectionCount()` | Connections opened so far |

`address` compares numbers the way the phone does: `+43 699 1234567`,
`0043 699 1234567` and `0699 1234567` are the same. A number without
country code matches every number that ends with its digits (at least 7,
leading zeros not counted). Sender names such as `MyBank` are compared as
text, ignoring case. With both `address` and `conversationId`, the
conversation wins.

A page is `{"items": [...], "nextCursor": "…"}`. `nextCursor` is `null` on
the last page; pass it as `cursor` to get the next page. The iterators do
that for you, one request at a time:

```java
MessageFilter filter = new MessageFilter().address("+4369912345678").query("invoice");
for (Map<String, Object> message : dock.iterMessages(filter)) {
    System.out.println(message.get("body"));
}
```

`since` and `until` take milliseconds since the epoch or `Instant`
objects:

```java
Instant yesterday = Instant.now().minus(Duration.ofDays(1));
Map<String, Object> page =
    dock.messages(new MessageFilter().direction("in").since(yesterday), 50);
```

The class `SmsDock` holds the constants of the protocol (`EVENT_NAMES`,
`DEFAULT_API_PORT`, …) and the helpers `normalizeFingerprint`,
`formatFingerprint`, `certificateFingerprint` and `fetchFingerprint`.

### Sending without duplicates

`send()` returns as soon as the phone has queued the message. The outcome
arrives later; `waitForResult()` waits for it and returns the outbox
entry. Check its `status`: a failed message raises no exception.

The phone sends a message with a given `requestId` only once and
remembers the id for 30 days, also across restarts of the app. `send()`
creates a random `requestId` for every call. If the connection drops after
the request was sent and before the response arrived, the client
reconnects and repeats the request with the same `requestId`; the phone
does not send again.

Pass your own `requestId` when your program may run again after a crash:

```java
Map<String, Object> result = dock.send(
    "+4369912345678", "Order 4711 shipped.", null, "order-4711-shipped");
System.out.println(result.get("status"));     // "queued" the first time, later "sent"
```

A repeat returns the result of the first request with the status of now.
It never sends again, also not after the first attempt failed: the status
is `failed` then. To try again, use a new `requestId`. One `requestId`
stands for one message: using it for another recipient or text raises
`ApiException` with the code `invalid_request`.

The status of a message is `queued` (handed to the phone), `sending`
(some parts are left), `sent`, `delivered` or `failed`.

### Events and hooks

A new connection gets no events. Subscribe, then read them:

```java
try (SmsDockClient dock = SmsDockClient.builder("wss://192.168.1.50:9443/ws")
        .reconnectWithoutLimit().connect()) {
    dock.subscribe("sms.received");
    for (Map<String, Object> event : dock.events()) {
        Map<?, ?> message = (Map<?, ?>) ((Map<?, ?>) event.get("data")).get("message");
        System.out.println(message.get("address") + " " + message.get("body"));
    }
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

- `subscribe()` adds names to the subscription, `unsubscribe()` removes
  them. `subscribe("*")` asks for all events; the result lists their
  names.
- `subscribe(List.of("sms.received"), "+4369912345678")` limits the
  `sms.*` events to one number. Each `subscribe()` sets this limit anew:
  without `address` it is removed. Events that name no number always
  arrive.
- `id` and `conversationId` of `sms.sent`, `sms.delivered` and
  `sms.failed` are `null` as long as the phone has not stored the
  message.
- You can send requests inside the loop. Events that arrive between a
  request and its response are queued and delivered by `events()` or
  `waitForEvent()` afterwards, in the order of their arrival.
- `events(Duration.ofSeconds(5))` ends when no event arrived for 5
  seconds.
- The phone does not send events again. What happens while the
  connection is down is lost; see the next section.
- `seq` counts all events of the phone. A client that subscribed to some
  of them sees gaps.
- The client is not thread-safe. Use one client per thread.

### Reconnecting

When the connection is lost, the client reconnects, logs in again and
subscribes again to what was subscribed. It waits 1, 2, 4, 8 and then
10 seconds between the attempts and gives up after 5 attempts with
`ConnectionInterruptedException`. `reconnectAttempts` changes the number;
`reconnectWithoutLimit()` suits a program that listens for days.

After every reconnect the client knows what it missed:

| Property | After a reconnect |
| --- | --- |
| `getMissedEvents()` | Number of events the phone counted while the connection was down: all events, subscribed or not. `null` if the app was restarted. |
| `isServerRestarted()` | `true` if the app was restarted (`epoch` changed). Sequence numbers began at 1 again; the number of missed events is not known. |

`onReconnect` is called with the client after every reconnect, when the
subscription is in force again. Use it to read what was missed:

```java
final class Listener {
    private long newest;                                // timestamp of the newest message handled
    private final Set<Object> seen = new HashSet<>();   // ids of the messages handled

    void handle(Map<String, Object> message) {
        if (!seen.add(message.get("id"))) {
            return;
        }
        newest = Math.max(newest, (Long) message.get("timestamp"));
        System.out.println(message.get("address") + " " + message.get("body"));
    }

    void catchUp(SmsDockClient dock) {
        Long missedEvents = dock.getMissedEvents();
        if (dock.isServerRestarted() || missedEvents == null || missedEvents > 0) {
            List<Map<String, Object>> missed = new ArrayList<>();
            dock.iterMessages(new MessageFilter().direction("in").since(newest))
                .forEach(missed::add);
            Collections.reverse(missed);                // oldest first
            missed.forEach(this::handle);
        }
    }

    @SuppressWarnings("unchecked")
    void run() {
        try (SmsDockClient dock = SmsDockClient.builder("wss://192.168.1.50:9443/ws")
                .reconnectWithoutLimit().onReconnect(this::catchUp).connect()) {
            newest = (Long) dock.ping().get("time");
            dock.subscribe("sms.received");
            for (Map<String, Object> event : dock.events()) {
                Map<String, Object> data = (Map<String, Object>) event.get("data");
                handle((Map<String, Object>) data.get("message"));
            }
        }
    }
}
```

Requests are repeated after a reconnect. That is safe for all actions of
the API: reading changes nothing, and `send()` carries a `requestId`. One
case is visible: if the connection drops after the phone deleted a message
and before the response arrived, the repeated `deleteMessage()` fails
with `not_found`.

The client does not reconnect when the server closed the connection for a
reason that a new attempt cannot cure: wrong or changed password (close
codes 4401 and 4410), missing licence (4403), protocol error (4400),
frame larger than 64 KiB (1009). It also never repeats a login that
failed, because every wrong password counts towards the lock of the phone.

While the client waits and nothing arrives for 20 seconds, it sends a
WebSocket ping; a server that does not answer within 20 more seconds
counts as lost.

### Rate limit

The phone accepts 20 requests per second per connection. A request over
the limit is answered with the error `rate_limited` and `retryAfter`, the
seconds to wait. The client waits that long and repeats the request, at
most twice (`rateLimitRetries`); then it raises `ApiException`. A client
that ignores the error and keeps sending is disconnected with close code
4429.

The phone also sends at most 20 SMS parts per minute. `send()` over this
limit fails with `rate_limited` without `retryAfter`; the client raises
`ApiException` at once. Wait a minute, or spread your messages.

### Several requests at once

Requests are normally sent one after the other. `sendRequest()` sends a
request without waiting; `waitForResponse()` fetches the response. The
server may answer in any order; the client matches responses by `id`.

```java
String first = dock.sendRequest("conversations.list", Map.of("limit", 20));
String second = dock.sendRequest("contacts.list");
Map<String, Object> contacts = dock.waitForResponse(second);
Map<String, Object> conversations = dock.waitForResponse(first);
```

These two methods do not reconnect and do not repeat requests that were
refused with `rate_limited`.

### Errors

```
SmsDockException (a RuntimeException)
├── ApiException                    the server answered with an error
│   ├── AuthenticationException     invalid_password, locked_out, not_authenticated
│   └── LicenceRequiredException    licence_required
├── ProtocolException               the server sent something unexpected
├── ConnectionInterruptedException  the connection failed and could not be restored
└── CertificateMismatchException    the certificate has another fingerprint
```

| Method | Of | Meaning |
| --- | --- | --- |
| `getCode()` | `ApiException` | Error code of [API.md](../API.md#errors), for example `invalid_number` |
| `getServerMessage()` | `ApiException` | Text of the server; `getMessage()` is `code: text` |
| `getDetails()` | `ApiException` | Other fields of the error, for example `{"retryAfter": 30}` |
| `getRetryAfter()` | `ApiException` | Seconds to wait (`locked_out`, `rate_limited`), else `null` |
| `getPermission()` | `ApiException` | Missing Android permission (`permission_missing`), else `null` |
| `getCloseCode()`, `getCloseReason()` | `ConnectionInterruptedException` | Close frame of the server, if there was one |
| `getExpected()`, `getActual()` | `CertificateMismatchException` | The pinned fingerprint and the one of the server |

`IllegalArgumentException` is raised for wrong arguments before anything
is sent. `SmsDockTimeoutException` (a `RuntimeException`, not a
`SmsDockException`) is raised by `waitForEvent()`, `waitForResult()` and
`waitForResponse()` when the time is over. All exceptions are unchecked.

```java
try (SmsDockClient dock = new SmsDockClient("wss://192.168.1.50:9443/ws").connect()) {
    dock.send("+4369912345678", "Hello");
} catch (AuthenticationException error) {
    if (error.getCode().equals("locked_out")) {
        System.out.println("Locked. Try again in " + error.getRetryAfter() + " seconds.");
    } else {
        System.out.println("Wrong password.");
    }
} catch (ApiException error) {
    System.out.println("The phone refused: " + error.getCode() + ": "
        + error.getServerMessage());
} catch (SmsDockException error) {
    System.out.println("No connection: " + error.getMessage());
}
```

## Examples

The directory [examples/](examples) contains four short programs. Each
takes the URL as its first argument and reads `SMSDOCK_PASSWORD` and
`SMSDOCK_FINGERPRINT`.

| File | What it shows |
| --- | --- |
| [ListMessages.java](examples/ListMessages.java) | The last 10 incoming SMS |
| [SendSms.java](examples/SendSms.java) | Send one SMS and wait for the result |
| [ListenIncoming.java](examples/ListenIncoming.java) | Hook: call a method for every incoming SMS, also for those that arrived while the connection was down |
| [AutoReply.java](examples/AutoReply.java) | Hook that answers incoming SMS, with guards against reply loops |

Each is one source file that `java` runs directly, with the jar on the
class path:

```bash
java -cp target/smsdock.jar examples/ListMessages.java wss://192.168.1.50:9443/ws
java -cp target/smsdock.jar examples/SendSms.java wss://192.168.1.50:9443/ws +4369912345678 'Hello'
java -cp target/smsdock.jar examples/ListenIncoming.java wss://192.168.1.50:9443/ws
java -cp target/smsdock.jar examples/AutoReply.java wss://192.168.1.50:9443/ws
```

A program that answers SMS can end up in a loop with another machine that
answers SMS. `AutoReply.java` answers a number at most once in 10 minutes,
ignores senders that are not ordinary phone numbers (names such as `A1`,
short codes such as `8810`) and sends at most 20 answers per hour. Keep
such guards when you write your own hook.

## How the certificate is checked

1. The client opens the TLS connection with a trust manager of its own
   (`X509ExtendedTrustManager`). Host name and chain are not verified,
   because the certificate of the phone has neither a host name nor an
   issuer that could be checked.
2. Inside the TLS handshake the trust manager takes the certificate the
   server presented, computes the SHA-256 of its DER form and compares it
   with the pinned fingerprint in constant time
   (`MessageDigest.isEqual`). If they differ, the handshake fails and the
   client raises `CertificateMismatchException`. At that point nothing was
   sent, not even the WebSocket request.
3. Every connection, also each reconnect, uses a TLS context of its own
   and therefore does a full handshake with this check.

The pinned certificate is the only one accepted; the certificate store of
the system plays no part.

## Tests

The tests need no phone. They run against a fake server
([FakeSmsDock.java](src/test/java/app/flutterdev/smsdock/FakeSmsDock.java),
a port of the Python one) that speaks the protocol of API.md, over real
TLS with a throwaway certificate.

```bash
mvn -q verify
```

The certificate is created with the `keytool` command of the JDK. The
test dependencies are JUnit 5 and
[Java-WebSocket](https://github.com/TooTallNate/Java-WebSocket) for the
fake server; Maven fetches them.

To try the tool and the examples without a phone, run the fake server of
the Python client. It prints its URL, fingerprint and password:

```bash
python3 ../python/tests/fake_server.py --port 9443 --incoming-every 15
```

```bash
export SMSDOCK_PASSWORD='secret123'
export SMSDOCK_FINGERPRINT='<fingerprint printed by the fake server>'
smsdock listen wss://127.0.0.1:9443/ws
```

See [../API.md](../API.md) for the protocol and
[../asyncapi.yaml](../asyncapi.yaml) for its machine-readable form.
