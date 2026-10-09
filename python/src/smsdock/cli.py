"""Command-line tool ``smsdock`` for the SMS Dock WebSocket API.

Exit codes: 0 success, 1 the server answered with an error, 2 wrong
usage, 3 connection or TLS problem.
"""

from __future__ import annotations

import argparse
import getpass
import json
import os
import sys
import time
from collections.abc import Iterable, Sequence
from datetime import datetime, timezone
from typing import Any
from urllib.parse import urlsplit

from .client import (
    DEFAULT_API_PORT,
    EVENT_NAMES,
    FINGERPRINT_ENV,
    PASSWORD_ENV,
    ApiError,
    SmsDockClient,
    SmsDockError,
    fetch_fingerprint,
)

EXIT_OK = 0
EXIT_API_ERROR = 1
EXIT_USAGE = 2
EXIT_CONNECTION = 3
EXIT_INTERRUPTED = 130

_TEXT_WIDTH = 60


class UsageError(Exception):
    """The command line cannot be carried out as given."""


def build_parser() -> argparse.ArgumentParser:
    """Return the argument parser of the ``smsdock`` command."""

    parser = argparse.ArgumentParser(
        prog="smsdock",
        description="Read, send and watch SMS through the SMS Dock app.",
        allow_abbrev=False,
        epilog=(
            f"The password is read from the environment variable "
            f"{PASSWORD_ENV}, never from the command line."
        ),
    )
    commands = parser.add_subparsers(dest="command", required=True)

    fingerprint = commands.add_parser(
        "fingerprint", allow_abbrev=False,
        help="print the certificate fingerprint of a server",
        description=(
            "Print the SHA-256 fingerprint of the certificate the server "
            "presents. Compare it with the Server screen of the app "
            "before you use it."
        ),
    )
    fingerprint.add_argument(
        "host", metavar="HOST[:PORT]",
        help=f"address of the phone; the default port is {DEFAULT_API_PORT}",
    )
    fingerprint.add_argument("--timeout", type=float, default=10.0,
                             metavar="SECONDS",
                             help="seconds to wait (default: 10)")

    connection = argparse.ArgumentParser(add_help=False)
    connection.add_argument(
        "url", help="wss:// URL shown by the app, e.g. wss://192.168.1.50:9443/ws"
    )
    connection.add_argument(
        "--fingerprint", metavar="HEX",
        default=os.environ.get(FINGERPRINT_ENV),
        help=(
            "SHA-256 fingerprint of the certificate as shown by the app "
            f"(default: environment variable {FINGERPRINT_ENV})"
        ),
    )
    connection.add_argument(
        "--insecure", action="store_true",
        help="do not check the certificate (for tests only)",
    )
    connection.add_argument(
        "--password-env", default=PASSWORD_ENV, metavar="NAME",
        help=f"environment variable with the password (default: {PASSWORD_ENV})",
    )
    connection.add_argument("--timeout", type=float, default=30.0,
                            metavar="SECONDS",
                            help="seconds to wait for a response (default: 30)")
    connection.add_argument(
        "--reconnect-attempts", type=int, default=5, metavar="N",
        help="reconnect attempts after a lost connection; -1 means "
             "without limit (default: 5)",
    )
    connection.add_argument("--json", action="store_true",
                            help="print JSON instead of text")

    commands.add_parser(
        "status", parents=[connection], allow_abbrev=False,
        help="show phone, SIMs, permissions and licence",
    )

    conversations = commands.add_parser(
        "conversations", parents=[connection], allow_abbrev=False,
        help="list conversations, newest first",
    )
    conversations.add_argument("--search", metavar="TEXT",
                               help="search text, numbers and contact names")
    conversations.add_argument("--unread", action="store_true",
                               help="only conversations with unread messages")
    conversations.add_argument("--sim", type=int, metavar="ID",
                               help="only this subscription id")
    conversations.add_argument("--limit", type=_positive, default=50,
                               metavar="N",
                               help="number of conversations (default: 50)")

    messages = commands.add_parser(
        "messages", parents=[connection], allow_abbrev=False,
        help="list messages, newest first",
    )
    messages.add_argument("--address", metavar="NUMBER",
                          help="only messages from or to this number")
    messages.add_argument("--conversation", type=int, metavar="ID",
                          help="only this conversation")
    messages.add_argument("--direction", choices=("in", "out", "all"),
                          default="all", help="incoming, outgoing or both")
    messages.add_argument("--search", metavar="TEXT", help="search the text")
    messages.add_argument("--since", type=_timestamp, metavar="TIME",
                          help="not older than this: 2026-09-29, "
                               "2026-09-29T08:00 or milliseconds")
    messages.add_argument("--until", type=_timestamp, metavar="TIME",
                          help="not newer than this")
    messages.add_argument("--unread", action="store_true",
                          help="only unread messages")
    messages.add_argument("--sim", type=int, metavar="ID",
                          help="only this subscription id")
    messages.add_argument("--limit", type=_positive, default=50, metavar="N",
                          help="number of messages (default: 50)")

    send = commands.add_parser(
        "send", parents=[connection], allow_abbrev=False,
        help="send an SMS",
        description="Send an SMS. Without --text the text is read from "
                    "standard input.",
    )
    send.add_argument("--to", required=True, metavar="NUMBER",
                      help="phone number of the recipient")
    send.add_argument("--text", help="text of the message")
    send.add_argument("--sim", type=int, metavar="ID",
                      help="subscription id of the SIM")
    send.add_argument("--request-id", metavar="ID",
                      help="your own id; the same id is sent only once")
    send.add_argument("--wait", action="store_true",
                      help="wait until the message was sent or failed")
    send.add_argument("--wait-timeout", type=float, default=60.0,
                      metavar="SECONDS",
                      help="longest time to wait with --wait (default: 60)")

    delete = commands.add_parser(
        "delete", parents=[connection], allow_abbrev=False,
        help="delete a message or a conversation",
    )
    target = delete.add_mutually_exclusive_group(required=True)
    target.add_argument("--message", type=int, metavar="ID",
                        help="id of the message")
    target.add_argument("--conversation", type=int, metavar="ID",
                        help="id of the conversation")

    mark_read = commands.add_parser(
        "mark-read", parents=[connection], allow_abbrev=False,
        help="mark a conversation as read",
        description="Mark all messages of a conversation as read. SMS Dock "
                    "must be the default SMS app of the phone.",
    )
    mark_read.add_argument("--conversation", type=int, metavar="ID",
                           required=True, help="id of the conversation")

    contacts = commands.add_parser(
        "contacts", parents=[connection], allow_abbrev=False,
        help="list the contacts of the phone",
    )
    contacts.add_argument("--search", metavar="TEXT",
                          help="search names and numbers")
    contacts.add_argument("--limit", type=_positive, default=50, metavar="N",
                          help="number of contacts (default: 50)")

    listen = commands.add_parser(
        "listen", parents=[connection], allow_abbrev=False,
        help="print incoming SMS as they arrive",
        description="Print events as they arrive until Ctrl+C.",
    )
    listen.add_argument("--address", metavar="NUMBER",
                        help="only SMS events of this number")
    listen.add_argument(
        "--events", nargs="+", default=["sms.received"], metavar="EVENT",
        choices=EVENT_NAMES + ("*",),
        help="events to print (default: sms.received); * means all",
    )
    listen.add_argument(
        "--count", type=_positive, metavar="N",
        help="stop after this many events",
    )
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """Run the command line and return the exit code."""

    parser = build_parser()
    try:
        args = parser.parse_args(argv)
    except SystemExit as stop:
        code = stop.code
        return code if isinstance(code, int) else EXIT_USAGE
    try:
        if args.command == "fingerprint":
            return _fingerprint(args)
        with _client(args) as client:
            return _COMMANDS[args.command](client, args)
    except KeyboardInterrupt:
        return EXIT_INTERRUPTED
    except BrokenPipeError:
        return EXIT_OK
    except ApiError as error:
        _complain(error)
        return EXIT_API_ERROR
    except (UsageError, ValueError) as error:
        _complain(error)
        return EXIT_USAGE
    except (SmsDockError, OSError) as error:
        _complain(error)
        return EXIT_CONNECTION


