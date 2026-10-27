"""Synchronous, reconnecting client for the SMS Dock WebSocket API.

The protocol is described in ``API.md`` at the top of the repository. The
client is not thread-safe: use one :class:`SmsDockClient` per thread.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import re
import selectors
import socket
import ssl
import sys
import threading
import time
import uuid
from collections import deque
from collections.abc import Iterable, Iterator, Sequence
from datetime import datetime
from typing import Any, Callable, Union
from urllib.parse import urlsplit

from websockets.exceptions import ConnectionClosed, WebSocketException
from websockets.sync.client import connect

from .models import (
    Contact,
    ContactPage,
    Conversation,
    ConversationPage,
    DeleteResult,
    GroupSendResult,
    Event,
    MarkReadResult,
    Message,
    MessagePage,
    OutboxEntry,
    PingResult,
    SendResult,
    Status,
    Subscription,
)

API_PROTOCOL_VERSION = 1
"""Protocol version this client speaks."""

DEFAULT_API_PORT = 9443
"""Default port of the WebSocket API."""

MAX_FRAME_BYTES = 64 * 1024
"""Largest frame the server accepts from a client."""

MAX_INCOMING_BYTES = 4 * 1024 * 1024
"""Largest frame the client accepts. Answers may exceed 64 KiB."""

MAX_PAGE_SIZE = 200
"""Largest value of ``limit``."""

PASSWORD_ENV = "SMSDOCK_PASSWORD"
"""Environment variable that holds the password."""

FINGERPRINT_ENV = "SMSDOCK_FINGERPRINT"
"""Environment variable that holds the certificate fingerprint."""

EVENT_NAMES = (
    "sms.received",
    "sms.queued",
    "sms.sent",
    "sms.delivered",
    "sms.failed",
    "sms.deleted",
    "conversations.changed",
)
"""All event names of protocol version 1."""

JsonObject = dict[str, Any]
ConnectFactory = Callable[..., Any]
ReconnectHook = Callable[["SmsDockClient"], None]
Timestamp = Union[int, float, datetime]

_REQUEST_ID_PATTERN = re.compile(r"[A-Za-z0-9._-]{1,64}")
_GROUP_REQUEST_ID_PATTERN = re.compile(r"[A-Za-z0-9._-]{1,61}")

#: Recipients of one group message at most.
MAX_GROUP_RECIPIENTS = 20
_FINGERPRINT_PATTERN = re.compile(r"[0-9a-f]{64}")
_DIRECTIONS = ("in", "out", "all")
_TRANSPORT_ERRORS = (WebSocketException, OSError)
_MAX_BACKOFF_EXPONENT = 16
_WAIT_SLICE = 0.5
_CERTIFICATE_KEY = "_smsdock_peer_certificate"
_FIRST_POLL = 0.1


class SmsDockError(Exception):
    """Base class of all errors raised by this package."""


class ApiError(SmsDockError):
    """The server answered a request with an error.

    ``code`` is one of the error codes of ``API.md``, ``message`` the text
    of the server. ``details`` holds the other fields of the error object,
    for example ``{"retryAfter": 30}`` or ``{"permission": "sendSms"}``.
    """

    def __init__(
        self,
        code: str,
        message: str,
        details: JsonObject | None = None,
    ) -> None:
        super().__init__(f"{code}: {message}")
        self.code = code
        self.message = message
        self.details: JsonObject = dict(details or {})

    @property
    def retry_after(self) -> int | None:
        """Seconds to wait before the next attempt, if the server said so."""

        value = self.details.get("retryAfter")
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            return None
        return int(value)

    @property
    def permission(self) -> str | None:
        """The missing Android permission of a ``permission_missing`` error."""

        value = self.details.get("permission")
        return value if isinstance(value, str) else None


class AuthenticationError(ApiError):
    """The login failed or the connection is no longer logged in.

    Codes: ``invalid_password``, ``locked_out`` (see :attr:`retry_after`)
    and ``not_authenticated``. The client never retries after this error,
    because every wrong password counts towards the lock of the phone.
    """


class LicenceRequiredError(ApiError):
    """The API needs the full licence of the app (``licence_required``)."""


class ProtocolError(SmsDockError):
    """The server sent something that protocol version 1 does not allow."""


class ConnectionInterrupted(SmsDockError):
    """The connection failed and could not be restored.

    ``close_code`` and ``close_reason`` are set when the server closed the
    WebSocket with a close frame.
    """

    def __init__(
        self,
        message: str,
        close_code: int | None = None,
        close_reason: str | None = None,
    ) -> None:
        super().__init__(message)
        self.close_code = close_code
        self.close_reason = close_reason


class CertificateMismatchError(SmsDockError):
    """The server presented a certificate other than the pinned one.

    ``expected`` and ``actual`` are SHA-256 fingerprints as upper-case hex
    pairs separated by colons. Nothing was sent to the server.
    """

    def __init__(self, expected: str, actual: str) -> None:
        super().__init__(
            "The certificate of the server does not match the pinned "
            f"fingerprint.\n  expected: {expected}\n  received: {actual}\n"
            "Compare the fingerprint with the Server screen of the app. It "
            "changes when the certificate is regenerated or the app is "
            "reinstalled."
        )
        self.expected = expected
        self.actual = actual


def normalize_fingerprint(fingerprint: str) -> str:
    """Return a SHA-256 fingerprint as 64 lower-case hex digits.

    Accepts hex with or without colons, in any case, and the line printed
    by ``openssl x509 -fingerprint`` (``sha256 Fingerprint=AB:CD:…``).
    Raises :class:`ValueError` for anything else.
    """

    text = fingerprint.strip()
    if "=" in text:
        text = text.rsplit("=", 1)[1]
    text = re.sub(r"[\s:]", "", text).lower()
    if not _FINGERPRINT_PATTERN.fullmatch(text):
        raise ValueError(
            "The fingerprint must be the SHA-256 of the certificate: "
            "64 hex digits, with or without colons"
        )
    return text


def format_fingerprint(fingerprint: str) -> str:
    """Return a fingerprint the way the app shows it: ``AB:CD:…``."""

    digits = normalize_fingerprint(fingerprint).upper()
    return ":".join(digits[i:i + 2] for i in range(0, len(digits), 2))


def certificate_fingerprint(der: bytes) -> str:
    """Return the SHA-256 fingerprint of a DER certificate as ``AB:CD:…``."""

    return format_fingerprint(hashlib.sha256(der).hexdigest())


def fetch_fingerprint(
    host: str,
    port: int = DEFAULT_API_PORT,
    *,
    timeout: float = 10.0,
) -> str:
    """Connect to ``host:port`` and return the fingerprint of its certificate.

    This helper trusts nobody: it only reports what the server presented.
    Compare the result with the fingerprint on the Server screen of the app
    before you pin it. Nothing but the TLS handshake is exchanged.
    """

    context = _unverified_context()
    with socket.create_connection(
        (host, port), timeout=timeout
    ) as plain, context.wrap_socket(plain) as tls:
        der = tls.getpeercert(binary_form=True)
    if not der:
        raise ProtocolError("The server presented no certificate")
    return certificate_fingerprint(der)


class SmsDockClient:
    """One authenticated connection to SMS Dock.

    ``url`` is the ``wss://`` address the app shows, for example
    ``wss://192.168.1.50:9443/ws``. Any other scheme is refused.

    ``password`` is the password set in the app. Without it the
    environment variable ``SMSDOCK_PASSWORD`` is read.

    ``fingerprint`` is the SHA-256 fingerprint of the certificate as shown
    on the Server screen of the app: hex, with or without colons, in any
    case. Without it the environment variable ``SMSDOCK_FINGERPRINT`` is
    read. The client compares it with the certificate of the server right
    after the TLS handshake and sends nothing if they differ.
    ``insecure=True`` skips this comparison and prints a warning to
    standard error; use it only for tests.

    ``timeout`` is the time in seconds to wait for a response,
    ``connect_timeout`` the time to open the connection.

    After a lost connection the client reconnects up to
    ``reconnect_attempts`` times (``None`` means without limit). It waits
    ``reconnect_delay`` seconds before the first attempt and doubles the
    delay each time up to ``max_reconnect_delay``. After reconnecting it
    logs in again and restores the subscription. Events that happened
    while the connection was down are lost; :attr:`missed_events` and
    :attr:`server_restarted` tell whether there were any.

    ``on_reconnect`` is called with the client after every reconnect, once
    the subscription is restored. Use it to read what was missed, for
    example with :meth:`messages`. It can also be set as an attribute.

    When the server answers ``rate_limited`` and names the seconds to
    wait, the client waits and repeats the request, at most
    ``rate_limit_retries`` times.

    At most ``event_queue_size`` events are kept while nobody reads them;
    after that the oldest are dropped and counted in
    :attr:`dropped_events`.

    ``connect_factory`` replaces ``websockets.sync.client.connect``; it
    exists for tests.

    Results are plain dictionaries as sent by the server; their keys are
    described in :mod:`smsdock.models` and ``API.md``.

    Use the client as a context manager::

        with SmsDockClient(url, fingerprint="AB:CD:…") as dock:
            print(dock.status())
    """

    def __init__(
        self,
        url: str,
        password: str | None = None,
        *,
        fingerprint: str | None = None,
        insecure: bool = False,
        timeout: float = 30.0,
        connect_timeout: float = 10.0,
        reconnect_attempts: int | None = 5,
        reconnect_delay: float = 1.0,
        max_reconnect_delay: float = 10.0,
        event_queue_size: int = 1000,
        rate_limit_retries: int = 2,
        on_reconnect: ReconnectHook | None = None,
        connect_factory: ConnectFactory | None = None,
    ) -> None:
        parts = urlsplit(url)
        if parts.scheme.lower() != "wss" or not parts.hostname:
            raise ValueError(
                "SMS Dock needs a wss:// URL, for example "
                "wss://192.168.1.50:9443/ws"
            )
        if password is None:
            password = os.environ.get(PASSWORD_ENV)
        if not password:
            raise ValueError(
                f"No password: pass one or set the environment variable "
                f"{PASSWORD_ENV}"
            )
        if fingerprint is None:
            fingerprint = os.environ.get(FINGERPRINT_ENV) or None
        if fingerprint is None and not insecure:
            raise ValueError(
                "No certificate fingerprint: pass the fingerprint shown on "
                "the Server screen of the app or set the environment "
                f"variable {FINGERPRINT_ENV}"
            )
        if timeout <= 0 or connect_timeout <= 0:
            raise ValueError("Timeouts must be positive")
        if reconnect_attempts is not None and reconnect_attempts < 0:
            raise ValueError("reconnect_attempts cannot be negative")
        if reconnect_delay < 0 or max_reconnect_delay < 0:
            raise ValueError("Reconnect delays cannot be negative")
        if event_queue_size < 1:
            raise ValueError("event_queue_size must be at least 1")
        if rate_limit_retries < 0:
            raise ValueError("rate_limit_retries cannot be negative")

        self.url = url
        self.timeout = timeout
        self.connect_timeout = connect_timeout
        self.reconnect_attempts = reconnect_attempts
        self.reconnect_delay = reconnect_delay
        self.max_reconnect_delay = max_reconnect_delay
        self.insecure = insecure
        self.rate_limit_retries = rate_limit_retries
        self.on_reconnect = on_reconnect
        """Called with the client after every reconnect, or ``None``."""
        self.server_info: JsonObject | None = None
        """``{"name", "version", "protocolVersion"}`` of the app, known
        after the login."""
        self.dropped_events = 0
        """Number of events dropped because the event queue was full."""
        self.connection_count = 0
        """Number of connections opened so far, reconnects included."""

        self._password = password
        self._fingerprint: bytes | None = None
        if not insecure and fingerprint is not None:
            self._fingerprint = bytes.fromhex(
                normalize_fingerprint(fingerprint)
            )
        self._connect_factory = connect_factory
        self._websocket: Any = None
        self._events: deque[JsonObject] = deque()
        self._event_queue_size = event_queue_size
        self._request_number = 0
        self._outstanding: set[str] = set()
        self._responses: dict[str, JsonObject] = {}
        self._subscription: JsonObject | None = None
        self._warned = False
        self._epoch: int | None = None
        self._latest_seq: int | None = None
        self._restarted = False
        self._missed: int | None = 0

    def __enter__(self) -> SmsDockClient:  # noqa: PYI034 - Self needs 3.11
        self.connect()
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def __repr__(self) -> str:
        state = "connected" if self.connected else "closed"
        return f"<SmsDockClient {self.url} {state}>"

    # ------------------------------------------------------------------
    # Connection
    # ------------------------------------------------------------------

    @property
    def connected(self) -> bool:
        """Whether a connection is open as far as the client knows."""

        return self._websocket is not None

    @property
    def subscription(self) -> Subscription | None:
        """The subscription in force, or ``None`` without one."""

        if self._subscription is None:
            return None
        return {
            "events": list(self._subscription["events"]),
            "address": self._subscription["address"],
        }

    @property
    def pending_events(self) -> int:
        """Number of events that wait in the queue."""

        return len(self._events)

    @property
    def epoch(self) -> int | None:
        """The ``epoch`` of the last login; ``None`` before the first.

        It identifies one run of the app. Sequence numbers begin at 1
        again when it changes.
        """

        return self._epoch

    @property
    def latest_seq(self) -> int | None:
        """Sequence number of the newest event the client knows of.

        This is the ``latestSeq`` of the last login or the ``seq`` of an
        event received since, whichever is higher. The phone counts all
        events, so a client that subscribed to some of them sees gaps.
        """

        return self._latest_seq

    @property
    def server_restarted(self) -> bool:
        """Whether the app was restarted before the last reconnect.

        ``True`` when the ``epoch`` of the current connection differs from
        the one of the connection before. Events were lost then, and
        their number is not known.
        """

        return self._restarted

    @property
    def missed_events(self) -> int | None:
        """Number of events of the phone during the last interruption.

        Counted over all events, subscribed or not. ``0`` after the first
        connect, ``None`` when the app was restarted and the number is
        not known.
        """

        return self._missed

    def connect(self) -> None:
        """Open the connection, check the certificate and log in.

        Makes one attempt. Raises :class:`CertificateMismatchError`,
        :class:`AuthenticationError`, :class:`LicenceRequiredError`,
        :class:`ProtocolError` or :class:`ConnectionInterrupted`.
        """

        self.close()
        try:
            self._open()
        except _TRANSPORT_ERRORS as error:
            self.close()
            raise (
                _fatal_error(error)
                or _interrupted(f"Cannot connect to {self.url}", error)
            ) from error

    def close(self) -> None:
        """Close the connection. Queued events are kept."""

        websocket, self._websocket = self._websocket, None
        self._outstanding.clear()
        self._responses.clear()
        if websocket is not None:
            try:
                websocket.close()
            except _TRANSPORT_ERRORS:
                pass

    # ------------------------------------------------------------------
    # Requests
    # ------------------------------------------------------------------

    def request(
        self,
        action: str,
        *,
        retry: bool = True,
        timeout: float | None = None,
        **parameters: Any,
    ) -> JsonObject:
        """Send one request and return the ``result`` of its response.

        ``parameters`` are the fields of the action as named in ``API.md``.
        With ``retry=True`` the request is sent again after a lost
        connection; pass ``retry=False`` for anything that must not happen
        twice. The same holds for the error ``rate_limited``: the client
        waits the seconds the server names and repeats the request.
        Raises :class:`ApiError` when the server answers with an error and
        :class:`ConnectionInterrupted` when the connection could not be
        restored.
        """

        if "id" in parameters:
            raise ValueError(
                "'id' is the id of the request; the client sets it"
            )
        attempt = 0
        while True:
            try:
                if self._websocket is None:
                    self._open()
                return self._request_patiently(
                    action, parameters, timeout or self.timeout,
                    self.rate_limit_retries if retry else 0,
                )
            except _TRANSPORT_ERRORS as error:
                what = f"performing {action!r}"
                if not retry:
                    self.close()
                    raise (
                        _fatal_error(error)
                        or _interrupted(
                            f"Connection interrupted while {what}", error
                        )
                    ) from error
                self._recover(what, error, attempt)
                attempt += 1

    def send_request(self, action: str, **parameters: Any) -> str:
        """Send a request without waiting and return its id.

        Several requests can be on their way at once; the server may
        answer them in any order. Fetch each response with
        :meth:`wait_for_response`. These two methods do not reconnect and
        do not repeat a request that was answered with ``rate_limited``.
        """

        if "id" in parameters:
            raise ValueError(
                "'id' is the id of the request; the client sets it"
            )
        try:
            if self._websocket is None:
                self._open()
            return self._send_frame(action, parameters)
        except _TRANSPORT_ERRORS as error:
            self.close()
            raise (
                _fatal_error(error)
                or _interrupted(
                    f"Connection interrupted while sending {action!r}", error
                )
            ) from error

    def wait_for_response(
        self,
        request_id: str,
        timeout: float | None = None,
    ) -> JsonObject:
        """Return the result of a request sent with :meth:`send_request`.

        Raises :class:`ApiError` when the response is an error,
        :class:`TimeoutError` when none arrives in time and
        :class:`ConnectionInterrupted` when the connection was lost.
        """

        key = str(request_id)
        if key not in self._outstanding and key not in self._responses:
            raise ValueError(f"No request with the id {request_id!r} is open")
        deadline = time.monotonic() + (timeout or self.timeout)
        try:
            return self._await_response(key, f"request {key}", deadline)
        except TimeoutError:
            raise
        except _TRANSPORT_ERRORS as error:
            self.close()
            raise (
                _fatal_error(error)
                or _interrupted(
                    f"Connection interrupted while waiting for {key!r}", error
                )
            ) from error

    def status(self) -> Status:
        """Return phone, SIMs, permissions, licence and limits."""

        return self.request("status")  # type: ignore[return-value]

    def ping(self) -> PingResult:
        """Check the connection. Returns ``{"time": ms}`` of the phone."""

        return self.request("ping")  # type: ignore[return-value]

    def conversations(
        self,
        *,
        q: str | None = None,
        unread: bool = False,
        sim: int | None = None,
        limit: int = 50,
        cursor: str | None = None,
    ) -> ConversationPage:
        """Return one page of conversations, newest first.

        ``q`` searches text, numbers and contact names. ``unread`` keeps
        only conversations with unread messages. ``sim`` is a subscription
        id. ``cursor`` is the ``nextCursor`` of the previous page.
        """

        return self.request(  # type: ignore[return-value]
            "conversations.list",
            **_conversation_filter(q, unread, sim),
            **_paging(limit, cursor),
        )

    def iter_conversations(
        self,
        *,
        q: str | None = None,
        unread: bool = False,
        sim: int | None = None,
        page_size: int = 50,
        max_items: int | None = None,
    ) -> Iterator[Conversation]:
        """Yield conversations, newest first, page after page."""

        filters = _conversation_filter(q, unread, sim)
        return self._iterate(  # type: ignore[return-value]
            "conversations.list", filters, page_size, max_items
        )

    def messages(
        self,
        *,
        address: str | None = None,
        conversation_id: int | None = None,
        direction: str = "all",
        q: str | None = None,
        since: Timestamp | None = None,
        until: Timestamp | None = None,
        sim: int | None = None,
        unread: bool = False,
        limit: int = 50,
        cursor: str | None = None,
    ) -> MessagePage:
        """Return one page of messages, newest first.

        ``address`` keeps messages from or to one number,
        ``conversation_id`` those of one conversation. ``direction`` is
        ``"in"``, ``"out"`` or ``"all"``. ``q`` searches the text.
        ``since`` and ``until`` limit the time, both inclusive; they are
        milliseconds since the epoch or :class:`datetime.datetime` objects
        and are sent as the fields ``from`` and ``to``. ``sim`` is a
        subscription id. ``limit`` is 1 to 200. ``cursor`` is the
        ``nextCursor`` of the previous page.

        The result is ``{"items": [Message, …], "nextCursor": str or None}``.
        """

        filters = _message_filter(
            address, conversation_id, direction, q, since, until, sim, unread
        )
        return self.request(  # type: ignore[return-value]
            "messages.list", **filters, **_paging(limit, cursor)
        )

    def iter_messages(
        self,
        *,
        address: str | None = None,
        conversation_id: int | None = None,
        direction: str = "all",
        q: str | None = None,
        since: Timestamp | None = None,
        until: Timestamp | None = None,
        sim: int | None = None,
        unread: bool = False,
        page_size: int = 50,
        max_items: int | None = None,
    ) -> Iterator[Message]:
        """Yield messages, newest first, following ``nextCursor``.

        The filters are those of :meth:`messages`. ``page_size`` is the
        ``limit`` of each request. ``max_items`` ends the iteration after
        that many messages; ``None`` yields all of them.
        """

        filters = _message_filter(
            address, conversation_id, direction, q, since, until, sim, unread
        )
        return self._iterate(  # type: ignore[return-value]
            "messages.list", filters, page_size, max_items
        )

    def get_message(self, message_id: int) -> Message:
        """Return one message. Raises ``not_found`` if it does not exist."""

        return self.request(  # type: ignore[return-value]
            "messages.get", messageId=_integer(message_id, "message_id")
        )

    def send(
        self,
        to: str,
        body: str,
        *,
        sim: int | None = None,
        request_id: str | None = None,
    ) -> SendResult:
        """Send an SMS and return the :class:`~smsdock.models.SendResult`.

        ``to`` is the phone number, ``body`` the text (at most 10 SMS
        parts), ``sim`` a subscription id (default: the SMS SIM of the
        phone). The message is on its way when this method returns; use
        :meth:`wait_for_result` for the outcome.

        ``request_id`` makes the request safe to repeat: the phone sends
        a message with the same ``request_id`` only once and remembers
        the id for 30 days. A repeat returns the result of the first
        request with the current ``status``, which may be ``sent``
        already. A repeat never sends again, also not after the first
        attempt failed: the status is ``failed`` then, and a new attempt
        needs a new ``request_id``. A repeat with another recipient or
        text fails with ``invalid_request``.

        Without ``request_id`` the client creates a random one. If the
        connection drops before the response arrives, the request is
        repeated with the same id.

        In the result ``to`` is the number as it is sent, without spaces
        and other ignored characters.
        """

        if not isinstance(to, str) or not to.strip():
            raise ValueError("'to' must be a phone number")
        if not isinstance(body, str) or not body:
            raise ValueError("'body' must be a text")
        if request_id is None:
            request_id = uuid.uuid4().hex
        elif not _REQUEST_ID_PATTERN.fullmatch(request_id):
            raise ValueError(
                "request_id must be 1 to 64 characters of "
                "A-Z a-z 0-9 . _ -"
            )
        parameters: JsonObject = {
            "to": to,
            "body": body,
            "requestId": request_id,
        }
        if sim is not None:
            parameters["sim"] = _integer(sim, "sim")
        return self.request(  # type: ignore[return-value]
            "messages.send", **parameters
        )

    def send_group(
        self,
        to: Sequence[str],
        body: str,
        *,
        sim: int | None = None,
        request_id: str | None = None,
    ) -> GroupSendResult:
        """Send one text to several numbers, one SMS each.

        ``to`` holds 1 to 20 phone numbers; repeated numbers are sent
        once. Every recipient gets a separate SMS and does not see the
        others. The result is a :class:`~smsdock.models.GroupSendResult`
        with one :class:`~smsdock.models.SendResult` per recipient; their
        request ids are ``<request_id>.1``, ``<request_id>.2`` and so on,
        and :meth:`wait_for_result` and :meth:`message_status` take them
        like those of single messages.

        The phone checks the whole group before it sends anything: a
        number that is not a phone number (``invalid_number``), a text
        that is too long, fewer messages left in the daily limit of the
        free version than the group has recipients (``quota_exceeded``)
        or more SMS parts than the 20 of one minute, parts times
        recipients (``invalid_request``), refuse the whole group. A
        refusal by the phone for one recipient does not stop the others;
        that member has the status ``failed`` in ``messages``.

        ``request_id`` has at most 61 characters here, so that the member
        suffix fits, and works as for :meth:`send`: the same id with the
        same text and numbers is not sent again but answered with the
        stored members and their current status.
        """

        if isinstance(to, str) or not isinstance(to, Sequence) or not to:
            raise ValueError("'to' must be a list of phone numbers")
        if any(not isinstance(number, str) or not number.strip() for number in to):
            raise ValueError("'to' must be a list of phone numbers")
        if len(to) > MAX_GROUP_RECIPIENTS:
            raise ValueError(
                f"'to' holds more than {MAX_GROUP_RECIPIENTS} numbers"
            )
        if not isinstance(body, str) or not body:
            raise ValueError("'body' must be a text")
        if request_id is None:
            request_id = uuid.uuid4().hex
        elif not _GROUP_REQUEST_ID_PATTERN.fullmatch(request_id):
            raise ValueError(
                "request_id of a group must be 1 to 61 characters of "
                "A-Z a-z 0-9 . _ -"
            )
        parameters: JsonObject = {
            "to": list(to),
            "body": body,
            "requestId": request_id,
        }
        if sim is not None:
            parameters["sim"] = _integer(sim, "sim")
        return self.request(  # type: ignore[return-value]
            "messages.sendGroup", **parameters
        )

    def message_status(self, request_id: str) -> OutboxEntry:
        """Return the outbox entry of a message sent through SMS Dock."""

        return self.request(  # type: ignore[return-value]
            "messages.status", requestId=request_id
        )

    def wait_for_result(
        self,
        request_id: str,
        timeout: float | None = None,
        *,
        delivered: bool = False,
        poll_interval: float = 1.0,
    ) -> OutboxEntry:
        """Wait until a sent message succeeded or failed.

        Returns the outbox entry once its ``status`` is ``sent``,
        ``delivered`` or ``failed``. With ``delivered=True`` the status
        ``sent`` is not enough: the method waits for the delivery report,
        which some networks never send. Check ``status`` and ``errorCode``
        of the result; a failed message raises no exception.

        The method asks the server with ``messages.status``: soon after
        the start, then less often, at last every ``poll_interval``
        seconds. If the connection is subscribed to
        ``sms.sent``, ``sms.delivered`` and ``sms.failed`` it asks as soon
        as such an event arrives. The events stay in the queue. Raises
        :class:`TimeoutError` after ``timeout`` seconds (default: the
        timeout of the client).
        """

        if poll_interval <= 0:
            raise ValueError("poll_interval must be positive")
        final = ("delivered", "failed") if delivered else (
            "sent", "delivered", "failed"
        )
        deadline = time.monotonic() + (timeout or self.timeout)
        pause = min(_FIRST_POLL, poll_interval)
        while True:
            entry = self.message_status(request_id)
            if entry.get("status") in final:
                return entry
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise TimeoutError(
                    f"No result for the message {request_id!r} in time; "
                    f"its status is {entry.get('status')!r}"
                )
            self._wait_for_outcome_event(
                request_id, time.monotonic() + min(pause, remaining)
            )
            pause = min(pause * 2, poll_interval)

    def delete_message(self, message_id: int) -> DeleteResult:
        """Delete one message. Returns ``{"deleted": 1, "mode": …}``.

        With mode ``hidden`` the message disappears from SMS Dock but
        stays on the phone. If the connection drops after the server
        deleted the message, the repeated request fails with ``not_found``.
        """

        return self.request(  # type: ignore[return-value]
            "messages.delete", messageId=_integer(message_id, "message_id")
        )

    def delete_conversation(self, conversation_id: int) -> DeleteResult:
        """Delete a conversation. Returns ``{"deleted": n, "mode": …}``."""

        return self.request(  # type: ignore[return-value]
            "conversations.delete",
            conversationId=_integer(conversation_id, "conversation_id"),
        )

    def mark_read(self, conversation_id: int) -> MarkReadResult:
        """Mark all messages of a conversation as read.

        Returns ``{"ok": True}``. Android lets only the default SMS app
        change messages; otherwise the error is ``not_default_app``.
        """

        return self.request(  # type: ignore[return-value]
            "conversations.markRead",
            conversationId=_integer(conversation_id, "conversation_id"),
        )

    def contacts(
        self,
        *,
        q: str | None = None,
        limit: int = 50,
        cursor: str | None = None,
    ) -> ContactPage:
        """Return one page of contacts, sorted by name.

        ``q`` searches names and numbers.
        """

        filters: JsonObject = {} if q is None else {"q": q}
        return self.request(  # type: ignore[return-value]
            "contacts.list", **filters, **_paging(limit, cursor)
        )

    def iter_contacts(
        self,
        *,
        q: str | None = None,
        page_size: int = 50,
        max_items: int | None = None,
    ) -> Iterator[Contact]:
        """Yield contacts, sorted by name, page after page."""

        filters: JsonObject = {} if q is None else {"q": q}
        return self._iterate(  # type: ignore[return-value]
            "contacts.list", filters, page_size, max_items
        )

    # ------------------------------------------------------------------
    # Events
    # ------------------------------------------------------------------

    def subscribe(
        self,
        events: str | Iterable[str],
        address: str | None = None,
    ) -> Subscription:
        """Ask for events and return the subscription now in force.

        ``events`` is one name or several, or ``"*"`` for all; the names
        are added to the subscription. ``address`` limits the ``sms.*``
        events to one number and replaces the limit set before; without
        it the limit is removed. Numbers are compared as the phone does:
        ``+43 699 1234567`` and ``0699 1234567`` are the same.

        In the result ``*`` is replaced by the names it stands for. The
        client remembers the subscription and restores it after a
        reconnect.
        """

        parameters: JsonObject = {"events": _event_names(events)}
        if address is not None:
            parameters["address"] = address
        result = self.request("subscribe", **parameters)
        self._remember_subscription(result)
        return result  # type: ignore[return-value]

    def unsubscribe(self, events: str | Iterable[str]) -> Subscription:
        """Stop events and return the subscription now in force.

        ``events`` is one name or several, or ``"*"`` for all. When no
        name is left, the limit to one number is removed too.
        """

        result = self.request("unsubscribe", events=_event_names(events))
        self._remember_subscription(result)
        return result  # type: ignore[return-value]

    def events(self, timeout: float | None = None) -> Iterator[Event]:
        """Yield events as they arrive.

        Events that were queued while a request waited for its response
        come first. ``timeout`` is the longest time in seconds to wait for
        the next event; the iteration ends when it passes. ``None`` waits
        without limit. Subscribe first, a new connection gets no events.

        After a lost connection the generator reconnects and goes on.
        Raises :class:`ConnectionInterrupted` when that fails.
        """

        while True:
            if self._events:
                yield self._events.popleft()  # type: ignore[misc]
                continue
            deadline = None if timeout is None else time.monotonic() + timeout
            event = self._read_event(deadline, "waiting for events")
            if event is None:
                return
            yield event  # type: ignore[misc]

    def wait_for_event(
        self,
        name: str | Iterable[str],
        timeout: float | None = None,
        *,
        predicate: Callable[[JsonObject], bool] | None = None,
    ) -> Event:
        """Return the next event with this name.

        ``name`` is one event name or several. ``predicate`` is called
        with each candidate and must return ``True`` for the wanted one.
        Other events stay in the queue. Raises :class:`TimeoutError` after
        ``timeout`` seconds (default: the timeout of the client).
        """

        names = set(_event_names(name))

        def wanted(event: JsonObject) -> bool:
            if "*" not in names and event.get("event") not in names:
                return False
            return predicate is None or bool(predicate(event))

        for index, event in enumerate(self._events):
            if wanted(event):
                del self._events[index]
                return event  # type: ignore[return-value]
        deadline = time.monotonic() + (timeout or self.timeout)
        label = ", ".join(sorted(names))
        while True:
            event = self._read_event(deadline, f"waiting for {label}")
            if event is None:
                raise TimeoutError(f"No event {label} in time")
            if wanted(event):
                return event  # type: ignore[return-value]
            self._queue_event(event)

    # ------------------------------------------------------------------
    # Internals
    # ------------------------------------------------------------------

    def _open(self) -> None:
        """Connect, pin the certificate, log in, restore the subscription."""

        if self.insecure and not self._warned:
            self._warned = True
            print(
                "smsdock: WARNING: the certificate of the server is not "
                "checked (insecure). Anyone on the network can read the "
                "password and the messages.",
                file=sys.stderr,
            )
        connecting = (self._connect_factory or connect)(
            self.url,
            ssl=_PinnedContext(self._fingerprint),
            proxy=None,
            open_timeout=self.connect_timeout,
            close_timeout=min(self.timeout, 5.0),
            max_size=MAX_INCOMING_BYTES,
            ping_interval=20,
            ping_timeout=20,
        )
        # websockets wants connect() to be used as a context manager
        # (17.1 warns otherwise). Entering it returns the connection in
        # all supported versions; close() ends it.
        enter = getattr(connecting, "__enter__", None)
        websocket = connecting if enter is None else enter()
        self._websocket = websocket
        self.connection_count += 1
        reconnected = self._epoch is not None
        try:
            if self._fingerprint is not None:
                _check_pin(_peer_certificate(websocket), self._fingerprint)
            self._handshake()
            if reconnected and self.on_reconnect is not None:
                self.on_reconnect(self)
        except BaseException:
            self.close()
            raise

    def _handshake(self) -> None:
        hello = self._receive(self.timeout)
        if hello.get("event") != "hello":
            raise ProtocolError(f"Expected 'hello', received {hello!r}")
        if hello.get("protocolVersion") != API_PROTOCOL_VERSION:
            raise ProtocolError(
                "The server speaks protocol version "
                f"{hello.get('protocolVersion')!r}, this client version "
                f"{API_PROTOCOL_VERSION}"
            )
        result = self._request_patiently(
            "authenticate", {"password": self._password}, self.timeout,
            self.rate_limit_retries,
        )
        if result.get("protocolVersion") != API_PROTOCOL_VERSION:
            raise ProtocolError(
                "The login answered with protocol version "
                f"{result.get('protocolVersion')!r}"
            )
        app = result.get("app")
        self.server_info = app if isinstance(app, dict) else None
        self._note_login(result.get("epoch"), result.get("latestSeq"))
        subscription = self._subscription
        if subscription is not None and subscription["events"]:
            parameters: JsonObject = {"events": list(subscription["events"])}
            if subscription["address"] is not None:
                parameters["address"] = subscription["address"]
            self._request_patiently(
                "subscribe", parameters, self.timeout,
                self.rate_limit_retries,
            )

    def _note_login(self, epoch: Any, latest: Any) -> None:
        """Compare epoch and sequence number with the connection before."""

        if not _is_integer(epoch) or not _is_integer(latest):
            raise ProtocolError(
                "The login answered without 'epoch' and 'latestSeq'"
            )
        if self._epoch is None:
            self._restarted, self._missed = False, 0
        elif epoch != self._epoch:
            self._restarted, self._missed = True, None
        else:
            known = self._latest_seq or 0
            self._restarted = False
            self._missed = max(0, latest - known)
        self._epoch, self._latest_seq = epoch, latest

    def _request_patiently(
        self,
        action: str,
        parameters: JsonObject,
        timeout: float,
        retries: int,
    ) -> JsonObject:
        """Send a request; wait and repeat it while it is rate limited.

        Only when the server names the seconds to wait. The limit of
        messages per minute comes without them and is reported at once.
        """

        refused = 0
        while True:
            try:
                return self._request_once(action, parameters, timeout)
            except ApiError as error:
                wait = error.retry_after
                if (
                    error.code != "rate_limited"
                    or wait is None
                    or wait > timeout
                    or refused >= retries
                ):
                    raise
                refused += 1
                time.sleep(max(0, wait))

    def _recover(
        self, what: str, error: BaseException, attempt: int
    ) -> None:
        """Close after a failure and wait before the next attempt.

        Raises instead when the failure is final or no attempt is left.
        """

        self.close()
        fatal = _fatal_error(error)
        if fatal is not None:
            raise fatal from error
        attempts = self.reconnect_attempts
        if attempts is not None and attempt >= attempts:
            raise _interrupted(
                f"Connection interrupted while {what}", error
            ) from error
        exponent = min(attempt, _MAX_BACKOFF_EXPONENT)
        time.sleep(
            min(self.reconnect_delay * (2 ** exponent),
                self.max_reconnect_delay)
        )

    def _request_once(
        self, action: str, parameters: JsonObject, timeout: float
    ) -> JsonObject:
        key = self._send_frame(action, parameters)
        return self._await_response(
            key, repr(action), time.monotonic() + timeout
        )

    def _send_frame(self, action: str, parameters: JsonObject) -> str:
        websocket = self._websocket
        if websocket is None:
            raise ConnectionInterrupted("No connection to SMS Dock")
        self._request_number += 1
        payload: JsonObject = {"id": f"py-{self._request_number}"}
        payload["action"] = action
        payload.update(parameters)
        frame = json.dumps(payload, separators=(",", ":"), ensure_ascii=False)
        if len(frame.encode("utf-8")) > MAX_FRAME_BYTES:
            raise ValueError(
                f"The request is larger than {MAX_FRAME_BYTES // 1024} KiB"
            )
        key = str(payload["id"])
        websocket.send(frame)
        self._outstanding.add(key)
        return key

    def _await_response(
        self, key: str, label: str, deadline: float
    ) -> JsonObject:
        while True:
            response = self._responses.pop(key, None)
            if response is None:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise TimeoutError(f"No response to {label} in time")
                try:
                    message = self._receive(remaining)
                except TimeoutError:
                    raise TimeoutError(
                        f"No response to {label} in time"
                    ) from None
                if "event" in message:
                    self._queue_event(message)
                    continue
                self._file_response(message)
                continue
            self._outstanding.discard(key)
            if response.get("ok") is True:
                return _object(response.get("result"), f"result of {label}")
            error = response.get("error")
            if response.get("ok") is not False or not isinstance(error, dict):
                raise ProtocolError(f"Malformed response: {response!r}")
            raise _api_error(error)

    def _file_response(self, response: JsonObject) -> None:
        """Keep a response for the request that waits for it."""

        identifier = response.get("id")
        key = None if identifier is None else str(identifier)
        if key is None and len(self._outstanding) == 1:
            # The server could not read the id of the only open request.
            key = next(iter(self._outstanding))
        if key in self._outstanding:
            self._responses[key] = response  # type: ignore[index]
        # Anything else answers a request nobody waits for any more.

    def _receive(self, timeout: float | None) -> JsonObject:
        websocket = self._websocket
        if websocket is None:
            raise ConnectionInterrupted("No connection to SMS Dock")
        frame = websocket.recv(timeout=timeout)
        if not isinstance(frame, str):
            raise ProtocolError("The server sent a binary frame")
        try:
            decoded = json.loads(frame)
        except json.JSONDecodeError as error:
            raise ProtocolError("The server sent invalid JSON") from error
        message = _object(decoded, "frame")
        sequence = message.get("seq")
        if (
            "event" in message
            and _is_integer(sequence)
            and (self._latest_seq is None or sequence > self._latest_seq)
        ):
            self._latest_seq = sequence
        return message

    def _queue_event(self, event: JsonObject) -> None:
        if len(self._events) >= self._event_queue_size:
            self._events.popleft()
            self.dropped_events += 1
        self._events.append(event)

    def _read_event(
        self, deadline: float | None, what: str
    ) -> JsonObject | None:
        """Read from the connection until an event arrives.

        Returns the event without queueing it, or ``None`` when the
        deadline passed. Reconnects when needed.
        """

        attempt = 0
        while True:
            try:
                if self._websocket is None:
                    self._open()
            except _TRANSPORT_ERRORS as error:
                self._recover(what, error, attempt)
                attempt += 1
                continue
            wait: float | None = None
            if deadline is not None:
                wait = deadline - time.monotonic()
                if wait <= 0:
                    return None
            try:
                message = self._receive(wait)
            except TimeoutError:
                return None
            except _TRANSPORT_ERRORS as error:
                self._recover(what, error, attempt)
                attempt += 1
                continue
            attempt = 0
            if "event" in message:
                return message
            self._file_response(message)

    def _wait_for_outcome_event(
        self, request_id: str, deadline: float
    ) -> None:
        """Return at the deadline, or earlier when an outcome arrives."""

        names = ("sms.sent", "sms.delivered", "sms.failed")
        while True:
            event = self._read_event(deadline, "waiting for the result")
            if event is None:
                return
            self._queue_event(event)
            data = event.get("data")
            if (
                event.get("event") in names
                and isinstance(data, dict)
                and data.get("requestId") == request_id
            ):
                return

    def _iterate(
        self,
        action: str,
        filters: JsonObject,
        page_size: int,
        max_items: int | None,
    ) -> Iterator[JsonObject]:
        if max_items is not None and max_items < 0:
            raise ValueError("max_items cannot be negative")
        _paging(page_size, None)
        return self._pages(action, filters, page_size, max_items)

    def _pages(
        self,
        action: str,
        filters: JsonObject,
        page_size: int,
        max_items: int | None,
    ) -> Iterator[JsonObject]:
        cursor: str | None = None
        count = 0
        while max_items is None or count < max_items:
            limit = page_size
            if max_items is not None:
                limit = min(page_size, max_items - count)
            page = self.request(action, **filters, **_paging(limit, cursor))
            items = page.get("items")
            if not isinstance(items, list):
                raise ProtocolError(f"{action} returned no list of items")
            for item in items:
                yield item
                count += 1
                if max_items is not None and count >= max_items:
                    return
            next_cursor = page.get("nextCursor")
            if next_cursor is None:
                return
            if not isinstance(next_cursor, str) or next_cursor == cursor:
                raise ProtocolError(
                    f"{action} returned the cursor {next_cursor!r}"
                )
            cursor = next_cursor

    def _remember_subscription(self, result: JsonObject) -> None:
        events = result.get("events")
        if not isinstance(events, list):
            raise ProtocolError(f"Malformed subscription: {result!r}")
        address = result.get("address")
        self._subscription = {
            "events": [str(name) for name in events],
            "address": address if isinstance(address, str) else None,
        }


class _SerializedSocket(ssl.SSLSocket):
    """TLS socket that never reads and writes at the same time.

    ``websockets.sync`` reads in a background thread while the caller
    writes. One TLS connection must not be used by two threads at once:
    when the server sends TLS 1.3 session tickets while the client writes
    its first request, the request can get lost and the connection hangs.
    This class takes a lock for every read and write. A read never waits
    for data while it holds the lock, so a write is never held up.
    """

    _guard = threading.Lock()

    @property
    def _io_lock(self) -> threading.RLock:
        lock = self.__dict__.get("_smsdock_io_lock")
        if lock is None:
            with _SerializedSocket._guard:
                lock = self.__dict__.setdefault(
                    "_smsdock_io_lock", threading.RLock()
                )
        return lock

    def settimeout(self, value: float | None) -> None:
        """Set the timeout of reads and writes."""

        with self._io_lock:
            super().settimeout(value)

    def recv(self, buflen: int = 1024, flags: int = 0) -> bytes:
        """Read decrypted data; wait outside the lock when there is none."""

        with self._io_lock:
            timeout = self.gettimeout()
        deadline = None if timeout is None else time.monotonic() + timeout
        while True:
            with self._io_lock:
                saved = self.gettimeout()
                super().settimeout(0.0)
                try:
                    return super().recv(buflen, flags)
                except (ssl.SSLWantReadError, BlockingIOError):
                    wanted = selectors.EVENT_READ
                except ssl.SSLWantWriteError:
                    wanted = selectors.EVENT_WRITE
                finally:
                    try:
                        super().settimeout(saved)
                    except OSError:
                        pass
            self._wait(wanted, deadline)

    def send(self, data: Any, flags: int = 0) -> int:
        """Write some data and return the number of bytes written."""

        with self._io_lock:
            return super().send(data, flags)

    def sendall(self, data: Any, flags: int = 0) -> None:
        """Write all data."""

        with self._io_lock:
            super().sendall(data, flags)

    def _wait(self, events: int, deadline: float | None) -> None:
        """Wait until the socket is ready or raise at the deadline.

        The wait ends after ``_WAIT_SLICE`` seconds at the latest, and the
        caller tries again. A socket that another thread closes in the
        meantime may never become ready.
        """

        wait = _WAIT_SLICE
        if deadline is not None:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise socket.timeout("timed out")
            wait = min(wait, remaining)
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(self, events)
                selector.select(wait)
        except ValueError as error:
            raise OSError("The connection is closed") from error


class _PinnedContext(ssl.SSLContext):
    """TLS context for the self-signed certificate of the phone.

    The certificate names no host and has no issuer to check, so host
    name and chain verification are off. Instead the fingerprint of the
    certificate is compared right after the TLS handshake, before
    anything else is sent. Without a fingerprint nothing is compared.
    """

    def __new__(  # noqa: PYI034 - Self needs Python 3.11
        cls, fingerprint: bytes | None
    ) -> _PinnedContext:
        context = super().__new__(cls, ssl.PROTOCOL_TLS_CLIENT)
        context.check_hostname = False
        context.verify_mode = ssl.CERT_NONE
        context.minimum_version = ssl.TLSVersion.TLSv1_2
        context.sslsocket_class = _SerializedSocket
        return context

    def __init__(self, fingerprint: bytes | None) -> None:
        self._pinned = fingerprint

    def wrap_socket(self, *args: Any, **kwargs: Any) -> ssl.SSLSocket:
        """Do the handshake, then compare the fingerprint."""

        tls = super().wrap_socket(*args, **kwargs)
        if self._pinned is not None:
            try:
                tls.do_handshake()
                der = tls.getpeercert(binary_form=True)
                _check_pin(der, self._pinned)
            except BaseException:
                tls.close()
                raise
            tls.__dict__[_CERTIFICATE_KEY] = der
        return tls


def _unverified_context() -> ssl.SSLContext:
    """TLS context that accepts every certificate and compares nothing."""

    return _PinnedContext(None)


def _peer_certificate(websocket: Any) -> bytes | None:
    """Return the DER certificate of the server of a websockets connection.

    ``websockets.sync`` keeps the TLS socket, an :class:`ssl.SSLSocket`,
    in the attribute ``socket`` of the connection (versions 15 to 17).
    """

    tls = getattr(websocket, "socket", None)
    getpeercert = getattr(tls, "getpeercert", None)
    if getpeercert is None:
        raise ProtocolError(
            "Cannot read the certificate of the server from the "
            "connection; refusing to go on"
        )
    try:
        return getpeercert(binary_form=True)
    except (AttributeError, ValueError, OSError) as error:
        # The server closed the connection in the meantime. Use what this
        # socket presented in its TLS handshake, if that was recorded.
        recorded = getattr(tls, "__dict__", {}).get(_CERTIFICATE_KEY)
        if recorded is None:
            raise OSError(
                "The connection was closed before the certificate could "
                "be checked"
            ) from error
        return recorded


def _check_pin(der: bytes | None, pinned: bytes) -> None:
    """Raise :class:`CertificateMismatchError` unless the digest matches."""

    actual = hashlib.sha256(der or b"").digest()
    if not der or not hmac.compare_digest(actual, pinned):
        raise CertificateMismatchError(
            format_fingerprint(pinned.hex()),
            format_fingerprint(actual.hex()) if der else "no certificate",
        )


def _api_error(error: JsonObject) -> ApiError:
    code = str(error.get("code", "unknown"))
    message = str(error.get("message") or "no message")
    details = {
        key: value
        for key, value in error.items()
        if key not in ("code", "message")
    }
    if code in ("invalid_password", "locked_out", "not_authenticated"):
        return AuthenticationError(code, message, details)
    if code == "licence_required":
        return LicenceRequiredError(code, message, details)
    return ApiError(code, message, details)


def _close_frame(
    error: BaseException,
) -> tuple[int | None, str | None]:
    received = getattr(error, "rcvd", None)
    if not isinstance(error, ConnectionClosed) or received is None:
        return None, None
    return received.code, received.reason or None


def _fatal_error(error: BaseException) -> SmsDockError | None:
    """Return the error to raise for a close code that forbids a retry."""

    code, reason = _close_frame(error)
    details = {"closeCode": code}
    if code == 4401:
        return AuthenticationError(
            "not_authenticated",
            reason or "The server closed the connection: not logged in",
            details,
        )
    if code == 4410:
        return AuthenticationError(
            "not_authenticated",
            reason or "The password was changed in the app",
            details,
        )
    if code == 4403:
        return LicenceRequiredError(
            "licence_required",
            reason or "The API needs the full licence of the app",
            details,
        )
    if code == 1009:
        return ProtocolError(
            "The server closed the connection: frame larger than 64 KiB"
        )
    if code == 4400:
        return ProtocolError(
            "The server closed the connection: protocol error"
            + (f" ({reason})" if reason else "")
        )
    return None


def _interrupted(message: str, error: BaseException) -> ConnectionInterrupted:
    code, reason = _close_frame(error)
    if code is not None:
        cause = f"closed by the server with code {code}"
        if reason:
            cause += f" ({reason})"
    else:
        cause = str(error) or type(error).__name__
    return ConnectionInterrupted(f"{message}: {cause}", code, reason)


def _object(value: Any, label: str) -> JsonObject:
    if not isinstance(value, dict):
        raise ProtocolError(f"The {label} is not a JSON object")
    return value


def _is_integer(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _integer(value: Any, label: str) -> int:
    if not _is_integer(value):
        raise TypeError(f"{label} must be an integer")
    return value


def _millis(value: Timestamp, label: str) -> int:
    if isinstance(value, datetime):
        return int(value.timestamp() * 1000)
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise TypeError(
            f"{label} must be milliseconds since the epoch or a datetime"
        )
    return int(value)


def _paging(limit: int, cursor: str | None) -> JsonObject:
    if (
        isinstance(limit, bool)
        or not isinstance(limit, int)
        or not 1 <= limit <= MAX_PAGE_SIZE
    ):
        raise ValueError(f"limit must be between 1 and {MAX_PAGE_SIZE}")
    parameters: JsonObject = {"limit": limit}
    if cursor is not None:
        parameters["cursor"] = cursor
    return parameters


def _conversation_filter(
    q: str | None, unread: bool, sim: int | None
) -> JsonObject:
    filters: JsonObject = {}
    if q is not None:
        filters["q"] = q
    if unread:
        filters["unread"] = True
    if sim is not None:
        filters["sim"] = _integer(sim, "sim")
    return filters


def _message_filter(
    address: str | None,
    conversation_id: int | None,
    direction: str,
    q: str | None,
    since: Timestamp | None,
    until: Timestamp | None,
    sim: int | None,
    unread: bool,
) -> JsonObject:
    if direction not in _DIRECTIONS:
        raise ValueError("direction must be 'in', 'out' or 'all'")
    filters: JsonObject = {}
    if address is not None:
        filters["address"] = address
    if conversation_id is not None:
        filters["conversationId"] = _integer(
            conversation_id, "conversation_id"
        )
    if direction != "all":
        filters["direction"] = direction
    if q is not None:
        filters["q"] = q
    if since is not None:
        filters["from"] = _millis(since, "since")
    if until is not None:
        filters["to"] = _millis(until, "until")
    if sim is not None:
        filters["sim"] = _integer(sim, "sim")
    if unread:
        filters["unread"] = True
    return filters


def _event_names(events: str | Iterable[str]) -> list[str]:
    names = [events] if isinstance(events, str) else list(events)
    if not names or not all(isinstance(n, str) and n for n in names):
        raise ValueError("events must be one event name or several")
    return names
