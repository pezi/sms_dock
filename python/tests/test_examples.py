"""The examples run on their own against the fake server."""

from __future__ import annotations

import importlib.util
import os
import subprocess
import sys
import threading
from pathlib import Path
from typing import Any

import pytest
from conftest import ANNA, BEN, PASSWORD, wait_until
from fake_server import FakeSmsDock, TlsFiles

EXAMPLES = Path(__file__).resolve().parent.parent / "examples"


def environment(tls: TlsFiles) -> dict[str, str]:
    """Return the environment of an example process."""

    return {
        **os.environ,
        "SMSDOCK_PASSWORD": PASSWORD,
        "SMSDOCK_FINGERPRINT": tls.fingerprint,
        "PYTHONUNBUFFERED": "1",
    }


def run_example(name: str, tls: TlsFiles, *arguments: str) -> Any:
    """Run an example to its end."""

    return subprocess.run(
        [sys.executable, str(EXAMPLES / name), *arguments],
        env=environment(tls), capture_output=True, text=True, timeout=60,
        check=False,
    )


def start_example(name: str, tls: TlsFiles, url: str) -> Any:
    """Start an example that runs until it is stopped."""

    return subprocess.Popen(
        [sys.executable, str(EXAMPLES / name), url],
        env=environment(tls), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True,
    )


def read_lines(process: Any, count: int, timeout: float = 20.0) -> list[str]:
    """Read lines of the output, but do not wait for ever."""

    lines: list[str] = []

    def read() -> None:
        for line in process.stdout:
            lines.append(line.rstrip("\n"))
            if len(lines) >= count:
                return

    thread = threading.Thread(target=read, daemon=True)
    thread.start()
    thread.join(timeout)
    return lines


def subscribed(server: FakeSmsDock) -> bool:
    return any(r.get("action") == "subscribe" for r in server.requests)


def load(name: str) -> Any:
    """Import an example as a module."""

    spec = importlib.util.spec_from_file_location(name, EXAMPLES / f"{name}.py")
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_list_messages(need_tls: TlsFiles, server: FakeSmsDock) -> None:
    result = run_example("list_messages.py", need_tls, server.url)
    assert result.returncode == 0, result.stderr
    lines = result.stdout.splitlines()
    assert len(lines) == 10
    assert lines[0].endswith("Anna Example: Anna 24")
    assert lines[9].endswith("Anna Example: Anna 6")


def test_send_sms(need_tls: TlsFiles, server: FakeSmsDock) -> None:
    result = run_example("send_sms.py", need_tls, server.url, BEN, "Hello")
    assert result.returncode == 0, result.stderr
    assert result.stdout.splitlines()[0] == f"1 part(s) for {BEN}"
    assert result.stdout.splitlines()[1].startswith("Sent, message id ")
    assert [(m["to"], m["body"]) for m in server.sent] == [(BEN, "Hello")]


def test_send_sms_refused(need_tls: TlsFiles, server: FakeSmsDock) -> None:
    result = run_example("send_sms.py", need_tls, server.url, "abc", "Hello")
    assert result.returncode == 1
    assert result.stdout.startswith("Not sent: invalid_number")


def test_examples_report_a_missing_fingerprint(
    need_tls: TlsFiles, server: FakeSmsDock
) -> None:
    env = environment(need_tls)
    del env["SMSDOCK_FINGERPRINT"]
    result = subprocess.run(
        [sys.executable, str(EXAMPLES / "list_messages.py"), server.url],
        env=env, capture_output=True, text=True, timeout=60, check=False,
    )
    assert result.returncode == 1
    assert "SMSDOCK_FINGERPRINT" in result.stderr
    assert "Traceback" not in result.stderr


@pytest.mark.parametrize("name", [
    "list_messages.py", "send_sms.py", "listen_incoming.py", "auto_reply.py",
])
def test_examples_explain_their_usage(name: str, need_tls: TlsFiles) -> None:
    result = run_example(name, need_tls)
    assert result.returncode == 2
    assert "Usage:" in result.stdout


def test_listen_incoming(need_tls: TlsFiles, server: FakeSmsDock) -> None:
    process = start_example("listen_incoming.py", need_tls, server.url)
    try:
        assert wait_until(lambda: subscribed(server), 20)
        server.receive_sms(ANNA, "STATUS")
        server.receive_sms("A1", "Your bill")
        lines = read_lines(process, 3)
    finally:
        process.kill()
        process.wait()
    assert lines[0].startswith("Waiting for SMS")
    assert lines[1].endswith("Anna Example: STATUS")
    assert lines[2].endswith("A1: Your bill")


def test_listen_incoming_reads_what_it_missed(
    need_tls: TlsFiles, server: FakeSmsDock
) -> None:
    process = start_example("listen_incoming.py", need_tls, server.url)
    try:
        assert wait_until(lambda: subscribed(server), 20)
        server.receive_sms(ANNA, "first")
        assert len(read_lines(process, 2)) == 2
        server.drop_connections()
        assert wait_until(lambda: server.client_count == 0)
        server.receive_sms(BEN, "while away")
        server.restart()
        server.receive_sms(ANNA, "after the restart")
        lines = read_lines(process, 2)
        assert wait_until(lambda: server.client_count == 1, 20)
        server.receive_sms(ANNA, "live again")
        lines += read_lines(process, 1)
    finally:
        process.kill()
        process.wait()
    # Old messages of the phone are not shown, new ones only once.
    assert [line.split(": ", 1)[1] for line in lines] == [
        "while away", "after the restart", "live again",
    ]


def test_auto_reply(need_tls: TlsFiles, server: FakeSmsDock) -> None:
    process = start_example("auto_reply.py", need_tls, server.url)
    try:
        assert wait_until(lambda: subscribed(server), 20)
        server.receive_sms(ANNA, "status")
        server.receive_sms(ANNA, "STATUS")
        server.receive_sms("A1", "Your bill")
        server.receive_sms("8810", "Your code is 1234")
        server.receive_sms(BEN, "Hello?", sim=2)
        lines = read_lines(process, 6)
    finally:
        process.kill()
        process.wait()
    assert lines[1].startswith(f"{ANNA}: answered, requestId ")
    assert lines[2] == f"{ANNA}: no answer (answered a short time ago)"
    assert lines[3] == "A1: no answer (not a phone number)"
    assert lines[4] == "8810: no answer (not a phone number)"
    assert lines[5].startswith(f"{BEN}: answered, requestId ")
    assert [(m["to"], m["body"], m["sim"]) for m in server.sent] == [
        (ANNA, "All systems running.", 1),
        (BEN, "Commands: STATUS", 2),
    ]


def test_reply_guard() -> None:
    guard = load("auto_reply").ReplyGuard(cooldown_seconds=600, replies_per_hour=3)
    assert guard.refusal("+4369912345678", now=0) is None
    assert guard.refusal("+4369912345678", now=599) is not None
    assert guard.refusal("+43 699 123-456.78", now=599) is not None
    assert guard.refusal("+4369912345678", now=600) is None
    for sender in ("A1", "Post", "8810", "12345", "", "+43 abc"):
        assert guard.refusal(sender, now=700) == "not a phone number"
    assert guard.refusal("+4369087654321", now=700) is None
    # Three answers in the last hour: the fourth sender has to wait.
    assert guard.refusal("+436601111111", now=800) == (
        "too many answers in the last hour"
    )
    assert guard.refusal("+436601111111", now=3600) is None


def test_answers() -> None:
    answer_for = load("auto_reply").answer_for
    assert answer_for(" status\n") == "All systems running."
    assert answer_for("Hello") == "Commands: STATUS"