def _complain(error: BaseException) -> None:
    print(f"smsdock: {error}", file=sys.stderr)


def _client(args: argparse.Namespace) -> SmsDockClient:
    if not args.insecure and not args.fingerprint:
        raise UsageError(
            "No certificate fingerprint. Pass --fingerprint or set "
            f"{FINGERPRINT_ENV}. 'smsdock fingerprint HOST' prints the "
            "fingerprint of a server."
        )
    if args.reconnect_attempts < -1:
        raise UsageError("--reconnect-attempts cannot be less than -1")
    attempts = None if args.reconnect_attempts == -1 else args.reconnect_attempts
    return SmsDockClient(
        args.url,
        _password(args.password_env),
        fingerprint=args.fingerprint,
        insecure=args.insecure,
        timeout=args.timeout,
        reconnect_attempts=attempts,
    )


def _password(variable: str) -> str:
    password = os.environ.get(variable)
    if password:
        return password
    if sys.stdin.isatty():
        password = getpass.getpass("SMS Dock password: ")
        if password:
            return password
    raise UsageError(
        f"No password. Set the environment variable {variable}."
    )


def _fingerprint(args: argparse.Namespace) -> int:
    host, port = _host_and_port(args.host)
    print(fetch_fingerprint(host, port, timeout=args.timeout))
    print(
        "Compare this fingerprint with the Server screen of the app. "
        "Use it only if both are the same.",
        file=sys.stderr,
    )
    return EXIT_OK


