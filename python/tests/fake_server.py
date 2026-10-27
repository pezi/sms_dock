"""A fake SMS Dock server that speaks protocol version 1 of ``API.md``.

It keeps all data in memory and sends nothing. The tests use it in a
background thread. It can also run on its own to try the examples and the
command-line tool without a phone::

    python tests/fake_server.py --port 9443 --incoming-every 15

The real server runs on the phone. This one follows ``API.md``; where the
document leaves something open, a comment names the assumption.
"""

from __future__ import annotations

import argparse
import asyncio
import http
import json
import math
import re
import shutil
import ssl
import subprocess
import tempfile
import threading
import time
import uuid
from collections import deque
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from websockets.asyncio.server import ServerConnection, serve
from websockets.exceptions import ConnectionClosed

JsonObject = dict[str, Any]

PROTOCOL_VERSION = 1
MAX_FRAME_BYTES = 64 * 1024
EVENT_NAMES = (
    "sms.received",
    "sms.queued",
    "sms.sent",
    "sms.delivered",
    "sms.failed",
    "sms.deleted",
    "conversations.changed",
)
_REQUEST_ID = re.compile(r"[A-Za-z0-9._-]{1,64}")
_NUMBER = re.compile(r"\+?[0-9]{3,15}")


@dataclass
class TlsFiles:
    """A throwaway self-signed certificate made with ``openssl``."""

    certificate: Path
    key: Path
    fingerprint: str

    def server_context(self) -> ssl.SSLContext:
        """Return the TLS context of a server that presents it."""

        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(str(self.certificate), str(self.key))
        return context


def make_certificate(directory: Path, name: str = "server") -> TlsFiles | None:
    """Create a self-signed EC certificate like the one of the phone.

    Returns ``None`` when the ``openssl`` command is missing or fails.
    The fingerprint is the one ``openssl`` prints, so that the tests
    compare the client with an independent implementation.
    """

    openssl = shutil.which("openssl")
    if openssl is None:
        return None
    key = directory / f"{name}.key"
    certificate = directory / f"{name}.crt"
    commands = [
        [openssl, "ecparam", "-name", "prime256v1", "-genkey", "-noout",
         "-out", str(key)],
        [openssl, "req", "-x509", "-new", "-key", str(key), "-sha256",
         "-days", "2", "-subj", "/CN=SMS Dock", "-out", str(certificate)],
    ]
    try:
        for command in commands:
            subprocess.run(command, check=True, capture_output=True, timeout=60)
        shown = subprocess.run(
            [openssl, "x509", "-in", str(certificate), "-noout",
             "-fingerprint", "-sha256"],
            check=True, capture_output=True, timeout=60, text=True,
        ).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    fingerprint = shown.strip().rsplit("=", 1)[-1]
    if not re.fullmatch(r"([0-9A-F]{2}:){31}[0-9A-F]{2}", fingerprint):
        return None
    return TlsFiles(certificate, key, fingerprint)


class _Failure(Exception):
    """An error response, optionally followed by a close frame."""

    def __init__(
        self,
        code: str,
        message: str,
        close_code: int | None = None,
        **extra: Any,
    ) -> None:
        super().__init__(message)
        self.error: JsonObject = {"code": code, "message": message, **extra}
        self.close_code = close_code


@dataclass
class _Client:
    connection: ServerConnection
    authenticated: bool = False
    events: set[str] = field(default_factory=set)
    address: str | None = None
    request_times: deque[float] = field(default_factory=deque)
    refused: int = 0


def normalize_number(number: str) -> str:
    """Remove the characters that are ignored in phone numbers.

    These are spaces, dashes, dots, slashes and brackets.
    """

    return re.sub(r"[\s\-./()]", "", number.strip())


def is_number(address: str) -> bool:
    """Whether an address is a phone number and not a sender name."""

    return re.fullmatch(r"\+?[0-9]+", normalize_number(address)) is not None


def address_key(address: str) -> str:
    """Return a key that is equal for equal addresses where that is known.

    The international form for numbers written with ``+`` or ``00``, the
    bare digits for other numbers, lower case for sender names.
    """

    if not is_number(address):
        return address.strip().lower()
    digits = normalize_number(address)
    if digits.startswith("00") and len(digits) > 4:
        return "+" + digits[2:]
    return digits


def same_peer(first: str, second: str) -> bool:
    """Whether two addresses mean the same peer ("Phone numbers" in API.md).

    ``+43 699 1234567``, ``0043 699 1234567`` and ``0699 1234567`` are the
    same. A number without country code matches if its digits without
    leading zeros, at least 7, end the other number. Sender names are
    compared as text, ignoring case.
    """

    a, b = address_key(first), address_key(second)
    if a == b:
        return True
    if not is_number(first) or not is_number(second):
        return False
    if a.startswith("+") and b.startswith("+"):
        return False

    def ends(full: str, national: str) -> bool:
        significant = national.lstrip("0")
        return len(significant) >= 7 and full.endswith(significant)

    if a.startswith("+"):
        return ends(a, b)
    if b.startswith("+"):
        return ends(b, a)
    return ends(a, b) or ends(b, a)


def count_parts(body: str) -> int:
    """Return the number of SMS parts of a text (simplified alphabet test)."""

    plain = all(ord(character) < 128 for character in body)
    single, multi = (160, 153) if plain else (70, 67)
    if len(body) <= single:
        return 1
    return math.ceil(len(body) / multi)


