"""Tests of the command line ``smsdock``."""

from __future__ import annotations

import io
import json
import threading
from typing import Any, Callable

import pytest
from conftest import ANNA, BEN, PASSWORD, plain_connect, wait_until
from fake_server import FakeSmsDock, TlsFiles

import smsdock.client
from smsdock import cli

Arguments = Callable[..., list[str]]
URL = "wss://192.168.1.50:9443/ws"
FINGERPRINT = ":".join(["AB"] * 32)


@pytest.fixture(autouse=True)
def environment(
    clean_environment: None, monkeypatch: pytest.MonkeyPatch
) -> None:
    """Every test starts with the password set and no fingerprint."""

    monkeypatch.setenv("SMSDOCK_PASSWORD", PASSWORD)


@pytest.fixture
def arguments(server: FakeSmsDock, monkeypatch: pytest.MonkeyPatch) -> Arguments:
    """Return a function that adds URL and fingerprint to a command."""

    url = server.url.replace("ws://", "wss://", 1)
    if server.tls is not None:
        connection = [url, "--fingerprint", server.tls.fingerprint]
    else:
        monkeypatch.setattr(smsdock.client, "connect", plain_connect)
        connection = [url, "--insecure"]

    def build(command: str, *options: str) -> list[str]:
        return [command, *connection, "--timeout", "5", *options]

    return build


WARNING = "smsdock: WARNING:"


def run(capsys: pytest.CaptureFixture[str], argv: list[str]) -> Any:
    """Run the command line; return exit code, output and errors.

    The warning of ``--insecure`` is left out; without openssl every
    test uses that flag.
    """

    code = cli.main(argv)
    captured = capsys.readouterr()
    errors = [
        line for line in captured.err.splitlines(keepends=True)
        if not line.startswith(WARNING)
    ]
    return code, captured.out, "".join(errors)


# ----------------------------------------------------------------------
# Arguments
# ----------------------------------------------------------------------


def test_parser_reads_all_flags() -> None:
    parser = cli.build_parser()
    args = parser.parse_args([
        "messages", URL, "--fingerprint", FINGERPRINT, "--address", ANNA,
        "--direction", "in", "--search", "invoice", "--limit", "10",
        "--unread", "--sim", "2", "--json", "--timeout", "7",
        "--since", "1790672531000", "--until", "2026-09-29T08:00",
        "--conversation", "179", "--reconnect-attempts", "2",
    ])
    assert args.command == "messages"
    assert args.url == URL
    assert args.fingerprint == FINGERPRINT
    assert args.address == ANNA
    assert args.direction == "in"
    assert args.search == "invoice"
    assert args.limit == 10
    assert args.unread is True
    assert args.sim == 2
    assert args.json is True
    assert args.timeout == 7.0
    assert args.since == 1790672531000
    assert args.until > 1_700_000_000_000
    assert args.conversation == 179
    assert args.reconnect_attempts == 2
    assert args.insecure is False


def test_parser_defaults() -> None:
    args = cli.build_parser().parse_args(["messages", URL])
    assert args.direction == "all"
    assert args.limit == 50
    assert args.timeout == 30.0
    assert args.reconnect_attempts == 5
    assert not args.json and not args.unread and not args.insecure
    assert args.fingerprint is None
    assert args.password_env == "SMSDOCK_PASSWORD"
    listen = cli.build_parser().parse_args(["listen", URL])
    assert listen.events == ["sms.received"]
    assert listen.address is None
    send = cli.build_parser().parse_args(["send", URL, "--to", ANNA])
    assert send.text is None and send.wait is False