def _status(client: SmsDockClient, args: argparse.Namespace) -> int:
    status = client.status()
    if args.json:
        _print_json(status)
        return EXIT_OK
    app = status.get("app", {})
    licence = status.get("licence", {})
    permissions = status.get("permissions", {})
    missing = sorted(name for name, granted in permissions.items() if not granted)
    if licence.get("licensed"):
        licence_text = "full licence"
    elif not licence.get("enforced"):
        licence_text = "not enforced"
    else:
        licence_text = (
            f"free version, {licence.get('sentToday')} of "
            f"{licence.get('dailyLimit')} SMS sent today"
        )
    lines = [
        ("App", (f"{app.get('name')} {app.get('version')} "
                 f"(protocol {app.get('protocolVersion')})")),
        ("Default SMS app", _yes_no(status.get("defaultSmsApp"))),
        ("Delete mode", str(status.get("deleteMode"))),
        ("Licence", licence_text),
        ("Permissions", "all granted" if not missing
         else "missing: " + ", ".join(missing)),
    ]
    for label, value in lines:
        print(f"{label + ':':<17}{value}")
    sims = status.get("sims") or []
    if sims:
        print()
        _print_table(
            ("SIM", "SLOT", "NAME", "NUMBER", "DEFAULT"),
            [
                (sim.get("subscriptionId"), sim.get("slot"), sim.get("name"),
                 sim.get("number"), _yes_no(sim.get("isDefault")))
                for sim in sims
            ],
        )
    return EXIT_OK


def _conversations(client: SmsDockClient, args: argparse.Namespace) -> int:
    items = list(client.iter_conversations(
        q=args.search, unread=args.unread, sim=args.sim,
        page_size=min(args.limit, 200), max_items=args.limit,
    ))
    if args.json:
        _print_json(items)
        return EXIT_OK
    _print_table(
        ("ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT"),
        [
            (item.get("id"), _time(item.get("timestamp")), item.get("address"),
             item.get("contact"), item.get("messageCount"),
             item.get("unreadCount"), item.get("direction"),
             _one_line(item.get("snippet")))
            for item in items
        ],
    )
    return EXIT_OK


