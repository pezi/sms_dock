# SMS Dock WebSocket API

Protocol version: **1**

SMS Dock turns an Android phone into an SMS hub. This API lets programs
read, send and delete SMS and react to incoming ones.

- Endpoint: `wss://PHONE_IP:9443/ws` (the port can be changed in the app).
- Transport: WebSocket over TLS. Text frames only, each one UTF-8 JSON
  object. Frames sent by the client may have at most 64 KiB; answers of the
  server can be larger.
- The API needs the full licence of the app.
- At most 8 clients can be connected at once.

## Contents

1. [Certificate](#certificate)
2. [Connecting and authenticating](#connecting-and-authenticating)
3. [Requests and responses](#requests-and-responses)
4. [Actions](#actions)
5. [Phone numbers](#phone-numbers)
6. [Events](#events)
7. [Objects](#objects)
8. [Errors](#errors)
9. [Close codes](#close-codes)
10. [Limits](#limits)

## Certificate

The phone uses a self-signed certificate that is created on first start and
never leaves the phone. It names no host or address, so ordinary
verification cannot succeed. Clients **pin** it instead: they compare the
SHA-256 fingerprint of the certificate the server presents with the
fingerprint shown in the app (Server screen), and only then send the
password.

Read the fingerprint from a computer and compare it with the app:

```bash
openssl s_client -connect 192.168.1.50:9443 </dev/null 2>/dev/null \
  | openssl x509 -noout -fingerprint -sha256
```

Both ports of the phone use the same certificate. It changes only when
"Regenerate certificate" is used in the app or the app is reinstalled.

## Connecting and authenticating

After the WebSocket opens, the server sends:

```json
{"event":"hello","protocolVersion":1,"authenticationRequired":true}
```

The client must answer within 10 seconds with the password that is set in
the app (the same one the web interface uses):

```json
{"id":"1","action":"authenticate","password":"…"}
```

```json
{"id":"1","ok":true,"result":{"protocolVersion":1,"app":{"name":"SMS Dock","version":"1.0.0","protocolVersion":1},"latestSeq":42,"epoch":1790672000000}}
```

`latestSeq` is the sequence number of the newest [event](#events) so far.
`epoch` identifies one run of the app: when it differs from the one of the
previous connection, the app was restarted and sequence numbers began at 1
again. `authenticationRequired` is always `true`.

On a wrong password the server answers with the error `invalid_password`
and closes the connection with code 4401. After 5 wrong passwords (web
interface and API together) logins are locked for 30 seconds, doubling up
to 15 minutes. The error is then `locked_out` with the seconds to wait, and
the connection is closed with code 4401 as well:

```json
{"id":"1","ok":false,"error":{"code":"locked_out","message":"Too many attempts. Try again in 30 seconds.","retryAfter":30}}
```

Without a licence the answer to `authenticate` is the error
`licence_required`, followed by close code 4403. The licence is checked
before the password.

Any other action before authentication is answered with
`not_authenticated`.

## Requests and responses

A request is an object with an `action` and, optionally, an `id` (string or
number) that the response echoes. Every response has an `id`; it is `null`
if the request had none or could not be read. Other fields depend on the
action.

```json
{"id":"7","action":"messages.list","direction":"in","limit":10}
```

Success:

```json
{"id":"7","ok":true,"result":{"items":[],"nextCursor":null}}
```

Failure:

```json
{"id":"7","ok":false,"error":{"code":"invalid_request","message":"'limit' must be between 1 and 200."}}
```

Responses may arrive in a different order than the requests were sent, and
events may arrive in between; match responses by `id`.

## Actions

| Action | Purpose |
|---|---|
| [`authenticate`](#connecting-and-authenticating) | Log in |
| [`status`](#status) | Phone, SIMs, permissions, limits |
| [`conversations.list`](#conversationslist) | Conversations, one per phone number |
| [`messages.list`](#messageslist) | Messages, filtered and paged |
| [`messages.get`](#messagesget) | One message |
| [`messages.send`](#messagessend) | Send an SMS |
| [`messages.sendGroup`](#messagessendgroup) | Send one text to several numbers, one SMS each |
| [`messages.status`](#messagesstatus) | State of a sent SMS |
| [`messages.delete`](#messagesdelete) | Delete one message |
| [`conversations.delete`](#conversationsdelete) | Delete a conversation |
| [`conversations.markRead`](#conversationsmarkread) | Mark a conversation as read |
| [`contacts.list`](#contactslist) | Contacts of the phone |
| [`subscribe`](#subscribe-and-unsubscribe) / `unsubscribe` | Choose events |
| [`ping`](#ping) | Keep-alive |

### status

No fields. The result is a [Status](#status-object).

### conversations.list

| Field | Type | Default | Meaning |
|---|---|---|---|
| `limit` | integer 1–200 | 50 | Page size |
| `cursor` | string | | `nextCursor` of the previous page |
| `q` | string | | Search in text, numbers and contact names |
| `unread` | boolean | `false` | Only conversations with unread messages |
| `sim` | integer | | Only this SIM (subscription id) |

The result is a [Page](#page) of [Conversation](#conversation), newest
first.

### messages.list

| Field | Type | Default | Meaning |
|---|---|---|---|
| `address` | string | | Only messages from or to this number; see [Phone numbers](#phone-numbers) |
| `conversationId` | integer | | Only this conversation; wins if `address` is given too |
| `direction` | `"in"`, `"out"`, `"all"` | `"all"` | Incoming, outgoing or both |
| `q` | string | | Search in the message text |
| `from`, `to` | integer | | Time range in ms since the epoch, inclusive |
| `sim` | integer | | Only this SIM |
| `unread` | boolean | `false` | Only unread messages |
| `limit` | integer 1–200 | 50 | Page size |
| `cursor` | string | | `nextCursor` of the previous page |

The result is a [Page](#page) of [Message](#message), newest first.

The last 10 incoming messages:

```json
{"id":"2","action":"messages.list","direction":"in","limit":10}
```

### messages.get

| Field | Type | Meaning |
|---|---|---|
| `messageId` | integer | `id` of the message |

The result is a [Message](#message).

```json
{"id":"3","action":"messages.get","messageId":2813}
```

### messages.send

| Field | Type | Meaning |
|---|---|---|
| `to` | string | Phone number. Spaces, dashes, dots, slashes and brackets are ignored. |
| `body` | string | Text, at most 10 SMS parts |
| `sim` | integer, optional | Subscription id; default is the phone's SMS SIM |
| `requestId` | string, optional | Your own id, 1–64 characters of `A-Z a-z 0-9 . _ -`; see below |

The result is a [SendResult](#sendresult). The message is then on its way;
the outcome arrives as `sms.sent`, `sms.delivered` or `sms.failed` events
and can be read with `messages.status`.

**Sending exactly once.** If the connection breaks after `messages.send`,
the client cannot know whether the message was accepted. Send the request
again with the same `requestId`: the phone does not send twice but answers
with the SendResult of the first request and its current `status`. A
`requestId` is remembered for 30 days, also across restarts of the app.
Using it again for another recipient or text is answered with
`invalid_request`. A repeat never sends again, also not after the first
attempt failed: the answer then has the status `failed`. To try again, use a
new `requestId`.

### messages.sendGroup

| Field | Type | Meaning |
|---|---|---|
| `to` | array of strings | 1 to 20 phone numbers, as in `messages.send`; repeated numbers are dropped |
| `body` | string | Text, at most 10 SMS parts |
| `sim` | integer, optional | Subscription id for every message; default is the phone's SMS SIM |
| `requestId` | string, optional | Your own id of the group, 1–61 characters of `A-Z a-z 0-9 . _ -` |

The result is a [GroupSendResult](#groupsendresult): the same text goes
to every number as a separate SMS, and nobody sees the other recipients.
Each member has its own request id, `<requestId>.<n>` counted from 1, and
is followed like a single message: through the events and with
`messages.status`. The group id itself has no outbox entry.

The checks run for the whole group before the first SMS leaves. A number
that is not a phone number (`invalid_number`, the message names it), a
text that is too long, fewer messages left in the daily limit than the
group has recipients (`quota_exceeded`), or more SMS parts than the 20 of
one minute, parts × recipients (`invalid_request`), refuse the whole
group. A refusal by the phone for one recipient does not stop the others:
that member has the status `failed`, and the answer is still a success.

**Sending exactly once** works as for `messages.send`: the same
`requestId` with the same text and numbers answers with the stored
members and their current status; with another text or other numbers it
is an `invalid_request`.

```json
{"id":"4","action":"messages.sendGroup","to":["+4369912345678","+436641112223"],"body":"Meeting at 10.","requestId":"news-1"}
```

```json
{"id":"4","ok":true,"result":{"requestId":"news-1","to":["+4369912345678","+436641112223"],"parts":1,"status":"queued","messages":[{"requestId":"news-1.1","to":"+4369912345678","parts":1,"status":"queued"},{"requestId":"news-1.2","to":"+436641112223","parts":1,"status":"queued"}]}}
```

### messages.status

| Field | Type | Meaning |
|---|---|---|
| `requestId` | string | From `messages.send` |

The result is an [Outbox entry](#outbox-entry).

### messages.delete

| Field | Type | Meaning |
|---|---|---|
| `messageId` | integer | `id` of the message |

The result is `{"deleted":1,"mode":"hidden"}`. With mode `hidden` the
message disappears from SMS Dock (web interface and API) but stays on the
phone. With mode `provider` it is removed from the phone; this needs SMS
Dock to be the phone's default SMS app. `status.deleteMode` tells which one
applies.

### conversations.delete

| Field | Type | Meaning |
|---|---|---|
| `conversationId` | integer | `id` of the conversation |

The result is `{"deleted":12,"mode":"hidden"}`; `deleted` is the number of
messages.

### conversations.markRead

| Field | Type | Meaning |
|---|---|---|
| `conversationId` | integer | `id` of the conversation |

The result is `{"ok":true}`. Android lets only the default SMS app change
messages; otherwise the answer is the error `not_default_app`, whether or
not the conversation exists. A `conversations.changed` event follows the
change.

### contacts.list

| Field | Type | Default | Meaning |
|---|---|---|---|
| `q` | string | | Search in names and numbers; a number also matches its national or international form |
| `limit` | integer 1–200 | 50 | Page size |
| `cursor` | string | | `nextCursor` of the previous page |

The result is a [Page](#page) of [Contact](#contact), sorted by name.

### subscribe and unsubscribe

| Field | Type | Meaning |
|---|---|---|
| `events` | array of strings | Event names, or `["*"]` for all |
| `address` | string, optional | `subscribe` only: deliver `sms.*` events only for this number; see [Phone numbers](#phone-numbers) |

The result is `{"events":["sms.received"],"address":null}`, the
subscription now in force, with `*` replaced by the names it stands for and
`address` as it was given. A new connection is subscribed to nothing. An
unknown event name is answered with `invalid_request`.

- `subscribe` adds the names to the subscription. Its `address` replaces
  the filter; without `address`, or with `null`, the filter is removed.
- `unsubscribe` removes the names. When no name is left, the filter is
  removed too.
- The filter applies to events that name a peer. `conversations.changed`
  and events whose number is not known are always delivered.

### ping

No fields. The result is `{"time":1790672531000}`. The server also sends
WebSocket pings every 20 seconds and closes connections that do not answer
within 45 seconds; WebSocket libraries answer these by themselves.

## Phone numbers

In `to`, spaces, dashes, dots, slashes and brackets are ignored.

Where a number selects messages (`messages.list.address`,
`subscribe.address`), it is compared the way the phone's messaging app
does: `+43 699 1234567`, `0043 699 1234567` and `0699 1234567` mean the
same peer. A number without country code matches if its digits without
leading zeros (at least 7) end the other number. Sender names such as
`MyBank` are compared as text, ignoring case.

A number without country code therefore also matches the same digits in
another country. The comparison is a convenience for finding messages, not
a security boundary.

## Events

Events are sent to subscribed clients without being asked for:

```json
{"event":"sms.received","seq":42,"data":{"message":{}}}
```

`seq` increases by one per event, counted over all events of the phone. A
client that subscribed to some events, or filters by number, therefore sees
gaps. The count restarts at 1 when the app is restarted.

| Event | `data` | When |
|---|---|---|
| `sms.received` | `{"message": Message}` | An SMS arrived |
| `sms.queued` | `{"requestId","to","parts","sim","source"}` | SMS Dock accepted a message for sending |
| `sms.sent` | `{"requestId","conversationId","to","message": Message or null}` | The network accepted a message; `requestId` is `null` if another app sent it |
| `sms.delivered` | `{"requestId","id","conversationId","to"}` | The recipient's phone confirmed receipt |
| `sms.failed` | `{"requestId","id","conversationId","to","errorCode","reason"}` | Sending failed |
| `sms.deleted` | `{"id","conversationId","address","mode"}` | A message or (`id` null) a conversation was deleted |
| `conversations.changed` | `{}` | Messages changed outside SMS Dock |

- `id` and `conversationId` are `null` as long as the phone has not stored
  the message.
- `source` tells who sent: `web`, `api` or `app`.
- `errorCode` is Android's result code, `reason` its name: `generic_failure`,
  `radio_off`, `null_pdu`, `no_service`, `limit_exceeded`,
  `fdn_check_failure`, `short_code_not_allowed`,
  `short_code_never_allowed`, or `error_N` for other codes.
- If the phone does not even take the message, `messages.send` is answered
  with the error `send_failed`, and an `sms.failed` with `errorCode` `null`
  and the reason `send_refused` is published. The `requestId` is then
  remembered with the status `failed`.

### Reacting to incoming SMS

```json
{"id":"1","action":"subscribe","events":["sms.received"]}
```

```json
{"event":"sms.received","seq":7,"data":{"message":{"id":2815,"conversationId":179,"address":"+4369912345678","contact":null,"direction":"in","body":"STATUS","timestamp":1790672531000,"sentTimestamp":1790672529000,"read":false,"status":"received","sim":1,"requestId":null}}}
```

```json
{"id":"2","action":"messages.send","to":"+4369912345678","body":"All systems running."}
```

## Objects

Timestamps are milliseconds since the Unix epoch. Fields that do not apply
are `null`, never left out.

### Message

| Field | Type | Notes |
|---|---|---|
| `id` | integer or null | Id on the phone. `null` only in an `sms.received` event whose stored copy was not found in time. |
| `conversationId` | integer or null | |
| `address` | string | Peer number or sender name as stored |
| `contact` | string or null | Name from the phone's contacts |
| `direction` | `"in"` or `"out"` | |
| `body` | string | |
| `timestamp` | integer | |
| `sentTimestamp` | integer or null | Incoming only: time from the sender's network |
| `read` | boolean | |
| `status` | string | `received`, `queued`, `sending`, `sent`, `delivered`, `failed` |
| `sim` | integer or null | Subscription id |
| `requestId` | string or null | Set for messages sent through SMS Dock |

### Conversation

| Field | Type | Notes |
|---|---|---|
| `id` | integer | |
| `address` | string | |
| `contact` | string or null | |
| `snippet` | string | Newest message, cut to 160 characters |
| `timestamp` | integer | Of the newest message |
| `direction` | `"in"` or `"out"` | Of the newest message |
| `messageCount` | integer | |
| `unreadCount` | integer | |
| `sim` | integer or null | Of the newest message |

### Contact

```json
{"name":"Anna Example","numbers":[{"number":"+4369912345678","label":"Mobile"}]}
```

### Sim

```json
{"subscriptionId":1,"slot":0,"name":"Play","number":null,"isDefault":true}
```

### Status object

```json
{
  "app": {"name":"SMS Dock","version":"1.0.0","protocolVersion":1},
  "sims": [],
  "permissions": {"readSms":true,"sendSms":true,"receiveSms":true,"contacts":true,"phoneState":true,"notifications":true},
  "defaultSmsApp": false,
  "deleteMode": "hidden",
  "licence": {"enforced":false,"licensed":false,"dailyLimit":10,"sentToday":3,"remaining":null},
  "popup": true
}
```

### SendResult

```json
{"requestId":"b0c1d2","to":"+4369087654321","parts":1,"status":"queued"}
```

`to` is the number as it is sent, without the ignored characters.

### GroupSendResult

```json
{"requestId":"news-1","to":["+4369912345678","+436641112223"],"parts":1,"status":"queued","messages":[{"requestId":"news-1.1","to":"+4369912345678","parts":1,"status":"queued"},{"requestId":"news-1.2","to":"+436641112223","parts":1,"status":"failed"}]}
```

`messages` holds one SendResult per recipient in the order of `to`.
`status` is the least advanced status of the members (`queued`,
`sending`, `sent`, `delivered`), or `failed` when every member failed.

### Outbox entry

```json
{"requestId":"b0c1d2","to":"+4369087654321","parts":2,"partsSent":2,"status":"sent","errorCode":null,"messageId":2814,"sim":1,"source":"api","createdAt":1790672531000}
```

### Page

```json
{"items":[],"nextCursor":"1790672531000:2813"}
```

`nextCursor` is `null` on the last page.

## Errors

| Code | Meaning |
|---|---|
| `invalid_json` | The frame is not a JSON object |
| `invalid_request` | A field is missing or malformed |
| `unknown_action` | |
| `not_authenticated` | Authenticate first |
| `invalid_password` | |
| `locked_out` | Too many wrong passwords; `retryAfter` in the error object holds the seconds to wait |
| `licence_required` | The API needs the full licence |
| `quota_exceeded` | Daily sending limit of the free version reached |
| `rate_limited` | Too many frames per second or SMS parts per minute; `retryAfter` in the error object holds the seconds to wait |
| `permission_missing` | An Android permission is not granted; `permission` in the error object names it |
| `invalid_number` | |
| `message_too_long` | More than 10 SMS parts |
| `sim_unavailable` | The SIM is missing, or no default SMS SIM is set on the phone |
| `not_found` | |
| `not_default_app` | Needs SMS Dock as the phone's default SMS app |
| `send_failed` | Android refused the message |
| `internal_error` | |

## Close codes

| Code | Meaning |
|---|---|
| 1000 | Normal |
| 1001 | Server is stopping |
| 1009 | Frame from the client larger than 64 KiB |
| 4400 | Protocol error, for example a binary frame |
| 4401 | Not authenticated in time, wrong password, or logins locked |
| 4403 | Licence required |
| 4408 | No answer to pings |
| 4409 | Too many clients |
| 4410 | Password was changed |
| 4429 | The client kept sending although its requests were answered with `rate_limited` |

## Limits

| Limit | Value |
|---|---|
| Frame size, client to server | 64 KiB |
| Clients | 8 |
| Frames from the client | 20 per second per connection, counting every frame, also `authenticate` and unreadable ones |
| Sending | 20 SMS parts per minute for the whole phone |
| Group message | 20 recipients, and parts × recipients at most 20 |
| Message length | 10 SMS parts (1530 plain or 670 Unicode characters) |
| Page size | 200 |