class FakeSmsDock:
    """In-memory SMS Dock server on ``wss://127.0.0.1:PORT/ws``.

    Without ``tls`` the server speaks plain ``ws://``. Attributes such as
    :attr:`messages`, :attr:`sent` and :attr:`requests` may be read from
    the test thread; use the methods to change anything while clients are
    connected.
    """

    def __init__(
        self,
        *,
        password: str = "secret123",
        tls: TlsFiles | None = None,
        host: str = "127.0.0.1",
        port: int = 0,
        auth_timeout: float = 10.0,
        max_clients: int = 8,
        send_delay: float = 0.02,
        delivery_reports: bool = False,
        requests_per_second: int = 20,
        max_refused: int = 20,
    ) -> None:
        self.password = password
        self.tls = tls
        self.host = host
        self.port = port
        self.auth_timeout = auth_timeout
        self.max_clients = max_clients
        self.send_delay = send_delay
        self.delivery_reports = delivery_reports
        self.requests_per_second = requests_per_second
        self.max_refused = max_refused

        self.epoch = int(time.time() * 1000)
        """Identifies one run of the app; :meth:`restart` changes it."""
        self.protocol_version = PROTOCOL_VERSION
        self.licence_enforced = False
        self.licensed = False
        self.delete_mode = "hidden"
        self.failed_logins = 0
        self.lock_count = 0
        self.locked_until = 0.0

        self.sims: list[JsonObject] = [
            {"subscriptionId": 1, "slot": 0, "name": "Play",
             "number": None, "isDefault": True},
            {"subscriptionId": 2, "slot": 1, "name": "Work",
             "number": "+4369087654321", "isDefault": False},
        ]
        self.contacts: list[JsonObject] = []
        self.messages: list[JsonObject] = []
        self.outbox: dict[str, JsonObject] = {}
        self.fail_numbers: set[str] = set()
        self.sent: list[JsonObject] = []
        #: Numbers the phone refuses at once (``send_refused``).
        self.refuse_numbers: set[str] = set()
        """Messages handed to the (imaginary) radio, one entry per SMS."""
        self.requests: list[JsonObject] = []
        """Every request object received and not refused, in order."""
        self.refused_count = 0
        """Number of frames answered with ``rate_limited`` by the limiter."""
        self.connection_count = 0
        self.login_count = 0

        self._next_message_id = 2800
        self._conversation_ids: dict[str, int] = {}
        self._bodies: dict[str, str] = {}
        self._sequence = 0
        self._clients: list[_Client] = []
        self._drops: dict[str, list[str]] = {}
        self._failures: dict[str, list[_Failure]] = {}
        self._results: dict[str, list[JsonObject]] = {}
        self._ignored: dict[str, int] = {}
        self._frames_before_response: list[str | JsonObject] = []
        self._hold = 0
        self._held: list[JsonObject] = []
        self._tasks: set[asyncio.Task[Any]] = set()
        self._loop: asyncio.AbstractEventLoop | None = None
        self._stop: asyncio.Event | None = None
        self._thread: threading.Thread | None = None
        self._ready = threading.Event()
        self._startup_error: BaseException | None = None

    # ------------------------------------------------------------------
    # Life cycle
    # ------------------------------------------------------------------

    @property
    def url(self) -> str:
        """The address of the API."""

        scheme = "wss" if self.tls is not None else "ws"
        return f"{scheme}://{self.host}:{self.port}/ws"

    @property
    def client_count(self) -> int:
        """Number of open connections."""

        return len(self._clients)

    def start(self) -> FakeSmsDock:
        """Start the server in a background thread."""

        self._thread = threading.Thread(
            target=self._run_thread, name="fake-smsdock", daemon=True
        )
        self._thread.start()
        if not self._ready.wait(10):
            raise RuntimeError("The fake server did not start")
        if self._startup_error is not None:
            raise RuntimeError("The fake server failed") from self._startup_error
        return self

    def stop(self) -> None:
        """Stop the server; clients see the close code 1001."""

        loop, stop = self._loop, self._stop
        if loop is not None and stop is not None and loop.is_running():
            loop.call_soon_threadsafe(stop.set)
        if self._thread is not None:
            self._thread.join(10)
        self._thread = None

    def __enter__(self) -> FakeSmsDock:  # noqa: PYI034 - Self needs 3.11
        return self.start()

    def __exit__(self, *_: object) -> None:
        self.stop()

    def _run_thread(self) -> None:
        try:
            asyncio.run(self._main())
        except BaseException as error:  # noqa: BLE001 - start() reports it
            self._startup_error = error
            self._ready.set()

    async def _main(self) -> None:
        self._loop = asyncio.get_running_loop()
        self._stop = asyncio.Event()
        context = None if self.tls is None else self.tls.server_context()
        async with serve(
            self._handle,
            self.host,
            self.port,
            ssl=context,
            max_size=MAX_FRAME_BYTES,
            ping_interval=20,
            ping_timeout=45,
            process_request=self._check_path,
        ) as server:
            self.port = server.sockets[0].getsockname()[1]
            self._ready.set()
            await self._stop.wait()
            for task in list(self._tasks):
                task.cancel()

    def _call(self, coroutine: Any) -> Any:
        """Run a coroutine in the server thread and wait for it."""

        if self._loop is None:
            raise RuntimeError("The fake server is not running")
        future = asyncio.run_coroutine_threadsafe(coroutine, self._loop)
        return future.result(10)

    # ------------------------------------------------------------------
    # Controls for tests (thread-safe)
    # ------------------------------------------------------------------

    def add_contact(self, name: str, number: str, label: str = "Mobile") -> None:
        """Add a contact with one number."""

        self.contacts.append(
            {"name": name, "numbers": [{"number": number, "label": label}]}
        )

    def add_message(
        self,
        address: str,
        body: str,
        *,
        direction: str = "in",
        timestamp: int | None = None,
        read: bool = True,
        sim: int | None = 1,
        request_id: str | None = None,
    ) -> JsonObject:
        """Store a message without sending an event."""

        if timestamp is None:
            timestamp = int(time.time() * 1000)
        incoming = direction == "in"
        self._next_message_id += 1
        message = {
            "id": self._next_message_id,
            "conversationId": self._conversation_id(address),
            "address": address,
            "contact": self._contact_name(address),
            "direction": direction,
            "body": body,
            "timestamp": timestamp,
            "sentTimestamp": timestamp - 2000 if incoming else None,
            "read": read,
            "status": "received" if incoming else "sent",
            "sim": sim,
            "requestId": request_id,
        }
        self.messages.append(message)
        return message

    def receive_sms(self, address: str, body: str, sim: int = 1) -> JsonObject:
        """Let an SMS arrive: store it and send ``sms.received``."""

        return self._call(self._receive_sms(address, body, sim))

    def emit(self, name: str, data: JsonObject) -> None:
        """Send an event to the subscribed clients."""

        self._call(self._emit(name, data))

    def restart(self) -> None:
        """Behave like a restart of the app.

        All connections are lost, the sequence numbers begin at 1 again
        and ``epoch`` changes. Messages and the outbox are kept.
        """

        self._call(self._restart())

    def drop_connections(
        self, code: int | None = None, reason: str = ""
    ) -> None:
        """Close all connections: abruptly, or with a close code."""

        self._call(self._drop_all(code, reason))

    def drop_on(self, action: str, when: str = "after") -> None:
        """Drop the connection at the next request with this action.

        ``when="before"`` drops before the request is carried out,
        ``when="after"`` after it was carried out but before the response
        is sent. Calling it again queues one more drop.
        """

        if when not in ("before", "after"):
            raise ValueError("when must be 'before' or 'after'")
        self._drops.setdefault(action, []).append(when)

    def fail_next(
        self,
        action: str,
        code: str,
        message: str = "Injected failure.",
        close_code: int | None = None,
        **extra: Any,
    ) -> None:
        """Answer the next request with this action with an error."""

        self._failures.setdefault(action, []).append(
            _Failure(code, message, close_code, **extra)
        )

    def answer_next(self, action: str, result: JsonObject) -> None:
        """Answer the next request with this action with this result."""

        self._results.setdefault(action, []).append(result)

    def ignore_next(self, action: str, times: int = 1) -> None:
        """Do not answer the next requests with this action at all."""

        self._ignored[action] = self._ignored.get(action, 0) + times

    def before_next_response(self, *frames: str | JsonObject) -> None:
        """Send these frames to the client right before the next response."""

        self._frames_before_response.extend(frames)

    def reverse_next_responses(self, count: int = 2) -> None:
        """Hold the next ``count`` responses and send them in reverse order."""

        self._hold = count

    def lock_logins(self, seconds: float) -> None:
        """Lock logins as after too many wrong passwords."""

        self.locked_until = time.monotonic() + seconds

    def seed_demo_data(self) -> None:
        """Fill the server with a few contacts and conversations."""

        now = int(time.time() * 1000)
        self.add_contact("Anna Example", "+4369912345678")
        self.add_contact("Ben Example", "+4369087654321", "Work")
        minute = 60_000
        rows = [
            ("+4369912345678", "in", "Are you coming on Friday?", 300),
            ("+4369912345678", "out", "Yes, at eight.", 290),
            ("+4369912345678", "in", "Great, see you then.", 280),
            ("+4369087654321", "out", "The server is up again.", 200),
            ("+4369087654321", "in", "Thanks!", 190),
            ("A1", "in", "Your invoice is ready.", 120),
            ("+436641234567", "in", "STATUS", 5),
        ]
        for address, direction, body, age in rows:
            self.add_message(
                address, body, direction=direction,
                timestamp=now - age * minute, read=age > 100,
            )

    # ------------------------------------------------------------------
    # Connections
    # ------------------------------------------------------------------

    async def _check_path(self, connection: ServerConnection, request: Any) -> Any:
        if request.path.split("?", 1)[0] != "/ws":
            return connection.respond(http.HTTPStatus.NOT_FOUND, "Not found\n")
        return None

    async def _handle(self, connection: ServerConnection) -> None:
        if len(self._clients) >= self.max_clients:
            await connection.close(4409, "too many clients")
            return
        client = _Client(connection)
        self._clients.append(client)
        self.connection_count += 1
        timer = asyncio.get_running_loop().call_later(
            self.auth_timeout, self._auth_timed_out, client
        )
        try:
            await self._send(client, {
                "event": "hello",
                "protocolVersion": self.protocol_version,
                "authenticationRequired": True,
            })
            async for frame in connection:
                if not isinstance(frame, str):
                    await connection.close(4400, "binary frame")
                    return
                await self._on_frame(client, frame)
        except ConnectionClosed:
            pass
        finally:
            timer.cancel()
            if client in self._clients:
                self._clients.remove(client)

    def _spawn(self, coroutine: Any) -> None:
        task = asyncio.get_running_loop().create_task(coroutine)
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)

    def _auth_timed_out(self, client: _Client) -> None:
        if not client.authenticated:
            self._spawn(
                client.connection.close(4401, "not authenticated in time")
            )

    async def _restart(self) -> None:
        await self._drop_all(None)
        self._sequence = 0
        self.epoch = max(self.epoch + 1, int(time.time() * 1000))

    async def _drop_all(self, code: int | None, reason: str = "") -> None:
        for client in list(self._clients):
            if code is None:
                client.connection.transport.abort()
            else:
                await client.connection.close(code, reason)

    async def _send(self, client: _Client, frame: str | JsonObject) -> None:
        text = frame if isinstance(frame, str) else json.dumps(
            frame, separators=(",", ":"), ensure_ascii=False
        )
        try:
            await client.connection.send(text)
        except ConnectionClosed:
            pass

    async def _on_frame(self, client: _Client, frame: str) -> None:
        try:
            request = json.loads(frame)
        except json.JSONDecodeError:
            request = None
        if not self._within_rate(client):
            # Every frame counts, the login and unreadable ones as well.
            identifier = request.get("id") if isinstance(request, dict) else None
            if isinstance(identifier, bool) or not isinstance(
                identifier, (str, int, float)
            ):
                identifier = None
            self.refused_count += 1
            await self._send(client, {
                "id": identifier, "ok": False,
                "error": {"code": "rate_limited",
                          "message": "Too many requests.", "retryAfter": 1},
            })
            client.refused += 1
            if client.refused > self.max_refused:
                await client.connection.close(4429, "Too many requests.")
            return
        client.refused = 0
        if not isinstance(request, dict):
            await self._respond(client, {
                "id": None, "ok": False,
                "error": {"code": "invalid_json",
                          "message": "The frame is not a JSON object."},
            })
            return
        self.requests.append(request)
        identifier = request.get("id")
        action = request.get("action")
        drops = self._drops.get(action) if isinstance(action, str) else None
        if drops and drops[0] == "before":
            drops.pop(0)
            client.connection.transport.abort()
            return

        close_code: int | None = None
        response: JsonObject
        try:
            result = await self._dispatch(client, request)
            if result is None:
                return
            response = {"id": identifier, "ok": True, "result": result}
        except _Failure as failure:
            response = {"id": identifier, "ok": False, "error": failure.error}
            close_code = failure.close_code

        if drops and drops[0] == "after":
            drops.pop(0)
            client.connection.transport.abort()
            return
        await self._respond(client, response)
        if close_code is not None:
            await client.connection.close(close_code, response["error"]["code"])

    def _within_rate(self, client: _Client) -> bool:
        """Count a frame; at most ``requests_per_second`` fit in a second."""

        now = time.monotonic()
        times = client.request_times
        while times and now - times[0] >= 1.0:
            times.popleft()
        if len(times) >= self.requests_per_second:
            return False
        times.append(now)
        return True

    async def _respond(self, client: _Client, response: JsonObject) -> None:
        frames, self._frames_before_response = self._frames_before_response, []
        for frame in frames:
            await self._send(client, frame)
        if self._hold > 0:
            self._held.append(response)
            if len(self._held) < self._hold:
                return
            held, self._held, self._hold = self._held, [], 0
            for item in reversed(held):
                await self._send(client, item)
            return
        await self._send(client, response)

    # ------------------------------------------------------------------
    # Actions
    # ------------------------------------------------------------------

    async def _dispatch(
        self, client: _Client, request: JsonObject
    ) -> JsonObject | None:
        action = request.get("action")
        if not isinstance(action, str):
            raise _Failure("invalid_request", "The field 'action' is missing.")
        identifier = request.get("id")
        if isinstance(identifier, bool) or not isinstance(
            identifier, (str, int, float, type(None))
        ):
            raise _Failure("invalid_request", "'id' must be a string or a number.")
        if self._ignored.get(action, 0) > 0:
            self._ignored[action] -= 1
            return None
        if action == "authenticate":
            return self._authenticate(client, request)
        if not client.authenticated:
            raise _Failure("not_authenticated", "Authenticate first.")
        failures = self._failures.get(action)
        if failures:
            raise failures.pop(0)
        results = self._results.get(action)
        if results:
            return results.pop(0)
        handlers = {
            "status": self._status,
            "ping": self._ping,
            "conversations.list": self._conversations_list,
            "messages.list": self._messages_list,
            "messages.get": self._messages_get,
            "messages.status": self._messages_status,
            "messages.delete": self._messages_delete,
            "conversations.delete": self._conversations_delete,
            "conversations.markRead": self._conversations_mark_read,
            "contacts.list": self._contacts_list,
        }
        if action in handlers:
            return handlers[action](request)
        if action == "messages.send":
            return await self._messages_send(request)
        if action == "messages.sendGroup":
            return await self._messages_send_group(request)
        if action == "subscribe":
            return self._subscribe(client, request)
        if action == "unsubscribe":
            return self._unsubscribe(client, request)
        raise _Failure("unknown_action", f"Unknown action '{action}'.")

    def _authenticate(
        self, client: _Client, request: JsonObject
    ) -> JsonObject:
        self.login_count += 1
        # Assumption, as in the app: the licence is looked at before the
        # password, so a login without licence never counts as wrong.
        if self.licence_enforced and not self.licensed:
            raise _Failure(
                "licence_required",
                "The API needs the full licence of SMS Dock.", 4403,
            )
        remaining = self.locked_until - time.monotonic()
        if remaining > 0:
            raise _Failure(
                "locked_out",
                f"Too many attempts. Try again in {math.ceil(remaining)} "
                "seconds.",
                4401,
                retryAfter=math.ceil(remaining),
            )
        if request.get("password") != self.password:
            self.failed_logins += 1
            if self.failed_logins >= 5:
                seconds = min(30 * (2 ** self.lock_count), 900)
                self.lock_count += 1
                self.failed_logins = 0
                self.locked_until = time.monotonic() + seconds
            raise _Failure("invalid_password", "Wrong password.", 4401)
        self.failed_logins = 0
        self.lock_count = 0
        client.authenticated = True
        return {
            "protocolVersion": self.protocol_version,
            "app": {"name": "SMS Dock", "version": "1.0.0",
                    "protocolVersion": self.protocol_version},
            "latestSeq": self._sequence,
            "epoch": self.epoch,
        }

    def _status(self, request: JsonObject) -> JsonObject:
        sent_today = len(self.sent)
        return {
            "app": {"name": "SMS Dock", "version": "1.0.0",
                    "protocolVersion": self.protocol_version},
            "sims": self.sims,
            "permissions": {
                "readSms": True, "sendSms": True, "receiveSms": True,
                "contacts": True, "phoneState": True, "notifications": True,
            },
            "defaultSmsApp": self.delete_mode == "provider",
            "deleteMode": self.delete_mode,
            "licence": {
                "enforced": self.licence_enforced,
                "licensed": self.licensed,
                "dailyLimit": 10,
                "sentToday": sent_today,
                "remaining": None,
            },
            "popup": True,
        }

    def _ping(self, request: JsonObject) -> JsonObject:
        return {"time": int(time.time() * 1000)}

    def _conversations_list(self, request: JsonObject) -> JsonObject:
        query = _optional(request, "q", str)
        unread = _optional(request, "unread", bool) or False
        sim = _optional(request, "sim", int)
        items = self._conversations()
        if query:
            needle = query.lower()
            matching = {
                message["conversationId"] for message in self.messages
                if needle in message["body"].lower()
            }
            items = [
                item for item in items
                if item["id"] in matching
                or needle in item["address"].lower()
                or needle in (item["contact"] or "").lower()
            ]
        if unread:
            items = [item for item in items if item["unreadCount"] > 0]
        if sim is not None:
            items = [item for item in items if item["sim"] == sim]
        return _page(items, request)

    def _messages_list(self, request: JsonObject) -> JsonObject:
        address = _optional(request, "address", str)
        conversation = _optional(request, "conversationId", int)
        direction = _optional(request, "direction", str) or "all"
        query = _optional(request, "q", str)
        start = _optional(request, "from", int)
        end = _optional(request, "to", int)
        sim = _optional(request, "sim", int)
        unread = _optional(request, "unread", bool) or False
        if direction not in ("in", "out", "all"):
            raise _Failure(
                "invalid_request", "'direction' must be 'in', 'out' or 'all'."
            )
        items = sorted(
            self.messages,
            key=lambda message: (message["timestamp"], message["id"]),
            reverse=True,
        )
        if conversation is not None:
            # conversationId wins if address is given too.
            items = [m for m in items if m["conversationId"] == conversation]
        elif address is not None:
            items = [m for m in items if same_peer(m["address"], address)]
        if direction != "all":
            items = [m for m in items if m["direction"] == direction]
        if query:
            items = [m for m in items if query.lower() in m["body"].lower()]
        if start is not None:
            items = [m for m in items if m["timestamp"] >= start]
        if end is not None:
            items = [m for m in items if m["timestamp"] <= end]
        if sim is not None:
            items = [m for m in items if m["sim"] == sim]
        if unread:
            items = [m for m in items if not m["read"]]
        return _page(items, request)

    def _find_message(self, request: JsonObject) -> JsonObject:
        identifier = _required(request, "messageId", int)
        for message in self.messages:
            if message["id"] == identifier:
                return message
        raise _Failure("not_found", f"No message with the id {identifier}.")

    def _messages_get(self, request: JsonObject) -> JsonObject:
        return self._find_message(request)

    def _messages_status(self, request: JsonObject) -> JsonObject:
        request_id = _required(request, "requestId", str)
        entry = self.outbox.get(request_id)
        if entry is None:
            raise _Failure("not_found", f"No message with the requestId {request_id}.")
        return dict(entry)

    def _messages_delete(self, request: JsonObject) -> JsonObject:
        message = self._find_message(request)
        self.messages.remove(message)
        self._spawn(self._emit("sms.deleted", {
            "id": message["id"],
            "conversationId": message["conversationId"],
            "address": message["address"],
            "mode": self.delete_mode,
        }))
        return {"deleted": 1, "mode": self.delete_mode}

    def _conversations_delete(self, request: JsonObject) -> JsonObject:
        identifier = _required(request, "conversationId", int)
        doomed = [m for m in self.messages if m["conversationId"] == identifier]
        if not doomed:
            raise _Failure("not_found", "This conversation does not exist.")
        newest = max(doomed, key=lambda m: (m["timestamp"], m["id"]))
        self.messages = [m for m in self.messages if m not in doomed]
        self._spawn(self._emit("sms.deleted", {
            "id": None, "conversationId": identifier,
            "address": newest["address"], "mode": self.delete_mode,
        }))
        return {"deleted": len(doomed), "mode": self.delete_mode}

    def _conversations_mark_read(self, request: JsonObject) -> JsonObject:
        # API.md names no event for this action and no order of the two
        # errors. Assumption, as in the app: no event; the default SMS app
        # is checked first.
        identifier = _required(request, "conversationId", int)
        if self.delete_mode != "provider":
            raise _Failure(
                "not_default_app",
                "Only the default SMS app can mark messages as read.",
            )
        found = [m for m in self.messages if m["conversationId"] == identifier]
        if not found:
            raise _Failure("not_found", "This conversation does not exist.")
        for message in found:
            message["read"] = True
        return {"ok": True}

    def _contacts_list(self, request: JsonObject) -> JsonObject:
        query = _optional(request, "q", str)
        items = sorted(self.contacts, key=lambda contact: contact["name"].lower())
        if query:
            needle = query.lower()
            items = [
                contact for contact in items
                if needle in contact["name"].lower()
                or any(needle in n["number"] for n in contact["numbers"])
            ]
        return _page(items, request, by_index=True)

    async def _messages_send(self, request: JsonObject) -> JsonObject:
        to = _required(request, "to", str)
        body = _required(request, "body", str)
        sim = _optional(request, "sim", int)
        request_id = _optional(request, "requestId", str)
        if request_id is not None and not _REQUEST_ID.fullmatch(request_id):
            raise _Failure(
                "invalid_request",
                "'requestId' must be 1 to 64 characters of A-Z a-z 0-9 . _ -.",
            )
        number = normalize_number(to)
        if not _NUMBER.fullmatch(number):
            raise _Failure("invalid_number", "'to' is not a phone number.")
        if not body:
            raise _Failure("invalid_request", "'body' is empty.")
        parts = count_parts(body)
        if parts > 10:
            raise _Failure(
                "message_too_long",
                f"The message needs {parts} SMS parts; at most 10 are allowed.",
            )
        known = None if request_id is None else self.outbox.get(request_id)
        if known is not None:
            # A repeat: never sent twice, answered with the current status.
            if self._bodies[known["requestId"]] != body or not same_peer(
                known["to"], number
            ):
                raise _Failure(
                    "invalid_request",
                    "'requestId' was already used for another message.",
                )
            return {"requestId": known["requestId"], "to": known["to"],
                    "parts": known["parts"], "status": known["status"]}
        sim = self._subscription(sim)
        if request_id is None:
            request_id = uuid.uuid4().hex
        entry = await self._accept(request_id, number, body, parts, sim)
        return _send_result(entry)

    def _subscription(self, sim: int | None) -> int:
        """Return the SIM to send with; the default one without ``sim``."""

        if sim is None:
            return next(s["subscriptionId"] for s in self.sims if s["isDefault"])
        if all(s["subscriptionId"] != sim for s in self.sims):
            raise _Failure("sim_unavailable", f"No SIM with the subscription id {sim}.")
        return sim

    async def _accept(
        self, request_id: str, number: str, body: str, parts: int, sim: int,
        group: str | None = None,
    ) -> JsonObject:
        """Queue one message that passed the checks and start sending it."""

        entry = {
            "requestId": request_id, "to": number, "parts": parts,
            "partsSent": 0, "status": "queued", "errorCode": None,
            "messageId": None, "sim": sim, "source": "api",
            "createdAt": int(time.time() * 1000),
        }
        self.outbox[request_id] = entry
        self._bodies[request_id] = body
        sent = {"requestId": request_id, "to": number, "body": body, "sim": sim}
        if group is not None:
            sent["group"] = group
        self.sent.append(sent)
        await self._emit("sms.queued", {
            "requestId": request_id, "to": number, "parts": parts,
            "sim": sim, "source": "api",
        })
        if number in self.refuse_numbers:
            # The phone did not even take the message.
            entry.update(status="failed", errorCode=None)
            await self._emit("sms.failed", {
                "requestId": request_id, "id": None, "conversationId": None,
                "to": number, "errorCode": None, "reason": "send_refused",
            })
            return entry
        self._spawn(self._transmit(entry, body))
        return entry

    async def _messages_send_group(self, request: JsonObject) -> JsonObject:
        to = _required(request, "to", list)
        body = _required(request, "body", str)
        sim = _optional(request, "sim", int)
        request_id = _optional(request, "requestId", str)
        if not to:
            raise _Failure("invalid_request", "'to' is missing.")
        if any(not isinstance(item, str) for item in to):
            raise _Failure("invalid_request", "'to' must contain strings.")
        numbers: list[str] = []
        for item in to:
            number = normalize_number(item)
            if not _NUMBER.fullmatch(number):
                raise _Failure(
                    "invalid_number",
                    f"'to' contains '{item}', which is not a phone number.",
                )
            if not any(same_peer(known, number) for known in numbers):
                numbers.append(number)
        if len(numbers) > 20:
            raise _Failure(
                "invalid_request", "A group message goes to at most 20 recipients.",
            )
        if not body:
            raise _Failure("invalid_request", "'body' is empty.")
        parts = count_parts(body)
        if parts > 10:
            raise _Failure(
                "message_too_long",
                f"The message needs {parts} SMS parts; at most 10 are allowed.",
            )
        if request_id is not None and (
            not _REQUEST_ID.fullmatch(request_id) or len(request_id) > 61
        ):
            raise _Failure(
                "invalid_request",
                "'requestId' of a group must be 1 to 61 characters of "
                "A-Z a-z 0-9 . _ -.",
            )
        if request_id is None:
            request_id = uuid.uuid4().hex
        known = self._known_group(request_id)
        if known:
            same = len(known) == len(numbers) and all(
                self._bodies[m["requestId"]] == body for m in known
            ) and all(
                any(same_peer(m["to"], number) for m in known) for number in numbers
            )
            if not same:
                raise _Failure(
                    "invalid_request",
                    "'requestId' was already used for another message.",
                )
            return _group_result(request_id, known)
        total = parts * len(numbers)
        if total > 20:
            raise _Failure(
                "invalid_request",
                f"This group needs {total} SMS parts; at most 20 can be sent "
                "per minute. Send to fewer recipients or shorten the text.",
            )
        sim = self._subscription(sim)
        members = [
            await self._accept(f"{request_id}.{index + 1}", number, body, parts,
                               sim, request_id)
            for index, number in enumerate(numbers)
        ]
        return _group_result(request_id, members)

    def _known_group(self, request_id: str) -> list[JsonObject]:
        """Return the members of a group sent before, oldest first."""

        members: list[JsonObject] = []
        index = 1
        while f"{request_id}.{index}" in self.outbox:
            members.append(self.outbox[f"{request_id}.{index}"])
            index += 1
        return members

    async def _transmit(self, entry: JsonObject, body: str) -> None:
        number = entry["to"]
        if entry["parts"] > 1 and number not in self.fail_numbers:
            # Some parts are sent, some are left.
            await asyncio.sleep(self.send_delay / 2)
            entry.update(status="sending", partsSent=1)
            await asyncio.sleep(self.send_delay / 2)
        else:
            await asyncio.sleep(self.send_delay)
        if number in self.fail_numbers:
            # The phone has not stored the message: no id, no conversation.
            entry.update(status="failed", errorCode=1)
            await self._emit("sms.failed", {
                "requestId": entry["requestId"], "id": None,
                "conversationId": None, "to": number,
                "errorCode": 1, "reason": "generic_failure",
            })
            return
        message = self.add_message(
            number, body, direction="out", sim=entry["sim"],
            request_id=entry["requestId"],
        )
        entry.update(status="sent", partsSent=entry["parts"],
                     messageId=message["id"])
        await self._emit("sms.sent", {
            "requestId": entry["requestId"],
            "conversationId": message["conversationId"],
            "to": number, "message": message,
        })
        if self.delivery_reports:
            await asyncio.sleep(self.send_delay)
            entry["status"] = "delivered"
            message["status"] = "delivered"
            await self._emit("sms.delivered", {
                "requestId": entry["requestId"], "id": message["id"],
                "conversationId": message["conversationId"], "to": number,
            })

    def _subscribe(self, client: _Client, request: JsonObject) -> JsonObject:
        # subscribe adds the names; its address replaces the filter, and
        # without address (or with null) the filter is removed.
        client.events.update(_names(request))
        client.address = _optional(request, "address", str)
        return _subscription(client)

    def _unsubscribe(self, client: _Client, request: JsonObject) -> JsonObject:
        client.events.difference_update(_names(request))
        if not client.events:
            client.address = None
        return _subscription(client)

    # ------------------------------------------------------------------
    # Data
    # ------------------------------------------------------------------

    async def _receive_sms(self, address: str, body: str, sim: int) -> JsonObject:
        message = self.add_message(address, body, read=False, sim=sim)
        await self._emit("sms.received", {"message": message})
        return message

    async def _emit(self, name: str, data: JsonObject) -> None:
        self._sequence += 1
        event = {"event": name, "seq": self._sequence, "data": data}
        for client in list(self._clients):
            if client.authenticated and _wants(client, name, data):
                await self._send(client, event)

    def _conversation_id(self, address: str) -> int:
        for known, identifier in self._conversation_ids.items():
            if same_peer(known, address):
                return identifier
        identifier = 170 + len(self._conversation_ids)
        self._conversation_ids[address] = identifier
        return identifier

    def _contact_name(self, address: str) -> str | None:
        for contact in self.contacts:
            for number in contact["numbers"]:
                if same_peer(number["number"], address):
                    return contact["name"]
        return None

    def _conversations(self) -> list[JsonObject]:
        grouped: dict[int, list[JsonObject]] = {}
        for message in self.messages:
            grouped.setdefault(message["conversationId"], []).append(message)
        items = []
        for identifier, messages in grouped.items():
            newest = max(messages, key=lambda m: (m["timestamp"], m["id"]))
            items.append({
                "id": identifier,
                "address": newest["address"],
                "contact": newest["contact"],
                "snippet": newest["body"][:160],
                "timestamp": newest["timestamp"],
                "direction": newest["direction"],
                "messageCount": len(messages),
                "unreadCount": sum(1 for m in messages if not m["read"]),
                "sim": newest["sim"],
            })
        items.sort(key=lambda c: (c["timestamp"], c["id"]), reverse=True)
        return items