def _messages(client: SmsDockClient, args: argparse.Namespace) -> int:
    items = list(client.iter_messages(
        address=args.address, conversation_id=args.conversation,
        direction=args.direction, q=args.search, since=args.since,
        until=args.until, sim=args.sim, unread=args.unread,
        page_size=min(args.limit, 200), max_items=args.limit,
    ))
    if args.json:
        _print_json(items)
        return EXIT_OK
    _print_table(
        ("ID", "TIME", "DIR", "NUMBER", "NAME", "STATUS", "TEXT"),
        [
            (item.get("id"), _time(item.get("timestamp")),
             item.get("direction"), item.get("address"), item.get("contact"),
             item.get("status"), _one_line(item.get("body")))
            for item in items
        ],
    )
    return EXIT_OK


def _send(client: SmsDockClient, args: argparse.Namespace) -> int:
    text = args.text
    if text is None:
        if sys.stdin.isatty():
            raise UsageError("No text. Pass --text or write it to standard input.")
        text = sys.stdin.read()
        text = text.removesuffix("\n")
    if not text:
        raise UsageError("The text is empty.")
    result: dict[str, Any] = dict(client.send(
        args.to, text, sim=args.sim, request_id=args.request_id
    ))
    if args.wait:
        result = dict(client.wait_for_result(
            result["requestId"], args.wait_timeout
        ))
    # Also without --wait: a repeated request id tells the old outcome.
    failed = result.get("status") == "failed"
    if args.json:
        _print_json(result)
    elif failed:
        code = result.get("errorCode")
        cause = "" if code is None else f"error code {code}, "
        print(
            f"smsdock: Sending to {result.get('to')} failed "
            f"({cause}requestId {result.get('requestId')})",
            file=sys.stderr,
        )
    else:
        print(
            f"{str(result.get('status')).capitalize()}: {result.get('to')}, "
            f"{result.get('parts')} part(s), requestId {result.get('requestId')}"
        )
    return EXIT_API_ERROR if failed else EXIT_OK


def _delete(client: SmsDockClient, args: argparse.Namespace) -> int:
    if args.message is not None:
        result = client.delete_message(args.message)
    else:
        result = client.delete_conversation(args.conversation)
    if args.json:
        _print_json(result)
        return EXIT_OK
    where = (
        "removed from the phone" if result.get("mode") == "provider"
        else "hidden in SMS Dock, still on the phone"
    )
    print(f"Deleted {result.get('deleted')} message(s): {where}")
    return EXIT_OK


def _mark_read(client: SmsDockClient, args: argparse.Namespace) -> int:
    result = client.mark_read(args.conversation)
    if args.json:
        _print_json(result)
    else:
        print(f"Marked conversation {args.conversation} as read")
    return EXIT_OK


def _contacts(client: SmsDockClient, args: argparse.Namespace) -> int:
    items = list(client.iter_contacts(
        q=args.search, page_size=min(args.limit, 200), max_items=args.limit
    ))
    if args.json:
        _print_json(items)
        return EXIT_OK
    rows: list[tuple[Any, ...]] = []
    for contact in items:
        for number in contact.get("numbers") or [{}]:
            rows.append(
                (contact.get("name"), number.get("number"), number.get("label"))
            )
    _print_table(("NAME", "NUMBER", "LABEL"), rows)
    return EXIT_OK


def _listen(client: SmsDockClient, args: argparse.Namespace) -> int:
    client.on_reconnect = _report_reconnect
    client.subscribe(args.events, address=args.address)
    if not args.json:
        print(
            f"Listening for {', '.join(args.events)}. Press Ctrl+C to stop.",
            file=sys.stderr,
        )
    for count, event in enumerate(client.events(), start=1):
        if args.json:
            print(json.dumps(event, ensure_ascii=False), flush=True)
        else:
            print(_describe(dict(event)), flush=True)
        if args.count is not None and count >= args.count:
            break
    return EXIT_OK