def test_fingerprint_from_the_environment(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("SMSDOCK_FINGERPRINT", FINGERPRINT)
    args = cli.build_parser().parse_args(["status", URL])
    assert args.fingerprint == FINGERPRINT


@pytest.mark.parametrize("argv", [
    [],
    ["unknown"],
    ["status"],
    ["status", URL, "--password", "secret123"],
    ["messages", URL, "--direction", "sideways"],
    ["messages", URL, "--limit", "0"],
    ["messages", URL, "--limit", "many"],
    ["messages", URL, "--since", "yesterday"],
    ["send", URL, "--text", "no recipient"],
    ["delete", URL],
    ["delete", URL, "--message", "1", "--conversation", "2"],
    ["mark-read", URL],
    ["mark-read", URL, "--conversation", "first"],
    ["listen", URL, "--events", "sms.unknown"],
    ["fingerprint"],
])
def test_usage_errors(
    capsys: pytest.CaptureFixture[str], argv: list[str]
) -> None:
    code, out, err = run(capsys, argv)
    assert code == 2
    assert out == ""
    assert "usage:" in err


def test_help(capsys: pytest.CaptureFixture[str]) -> None:
    code, out, _ = run(capsys, ["--help"])
    assert code == 0
    for command in ("fingerprint", "status", "conversations", "messages",
                    "send", "delete", "mark-read", "contacts", "listen"):
        assert command in out
    assert "SMSDOCK_PASSWORD" in out
    code, out, _ = run(capsys, ["send", "--help"])
    assert code == 0
    assert "--password " not in out


def test_the_password_is_no_argument(
    capsys: pytest.CaptureFixture[str],
) -> None:
    code, _, err = run(capsys, [
        "status", URL, "--fingerprint", FINGERPRINT, "--password", PASSWORD,
    ])
    assert code == 2
    assert "unrecognized arguments" in err


@pytest.mark.parametrize("url", [
    "ws://192.168.1.50:9443/ws", "https://192.168.1.50:8443/", "192.168.1.50",
])
def test_refuses_other_urls(
    capsys: pytest.CaptureFixture[str], url: str
) -> None:
    code, out, err = run(capsys, ["status", url, "--fingerprint", FINGERPRINT])
    assert code == 2
    assert "wss://" in err
    assert out == ""


def test_needs_a_fingerprint(capsys: pytest.CaptureFixture[str]) -> None:
    code, _, err = run(capsys, ["status", URL])
    assert code == 2
    assert "--fingerprint" in err and "SMSDOCK_FINGERPRINT" in err


def test_refuses_a_malformed_fingerprint(
    capsys: pytest.CaptureFixture[str],
) -> None:
    code, _, err = run(capsys, ["status", URL, "--fingerprint", "AB:CD"])
    assert code == 2
    assert "SHA-256" in err


def test_needs_a_password(
    capsys: pytest.CaptureFixture[str], monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.delenv("SMSDOCK_PASSWORD")
    monkeypatch.setattr("sys.stdin", io.StringIO(""))
    code, _, err = run(capsys, ["status", URL, "--fingerprint", FINGERPRINT])
    assert code == 2
    assert "SMSDOCK_PASSWORD" in err


def test_asks_for_the_password_on_a_terminal(
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
    arguments: Arguments,
) -> None:
    class Terminal(io.StringIO):
        def isatty(self) -> bool:
            return True

    monkeypatch.delenv("SMSDOCK_PASSWORD")
    monkeypatch.setattr("sys.stdin", Terminal(""))
    monkeypatch.setattr(cli.getpass, "getpass", lambda prompt: PASSWORD)
    code, out, _ = run(capsys, arguments("status", "--json"))
    assert code == 0
    assert json.loads(out)["deleteMode"] == "hidden"


def test_other_password_variable(
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
    arguments: Arguments,
) -> None:
    monkeypatch.setenv("SMSDOCK_PASSWORD", "wrong-password")
    monkeypatch.setenv("OFFICE_PHONE", PASSWORD)
    code, _, _ = run(capsys, arguments("status", "--password-env", "OFFICE_PHONE"))
    assert code == 0


@pytest.mark.parametrize("text, expected", [
    ("192.168.1.50", ("192.168.1.50", 9443)),
    ("192.168.1.50:9555", ("192.168.1.50", 9555)),
    ("phone.local:9443", ("phone.local", 9443)),
    ("wss://192.168.1.50:9555/ws", ("192.168.1.50", 9555)),
    ("wss://192.168.1.50/ws", ("192.168.1.50", 9443)),
    ("[fe80::1]:9555", ("fe80::1", 9555)),
    ("fe80::1", ("fe80::1", 9443)),
])
def test_host_and_port(text: str, expected: Any) -> None:
    assert cli._host_and_port(text) == expected


@pytest.mark.parametrize("text", ["", ":9443", "192.168.1.50:port"])
def test_host_and_port_errors(text: str) -> None:
    with pytest.raises(cli.UsageError):
        cli._host_and_port(text)


# ----------------------------------------------------------------------
# Commands
# ----------------------------------------------------------------------


def test_fingerprint(
    capsys: pytest.CaptureFixture[str], need_tls: TlsFiles, server: FakeSmsDock
) -> None:
    code, out, err = run(capsys, ["fingerprint", f"127.0.0.1:{server.port}"])
    assert code == 0
    assert out == need_tls.fingerprint + "\n"
    assert "Compare" in err
    code, out, _ = run(capsys, ["fingerprint", server.url])
    assert (code, out) == (0, need_tls.fingerprint + "\n")
    assert server.requests == []


def test_fingerprint_of_nothing(
    capsys: pytest.CaptureFixture[str], server_factory: Callable[..., FakeSmsDock]
) -> None:
    server = server_factory()
    server.stop()
    code, out, err = run(capsys, [
        "fingerprint", f"127.0.0.1:{server.port}", "--timeout", "2",
    ])
    assert code == 3
    assert out == ""
    assert err.startswith("smsdock: ")


def test_status(capsys: pytest.CaptureFixture[str], arguments: Arguments) -> None:
    code, out, err = run(capsys, arguments("status"))
    assert code == 0
    assert "App:             SMS Dock 1.0.0 (protocol 1)" in out
    assert "Delete mode:     hidden" in out
    assert "Default SMS app: no" in out
    assert "Licence:         not enforced" in out
    assert "Permissions:     all granted" in out
    lines = out.splitlines()
    assert lines[-3].split() == ["SIM", "SLOT", "NAME", "NUMBER", "DEFAULT"]
    assert lines[-2].split() == ["1", "0", "Play", "yes"]
    assert lines[-1].split() == ["2", "1", "Work", BEN, "no"]
    assert PASSWORD not in out + err


def test_status_json(
    capsys: pytest.CaptureFixture[str], arguments: Arguments
) -> None:
    code, out, _ = run(capsys, arguments("status", "--json"))
    assert code == 0
    status = json.loads(out)
    assert status["app"]["protocolVersion"] == 1
    assert len(status["sims"]) == 2


def test_conversations(
    capsys: pytest.CaptureFixture[str], arguments: Arguments
) -> None:
    code, out, _ = run(capsys, arguments("conversations"))
    assert code == 0
    lines = out.splitlines()
    assert lines[0].split() == [
        "ID", "TIME", "NUMBER", "NAME", "MSGS", "UNREAD", "DIR", "TEXT",
    ]
    assert len(lines) == 4
    assert ANNA in lines[1] and "Anna Example" in lines[1]
    assert lines[1].endswith("Anna 24")
    assert "A1" in lines[3]

    code, out, _ = run(capsys, arguments(
        "conversations", "--json", "--unread", "--search", "bill", "--limit", "5",
    ))
    assert code == 0
    assert [c["address"] for c in json.loads(out)] == ["A1"]
    code, out, _ = run(capsys, arguments("conversations", "--sim", "2", "--json"))
    assert [c["address"] for c in json.loads(out)] == [BEN]


def test_last_ten_incoming_messages(
    capsys: pytest.CaptureFixture[str], arguments: Arguments
) -> None:
    code, out, _ = run(capsys, arguments(
        "messages", "--direction", "in", "--limit", "10",
    ))
    assert code == 0
    lines = out.splitlines()
    assert lines[0].split() == [
        "ID", "TIME", "DIR", "NUMBER", "NAME", "STATUS", "TEXT",
    ]
    assert len(lines) == 11
    assert all(" in " in line for line in lines[1:])
    assert lines[1].endswith("received  Anna 24")
    assert lines[10].endswith("received  Anna 6")


def test_messages_json_and_filters(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    code, out, _ = run(capsys, arguments(
        "messages", "--json", "--address", BEN, "--search", "invoice",
        "--direction", "out", "--sim", "2",
    ))
    assert code == 0
    messages = json.loads(out)
    assert [m["body"] for m in messages] == ["Your invoice is attached"]
    request = [r for r in server.requests if r["action"] == "messages.list"][-1]
    assert request["address"] == BEN
    assert request["q"] == "invoice"
    assert request["direction"] == "out"
    assert request["sim"] == 2

    code, out, _ = run(capsys, arguments("messages", "--json", "--unread"))
    assert len(json.loads(out)) == 4
    code, out, _ = run(capsys, arguments(
        "messages", "--json", "--since", "1790672553000",
    ))
    assert [m["body"] for m in json.loads(out)] == ["Anna 24", "Anna 23", "Anna 22"]


def test_messages_limit_over_one_page(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    for index in range(230):
        server.add_message(ANNA, f"More {index}")
    code, out, _ = run(capsys, arguments("messages", "--json", "--limit", "220"))
    assert code == 0
    assert len(json.loads(out)) == 220
    limits = [r["limit"] for r in server.requests if r["action"] == "messages.list"]
    assert limits == [200, 20]


def test_nothing_found(
    capsys: pytest.CaptureFixture[str], arguments: Arguments
) -> None:
    code, out, _ = run(capsys, arguments("messages", "--search", "no such text"))
    assert (code, out) == (0, "Nothing found.\n")
    code, out, _ = run(capsys, arguments(
        "messages", "--search", "no such text", "--json",
    ))
    assert (code, json.loads(out)) == (0, [])


def test_long_text_is_cut_in_tables(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    server.add_message(ANNA, "First line\nsecond line " + "x" * 100)
    code, out, _ = run(capsys, arguments("messages", "--limit", "1"))
    assert code == 0
    line = out.splitlines()[1]
    assert "First line second line xxx" in line
    assert line.endswith("…")
    code, out, _ = run(capsys, arguments("messages", "--limit", "1", "--json"))
    assert json.loads(out)[0]["body"].startswith("First line\nsecond line")


def test_send(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    code, out, _ = run(capsys, arguments(
        "send", "--to", BEN, "--text", "All systems running.",
    ))
    assert code == 0
    request_id = server.sent[0]["requestId"]
    assert out == f"Queued: {BEN}, 1 part(s), requestId {request_id}\n"
    assert server.sent[0]["body"] == "All systems running."


def test_send_from_standard_input(
    capsys: pytest.CaptureFixture[str],
    server: FakeSmsDock,
    arguments: Arguments,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr("sys.stdin", io.StringIO("Line one\nLine two\n"))
    code, out, _ = run(capsys, arguments(
        "send", "--to", BEN, "--sim", "2", "--request-id", "cron-1", "--json",
    ))
    assert code == 0
    assert json.loads(out) == {
        "requestId": "cron-1", "to": BEN, "parts": 1, "status": "queued",
    }
    assert server.sent == [{
        "requestId": "cron-1", "to": BEN, "body": "Line one\nLine two", "sim": 2,
    }]


def test_send_needs_a_text(
    capsys: pytest.CaptureFixture[str],
    server: FakeSmsDock,
    arguments: Arguments,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr("sys.stdin", io.StringIO(""))
    code, _, err = run(capsys, arguments("send", "--to", BEN))
    assert code == 2
    assert "empty" in err
    assert server.sent == []


def test_send_and_wait(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    code, out, _ = run(capsys, arguments(
        "send", "--to", BEN, "--text", "Hello", "--wait",
    ))
    assert code == 0
    assert out.startswith(f"Sent: {BEN}, 1 part(s), requestId ")

    code, out, _ = run(capsys, arguments(
        "send", "--to", BEN, "--text", "Hello", "--wait", "--json",
    ))
    assert code == 0
    entry = json.loads(out)
    assert entry["status"] == "sent" and entry["partsSent"] == 1


def test_send_and_wait_failed(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    server.fail_numbers.add("+43123456")
    code, out, err = run(capsys, arguments(
        "send", "--to", "+43123456", "--text", "Hello", "--wait",
    ))
    assert code == 1
    assert out == ""
    assert "failed" in err and "error code 1" in err

    code, out, _ = run(capsys, arguments(
        "send", "--to", "+43123456", "--text", "Hello", "--wait", "--json",
    ))
    assert code == 1
    assert json.loads(out)["status"] == "failed"


def test_send_again_tells_the_old_outcome(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    server.fail_numbers.add("+43123456")
    command = arguments(
        "send", "--to", "+43123456", "--text", "Hello", "--request-id", "job-1",
    )
    code, out, _ = run(capsys, command)
    assert (code, out) == (0, "Queued: +43123456, 1 part(s), requestId job-1\n")
    assert wait_until(lambda: server.outbox["job-1"]["status"] == "failed")
    server.fail_numbers.clear()
    # The repeat sends nothing and reports the failure of the first attempt.
    code, out, err = run(capsys, command)
    assert (code, out) == (1, "")
    assert err == "smsdock: Sending to +43123456 failed (requestId job-1)\n"
    assert len(server.sent) == 1

    ok = arguments(
        "send", "--to", BEN, "--text", "Hello", "--request-id", "job-2", "--wait",
    )
    assert run(capsys, ok)[0] == 0
    code, out, _ = run(capsys, ok[:-1])
    assert (code, out) == (0, f"Sent: {BEN}, 1 part(s), requestId job-2\n")
    assert len(server.sent) == 2


def test_send_and_wait_times_out(
    capsys: pytest.CaptureFixture[str],
    server_factory: Callable[..., FakeSmsDock],
    need_tls: TlsFiles,
) -> None:
    server = server_factory(send_delay=30)
    code, _, err = run(capsys, [
        "send", server.url, "--fingerprint", need_tls.fingerprint,
        "--to", BEN, "--text", "Hello", "--wait", "--wait-timeout", "0.3",
    ])
    assert code == 3
    assert "No result" in err


def test_delete(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    newest = max(server.messages, key=lambda m: m["timestamp"])
    code, out, _ = run(capsys, arguments("delete", "--message", str(newest["id"])))
    assert code == 0
    assert out == "Deleted 1 message(s): hidden in SMS Dock, still on the phone\n"
    assert newest not in server.messages

    server.delete_mode = "provider"
    conversation = server.messages[0]["conversationId"]
    code, out, _ = run(capsys, arguments(
        "delete", "--conversation", str(conversation),
    ))
    assert code == 0
    assert out == "Deleted 24 message(s): removed from the phone\n"
    code, out, _ = run(capsys, arguments(
        "delete", "--conversation", str(server.messages[0]["conversationId"]),
        "--json",
    ))
    assert json.loads(out) == {"deleted": 3, "mode": "provider"}


def test_mark_read(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    conversation = str(server.messages[0]["conversationId"])
    code, out, err = run(capsys, arguments(
        "mark-read", "--conversation", conversation,
    ))
    assert (code, out) == (1, "")
    assert err.startswith("smsdock: not_default_app: ")

    server.delete_mode = "provider"
    code, out, _ = run(capsys, arguments(
        "mark-read", "--conversation", conversation,
    ))
    assert (code, out) == (0, f"Marked conversation {conversation} as read\n")
    assert all(m["read"] for m in server.messages
               if str(m["conversationId"]) == conversation)
    code, out, _ = run(capsys, arguments(
        "mark-read", "--conversation", conversation, "--json",
    ))
    assert (code, json.loads(out)) == (0, {"ok": True})


def test_contacts(
    capsys: pytest.CaptureFixture[str], arguments: Arguments
) -> None:
    code, out, _ = run(capsys, arguments("contacts"))
    assert code == 0
    lines = out.splitlines()
    assert lines[0].split() == ["NAME", "NUMBER", "LABEL"]
    assert lines[1].split() == ["Anna", "Example", ANNA, "Mobile"]
    assert lines[2].split() == ["Ben", "Example", BEN, "Work"]
    code, out, _ = run(capsys, arguments("contacts", "--search", "ben", "--json"))
    assert json.loads(out) == [
        {"name": "Ben Example", "numbers": [{"number": BEN, "label": "Work"}]},
    ]


def incoming_later(server: FakeSmsDock, *messages: Any) -> threading.Thread:
    """Let messages arrive as soon as a client has subscribed."""

    def arrive() -> None:
        subscribed = lambda: any(
            r.get("action") == "subscribe" for r in server.requests
        )
        if wait_until(subscribed, 5):
            for address, body in messages:
                server.receive_sms(address, body)

    thread = threading.Thread(target=arrive)
    thread.start()
    return thread


def test_listen(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    thread = incoming_later(server, (ANNA, "STATUS"), ("A1", "Your bill"))
    try:
        code, out, err = run(capsys, arguments("listen", "--count", "2"))
    finally:
        thread.join()
    assert code == 0
    lines = out.splitlines()
    assert len(lines) == 2
    assert lines[0].endswith(f"  {ANNA} (Anna Example): STATUS")
    assert lines[1].endswith("  A1: Your bill")
    assert "Listening for sms.received" in err
    request = next(r for r in server.requests if r["action"] == "subscribe")
    assert request["events"] == ["sms.received"]
    assert "address" not in request


def test_listen_json_with_address(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    thread = incoming_later(server, (ANNA, "ignored"), (BEN, "wanted"))
    try:
        code, out, err = run(capsys, arguments(
            "listen", "--json", "--address", BEN, "--count", "1",
        ))
    finally:
        thread.join()
    assert code == 0
    assert err == ""
    event = json.loads(out)
    assert event["event"] == "sms.received"
    assert event["data"]["message"]["body"] == "wanted"
    assert len(out.splitlines()) == 1


def test_listen_to_other_events(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    def later() -> None:
        if wait_until(lambda: len(server.requests) >= 2, 5):
            server.emit("sms.delivered", {
                "requestId": "r1", "id": 7, "conversationId": 171, "to": BEN,
            })

    thread = threading.Thread(target=later)
    thread.start()
    try:
        code, out, _ = run(capsys, arguments(
            "listen", "--events", "sms.delivered", "sms.failed", "--count", "1",
        ))
    finally:
        thread.join()
    assert code == 0
    assert out.rstrip().endswith(
        f"sms.delivered requestId=r1 id=7 conversationId=171 to={BEN}"
    )


def test_listen_reports_a_reconnect(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    def later() -> None:
        if not wait_until(lambda: len(server.requests) >= 2, 5):
            return
        server.drop_connections()
        wait_until(lambda: server.client_count == 0)
        server.receive_sms(ANNA, "lost")
        server.receive_sms(ANNA, "lost too")
        if wait_until(lambda: len(server.requests) >= 4, 5):
            server.restart()
        if wait_until(lambda: len(server.requests) >= 6, 5):
            server.receive_sms(ANNA, "received")

    thread = threading.Thread(target=later)
    thread.start()
    try:
        code, out, err = run(capsys, arguments("listen", "--count", "1"))
    finally:
        thread.join()
    assert code == 0
    assert out.rstrip().endswith(": received")
    lines = err.splitlines()
    assert lines[1] == (
        "smsdock: reconnected; 2 event(s) of the phone were not received"
    )
    assert lines[2] == (
        "smsdock: reconnected; the app was restarted, events may be missing"
    )


def test_listen_stops_with_ctrl_c(
    capsys: pytest.CaptureFixture[str],
    arguments: Arguments,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def interrupted(*_: Any, **__: Any) -> Any:
        raise KeyboardInterrupt

    monkeypatch.setattr(cli.SmsDockClient, "events", interrupted)
    code, _, _ = run(capsys, arguments("listen"))
    assert code == 130


# ----------------------------------------------------------------------
# Exit codes
# ----------------------------------------------------------------------


def test_exit_code_1_for_api_errors(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    code, out, err = run(capsys, arguments("delete", "--message", "1"))
    assert (code, out) == (1, "")
    assert err.startswith("smsdock: not_found: ")

    code, _, err = run(capsys, arguments("send", "--to", "abc", "--text", "x"))
    assert code == 1
    assert "invalid_number" in err

    server.fail_next("messages.send", "quota_exceeded", "Daily limit reached.")
    code, _, err = run(capsys, arguments("send", "--to", BEN, "--text", "x"))
    assert code == 1
    assert err == "smsdock: quota_exceeded: Daily limit reached.\n"


def test_exit_code_1_for_a_wrong_password(
    capsys: pytest.CaptureFixture[str],
    server: FakeSmsDock,
    arguments: Arguments,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("SMSDOCK_PASSWORD", "wrong-password")
    code, out, err = run(capsys, arguments("status"))
    assert (code, out) == (1, "")
    assert "invalid_password" in err
    assert "wrong-password" not in err
    assert server.login_count == 1


def test_exit_code_1_without_licence(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    server.licence_enforced = True
    code, _, err = run(capsys, arguments("status"))
    assert code == 1
    assert "licence_required" in err


def test_exit_code_3_when_nothing_listens(
    capsys: pytest.CaptureFixture[str],
    server_factory: Callable[..., FakeSmsDock],
) -> None:
    server = server_factory()
    server.stop()
    code, out, err = run(capsys, [
        "status", f"wss://127.0.0.1:{server.port}/ws",
        "--fingerprint", FINGERPRINT, "--reconnect-attempts", "0",
    ])
    assert (code, out) == (3, "")
    assert "Cannot connect" in err


def test_exit_code_3_for_another_certificate(
    capsys: pytest.CaptureFixture[str],
    need_tls: TlsFiles,
    other_tls_files: TlsFiles,
    server: FakeSmsDock,
) -> None:
    code, out, err = run(capsys, [
        "status", server.url, "--fingerprint", other_tls_files.fingerprint,
    ])
    assert (code, out) == (3, "")
    assert need_tls.fingerprint in err
    assert other_tls_files.fingerprint in err
    assert server.requests == []


def test_fingerprint_from_the_environment_is_used(
    capsys: pytest.CaptureFixture[str],
    need_tls: TlsFiles,
    server: FakeSmsDock,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("SMSDOCK_FINGERPRINT", need_tls.fingerprint.lower())
    code, _, _ = run(capsys, ["status", server.url])
    assert code == 0


def test_insecure_warns(
    capsys: pytest.CaptureFixture[str], server: FakeSmsDock, arguments: Arguments
) -> None:
    url = arguments("status")[1]
    code = cli.main(["status", url, "--insecure", "--timeout", "5"])
    captured = capsys.readouterr()
    assert code == 0
    assert captured.err.startswith(WARNING)
    assert "not checked" in captured.err
    assert "App:" in captured.out


def test_module_can_be_run() -> None:
    import subprocess
    import sys

    result = subprocess.run(
        [sys.executable, "-m", "smsdock", "--help"],
        capture_output=True, text=True, timeout=60, check=False,
    )
    assert result.returncode == 0
    assert "usage: smsdock" in result.stdout