_STATUS_ORDER = ["queued", "sending", "sent", "delivered"]


def _send_result(entry: JsonObject) -> JsonObject:
    return {"requestId": entry["requestId"], "to": entry["to"],
            "parts": entry["parts"], "status": entry["status"]}


def _group_result(request_id: str, members: list[JsonObject]) -> JsonObject:
    """Return the GroupSendResult of ``members`` as the app builds it."""

    alive = [m["status"] for m in members if m["status"] != "failed"]
    status = min(alive, key=_STATUS_ORDER.index) if alive else "failed"
    return {
        "requestId": request_id,
        "to": [m["to"] for m in members],
        "parts": members[0]["parts"],
        "status": status,
        "messages": [_send_result(m) for m in members],
    }


def _required(request: JsonObject, name: str, kind: type) -> Any:
    value = _optional(request, name, kind)
    if value is None:
        raise _Failure("invalid_request", f"The field '{name}' is missing.")
    return value


def _optional(request: JsonObject, name: str, kind: type) -> Any:
    value = request.get(name)
    if value is None:
        return None
    wrong_bool = kind is not bool and isinstance(value, bool)
    if wrong_bool or not isinstance(value, kind):
        raise _Failure("invalid_request", f"The field '{name}' is malformed.")
    return value


def _names(request: JsonObject) -> list[str]:
    """Return the event names of a request, with ``*`` replaced."""

    names = request.get("events")
    if (
        not isinstance(names, list)
        or not names
        or not all(isinstance(name, str) for name in names)
    ):
        raise _Failure("invalid_request", "'events' is missing.")
    if "*" in names:
        return list(EVENT_NAMES)
    for name in names:
        if name not in EVENT_NAMES:
            raise _Failure("invalid_request", f"Unknown event '{name}'.")
    return names