def _report_reconnect(client: SmsDockClient) -> None:
    """Tell on standard error what a lost connection may have cost."""

    if client.server_restarted:
        lost = "the app was restarted, events may be missing"
    elif client.missed_events:
        lost = f"{client.missed_events} event(s) of the phone were not received"
    else:
        lost = "no event was missed"
    print(f"smsdock: reconnected; {lost}", file=sys.stderr, flush=True)


_COMMANDS = {
    "status": _status,
    "conversations": _conversations,
    "messages": _messages,
    "send": _send,
    "delete": _delete,
    "mark-read": _mark_read,
    "contacts": _contacts,
    "listen": _listen,
}


def _describe(event: dict[str, Any]) -> str:
    """Return one event as a line of text."""

    name = event.get("event")
    data = event.get("data") or {}
    message = data.get("message") or {}
    if name == "sms.received":
        sender = message.get("address")
        if message.get("contact"):
            sender = f"{sender} ({message.get('contact')})"
        return f"{_time(message.get('timestamp'), True)}  {sender}: {message.get('body')}"
    now = _time(int(time.time() * 1000), True)
    if name == "sms.sent" and message:
        return f"{now}  sent to {message.get('address')}: {message.get('body')}"
    details = " ".join(
        f"{key}={value}" for key, value in data.items()
        if not isinstance(value, (dict, list))
    )
    return f"{now}  {name} {details}".rstrip()


def _host_and_port(text: str) -> tuple[str, int]:
    """Split ``HOST``, ``HOST:PORT``, ``[IPv6]:PORT`` or a ``wss://`` URL."""

    value = text.strip()
    if "://" not in value:
        if value.count(":") > 1 and not value.startswith("["):
            value = f"[{value}]"
        value = f"//{value}"
    try:
        parts = urlsplit(value)
        host, port = parts.hostname, parts.port
    except ValueError as error:
        raise UsageError(f"'{text}' is not HOST or HOST:PORT") from error
    if not host:
        raise UsageError(f"'{text}' is not HOST or HOST:PORT")
    return host, port or DEFAULT_API_PORT


def _positive(text: str) -> int:
    try:
        value = int(text)
    except ValueError:
        value = 0
    if value < 1:
        raise argparse.ArgumentTypeError("must be a number from 1")
    return value


def _timestamp(text: str) -> int:
    """Read milliseconds since the epoch or an ISO 8601 date (local time)."""

    if text.isdigit():
        return int(text)
    try:
        return int(datetime.fromisoformat(text).timestamp() * 1000)
    except ValueError:
        raise argparse.ArgumentTypeError(
            "must be a date such as 2026-09-29 or 2026-09-29T08:00, "
            "or milliseconds since the epoch"
        ) from None


def _time(milliseconds: Any, seconds: bool = False) -> str:
    if isinstance(milliseconds, bool) or not isinstance(milliseconds, (int, float)):
        return ""
    try:
        moment = datetime.fromtimestamp(
            milliseconds / 1000, tz=timezone.utc
        ).astimezone()
    except (OverflowError, OSError, ValueError):
        return str(milliseconds)
    return moment.strftime("%Y-%m-%d %H:%M:%S" if seconds else "%Y-%m-%d %H:%M")


def _one_line(text: Any) -> str:
    line = " ".join(str(text or "").split())
    if len(line) > _TEXT_WIDTH:
        line = line[:_TEXT_WIDTH - 1] + "…"
    return line


def _yes_no(value: Any) -> str:
    return "yes" if value else "no"


def _print_json(value: Any) -> None:
    print(json.dumps(value, indent=2, ensure_ascii=False))


def _print_table(
    headers: Sequence[str], rows: Iterable[Sequence[Any]]
) -> None:
    table = [[("" if cell is None else str(cell)) for cell in row] for row in rows]
    if not table:
        print("Nothing found.")
        return
    widths = [
        max(len(header), *(len(row[index]) for row in table))
        for index, header in enumerate(headers)
    ]
    for row in [list(headers)] + table:
        cells = [cell.ljust(width) for cell, width in zip(row, widths)]
        print("  ".join(cells).rstrip())
