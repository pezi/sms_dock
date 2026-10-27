"""Type hints for the JSON objects of the SMS Dock API.

The client returns plain ``dict`` objects exactly as the server sent them.
The classes in this module are :class:`typing.TypedDict` definitions: they
describe the keys for editors and type checkers and cost nothing at run
time. All timestamps are milliseconds since the Unix epoch. Fields that do
not apply are ``None``; they are never left out.
"""

from __future__ import annotations

from typing import Any, TypedDict


class Message(TypedDict):
    """One SMS.

    ``id`` is ``None`` only inside an ``sms.received`` event whose stored
    copy was not found in time. ``direction`` is ``"in"`` or ``"out"``.
    ``status`` is one of ``received``, ``queued``, ``sending``, ``sent``,
    ``delivered`` and ``failed``. ``sentTimestamp`` is set for incoming
    messages only. ``requestId`` is set for messages sent through SMS Dock.
    """

    id: int | None
    conversationId: int | None
    address: str
    contact: str | None
    direction: str
    body: str
    timestamp: int
    sentTimestamp: int | None
    read: bool
    status: str
    sim: int | None
    requestId: str | None


class Conversation(TypedDict):
    """All messages exchanged with one phone number.

    ``snippet`` is the newest message cut to 160 characters; ``timestamp``,
    ``direction`` and ``sim`` describe that newest message.
    """

    id: int
    address: str
    contact: str | None
    snippet: str
    timestamp: int
    direction: str
    messageCount: int
    unreadCount: int
    sim: int | None


class ContactNumber(TypedDict):
    """One phone number of a contact."""

    number: str
    label: str | None


class Contact(TypedDict):
    """One contact of the phone that has at least one number."""

    name: str
    numbers: list[ContactNumber]


class Sim(TypedDict):
    """One SIM. ``subscriptionId`` is the value used as ``sim`` elsewhere."""

    subscriptionId: int
    slot: int
    name: str
    number: str | None
    isDefault: bool


class AppInfo(TypedDict):
    """Name and version of the app and the protocol version it speaks."""

    name: str
    version: str
    protocolVersion: int


class Licence(TypedDict):
    """Licence state. ``remaining`` is ``None`` when sending is unlimited."""

    enforced: bool
    licensed: bool
    dailyLimit: int
    sentToday: int
    remaining: int | None


class Status(TypedDict):
    """Result of ``status``.

    ``deleteMode`` is ``"hidden"`` or ``"provider"``. ``permissions`` maps
    the names ``readSms``, ``sendSms``, ``receiveSms``, ``contacts``,
    ``phoneState`` and ``notifications`` to booleans.
    """

    app: AppInfo
    sims: list[Sim]
    permissions: dict[str, bool]
    defaultSmsApp: bool
    deleteMode: str
    licence: Licence
    popup: bool


class SendResult(TypedDict):
    """Result of ``messages.send``.

    ``to`` is the number as it is sent, without the ignored characters.
    ``status`` is ``queued`` for a new message; a repeated request
    returns the current status, ``failed`` included.
    """

    requestId: str
    to: str
    parts: int
    status: str


class GroupSendResult(TypedDict):
    """Result of ``messages.sendGroup``: one SMS per recipient.

    ``messages`` holds one :class:`SendResult` per recipient in the order
    of ``to``, with the request ids ``<requestId>.1``, ``<requestId>.2``
    and so on. ``status`` is the least advanced status of the members, or
    ``failed`` when every member failed.
    """

    requestId: str
    to: list[str]
    parts: int
    status: str
    messages: list[SendResult]


class OutboxEntry(TypedDict):
    """State of a message sent through SMS Dock.

    ``status`` is ``queued`` (handed to the phone), ``sending`` (some
    parts are left), ``sent``, ``delivered`` or ``failed``. ``source``
    tells who sent: ``web``, ``api`` or ``app``.
    """

    requestId: str
    to: str
    parts: int
    partsSent: int
    status: str
    errorCode: int | None
    messageId: int | None
    sim: int | None
    source: str
    createdAt: int


class DeleteResult(TypedDict):
    """Result of a delete. ``mode`` is ``"hidden"`` or ``"provider"``."""

    deleted: int
    mode: str


class Subscription(TypedDict):
    """The subscription in force after ``subscribe`` or ``unsubscribe``.

    ``events`` holds event names; ``*`` is replaced by the names it
    stands for.
    """

    events: list[str]
    address: str | None


class PingResult(TypedDict):
    """Result of ``ping``: the time of the phone."""

    time: int


class MessagePage(TypedDict):
    """One page of messages. ``nextCursor`` is ``None`` on the last page."""

    items: list[Message]
    nextCursor: str | None


class ConversationPage(TypedDict):
    """One page of conversations."""

    items: list[Conversation]
    nextCursor: str | None


class ContactPage(TypedDict):
    """One page of contacts."""

    items: list[Contact]
    nextCursor: str | None


class MarkReadResult(TypedDict):
    """Result of ``conversations.markRead``: ``{"ok": True}``."""

    ok: bool


class Event(TypedDict):
    """An event: ``{"event": name, "seq": number, "data": {...}}``.

    ``seq`` counts all events of the phone and restarts at 1 when the app
    is restarted. The keys of ``data`` are listed under "Events" in
    ``API.md``.
    """

    event: str
    seq: int
    data: dict[str, Any]