def _subscription(client: _Client) -> JsonObject:
    events = [name for name in EVENT_NAMES if name in client.events]
    return {"events": events, "address": client.address}


def _wants(client: _Client, name: str, data: JsonObject) -> bool:
    """Whether a client subscribed to an event and its filter lets it pass.

    The filter applies to ``sms.*`` events that name a peer. Events whose
    number is not known are always delivered.
    """

    if name not in client.events:
        return False
    if client.address is None or not name.startswith("sms."):
        return True
    message = data.get("message")
    peer = message.get("address") if isinstance(message, dict) else None
    peer = peer or data.get("to") or data.get("address")
    if not isinstance(peer, str):
        return True
    return same_peer(peer, client.address)


def _page(
    items: list[JsonObject], request: JsonObject, by_index: bool = False
) -> JsonObject:
    limit = request.get("limit", 50)
    if (
        isinstance(limit, bool)
        or not isinstance(limit, int)
        or not 1 <= limit <= 200
    ):
        raise _Failure("invalid_request", "'limit' must be between 1 and 200.")
    cursor = _optional(request, "cursor", str)
    start = 0
    if cursor is not None and by_index:
        if not cursor.isdigit():
            raise _Failure("invalid_request", "'cursor' is invalid.")
        start = int(cursor)
    elif cursor is not None:
        keys = [f"{item['timestamp']}:{item['id']}" for item in items]
        if cursor not in keys:
            raise _Failure("invalid_request", "'cursor' is invalid.")
        start = keys.index(cursor) + 1
    chunk = items[start:start + limit]
    next_cursor = None
    if chunk and start + limit < len(items):
        next_cursor = (
            str(start + len(chunk)) if by_index
            else f"{chunk[-1]['timestamp']}:{chunk[-1]['id']}"
        )
    return {"items": chunk, "nextCursor": next_cursor}


def main() -> int:
    """Run the fake server until Ctrl+C."""

    parser = argparse.ArgumentParser(
        description="Fake SMS Dock server for trying the client without a phone."
    )
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9443)
    parser.add_argument("--password", default="secret123",
                        help="password of the fake server (test value only)")
    parser.add_argument("--incoming-every", type=float, metavar="SECONDS",
                        help="let an SMS arrive every so many seconds")
    args = parser.parse_args()

    with tempfile.TemporaryDirectory() as directory:
        tls = make_certificate(Path(directory))
        if tls is None:
            print("The openssl command is needed to create a certificate.")
            return 1
        server = FakeSmsDock(
            password=args.password, tls=tls, host=args.host, port=args.port,
            send_delay=0.5, delivery_reports=True,
        )
        server.seed_demo_data()
        with server:
            print(f"URL:         {server.url}")
            print(f"Fingerprint: {tls.fingerprint}")
            print(f"Password:    {args.password}")
            print("Press Ctrl+C to stop.")
            try:
                number = 0
                while True:
                    time.sleep(args.incoming_every or 3600)
                    if args.incoming_every:
                        number += 1
                        server.receive_sms("+436641234567", f"Test message {number}")
            except KeyboardInterrupt:
                print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
