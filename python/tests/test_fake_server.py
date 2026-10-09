"""Tests of the fake server itself, with a bare WebSocket connection.

They pin down the behaviour of ``API.md`` that the client never triggers
on its own: close codes, limits and errors before the login.
"""

from __future__ import annotations

import json
import time
from collections.abc import Iterator
from typing import Any, Callable

import pytest
from conftest import ANNA, PASSWORD
from fake_server import (
    EVENT_NAMES,
    FakeSmsDock,
    count_parts,
    normalize_number,
    same_peer,
)
from websockets.exceptions import ConnectionClosed, InvalidStatus
from websockets.sync.client import connect

import smsdock.client

ServerFactory = Callable[..., FakeSmsDock]


class Wire:
    """A bare connection that sends and receives JSON objects."""

    def __init__(self, server: FakeSmsDock, path: str = "") -> None:
        context = None
        if server.tls is not None:
            # The context of the client: it accepts the self-signed
            # certificate and keeps reads and writes of the two threads
            # of websockets apart.
            context = smsdock.client._unverified_context()
        self.connection = connect(
            server.url + path, ssl=context, proxy=None, open_timeout=5
        ).__enter__()

    def send(self, **request: Any) -> None:
        self.connection.send(json.dumps(request))

    def receive(self, timeout: float = 3.0) -> Any:
        return json.loads(self.connection.recv(timeout=timeout))

    def call(self, **request: Any) -> Any:
        self.send(**request)
        return self.receive()

    def login(self) -> None:
        assert self.receive()["event"] == "hello"
        assert self.call(id=0, action="authenticate", password=PASSWORD)["ok"]

    def close_code(self, timeout: float = 3.0) -> int:
        """Read until the server closes and return the close code."""

        with pytest.raises(ConnectionClosed) as raised:
            while True:
                self.connection.recv(timeout=timeout)
        assert raised.value.rcvd is not None
        return raised.value.rcvd.code


@pytest.fixture
def wires() -> Iterator[Callable[..., Wire]]:
    opened: list[Wire] = []

    def open_wire(server: FakeSmsDock, path: str = "") -> Wire:
        wire = Wire(server, path)
        opened.append(wire)
        return wire

    yield open_wire
    for wire in opened:
        wire.connection.close()


def test_hello_comes_first(server: FakeSmsDock, wires: Callable[..., Wire]) -> None:
    assert wires(server).receive() == {
        "event": "hello", "protocolVersion": 1, "authenticationRequired": True,
    }


def test_login_result(server: FakeSmsDock, wires: Callable[..., Wire]) -> None:
    wire = wires(server)
    wire.receive()
    server.receive_sms(ANNA, "one event so far")
    assert wire.call(id="1", action="authenticate", password=PASSWORD) == {
        "id": "1", "ok": True,
        "result": {
            "protocolVersion": 1,
            "app": {"name": "SMS Dock", "version": "1.0.0",
                    "protocolVersion": 1},
            "latestSeq": 1,
            "epoch": server.epoch,
        },
    }


def test_actions_need_a_login(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    wire = wires(server)
    wire.receive()
    response = wire.call(id=5, action="messages.list")
    assert response["id"] == 5
    assert response["ok"] is False
    assert response["error"]["code"] == "not_authenticated"


def test_wrong_password_closes_with_4401(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    wire = wires(server)
    wire.receive()
    response = wire.call(id="1", action="authenticate", password="wrong")
    assert response["error"]["code"] == "invalid_password"
    assert wire.close_code() == 4401


def test_lock_after_five_wrong_passwords(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    for _ in range(5):
        wire = wires(server)
        wire.receive()
        response = wire.call(id="1", action="authenticate", password="wrong")
        assert response["error"]["code"] == "invalid_password"
    wire = wires(server)
    wire.receive()
    response = wire.call(id="1", action="authenticate", password=PASSWORD)
    assert response["error"]["code"] == "locked_out"
    assert 1 <= response["error"]["retryAfter"] <= 30
    assert wire.close_code() == 4401


def test_licence_required_closes_with_4403(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    server.licence_enforced = True
    wire = wires(server)
    wire.receive()
    response = wire.call(id="1", action="authenticate", password=PASSWORD)
    assert response["error"]["code"] == "licence_required"
    assert wire.close_code() == 4403
    # The licence is looked at first: a wrong password is not counted.
    wire = wires(server)
    wire.receive()
    response = wire.call(id="1", action="authenticate", password="wrong")
    assert response["error"]["code"] == "licence_required"
    assert server.failed_logins == 0
    server.licensed = True
    wires(server).login()


def test_login_timeout_closes_with_4401(
    server_factory: ServerFactory, wires: Callable[..., Wire]
) -> None:
    server = server_factory(auth_timeout=0.2)
    wire = wires(server)
    assert wire.receive()["event"] == "hello"
    assert wire.close_code() == 4401


def test_no_timeout_after_the_login(
    server_factory: ServerFactory, wires: Callable[..., Wire]
) -> None:
    server = server_factory(auth_timeout=0.2)
    wire = wires(server)
    wire.login()
    with pytest.raises(TimeoutError):
        wire.receive(timeout=0.5)
    assert wire.call(id=1, action="ping")["ok"]


def test_large_frame_closes_with_1009(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    wire = wires(server)
    wire.login()
    wire.send(id=1, action="messages.send", to=ANNA, body="x" * (64 * 1024))
    assert wire.close_code() == 1009
    assert server.sent == []


def test_binary_frame_closes_with_4400(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    wire = wires(server)
    wire.login()
    wire.connection.send(b"\x00\x01")
    assert wire.close_code() == 4400


def test_ninth_client_closes_with_4409(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    for _ in range(8):
        wires(server).login()
    assert wires(server).close_code() == 4409
    assert server.client_count == 8


def test_other_paths_do_not_exist(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    with pytest.raises(InvalidStatus) as raised:
        wires(server, "/other")
    assert raised.value.response.status_code == 404


@pytest.mark.parametrize("frame", ["not JSON", "[1]", '"text"', "12"])
def test_invalid_json(
    server: FakeSmsDock, wires: Callable[..., Wire], frame: str
) -> None:
    wire = wires(server)
    wire.login()
    wire.connection.send(frame)
    response = wire.receive()
    assert response["ok"] is False
    assert response["error"]["code"] == "invalid_json"
    assert wire.call(id=1, action="ping")["ok"]


@pytest.mark.parametrize("request_fields, code", [
    ({"action": "unknown"}, "unknown_action"),
    ({}, "invalid_request"),
    ({"action": "messages.list", "limit": 0}, "invalid_request"),
    ({"action": "messages.list", "limit": 201}, "invalid_request"),
    ({"action": "messages.list", "limit": "10"}, "invalid_request"),
    ({"action": "messages.list", "direction": "up"}, "invalid_request"),
    ({"action": "messages.list", "cursor": "nonsense"}, "invalid_request"),
    ({"action": "messages.get"}, "invalid_request"),
    ({"action": "messages.get", "messageId": "2813"}, "invalid_request"),
    ({"action": "messages.get", "messageId": 1}, "not_found"),
    ({"action": "messages.delete"}, "invalid_request"),
    ({"action": "conversations.delete"}, "invalid_request"),
    ({"action": "conversations.delete", "conversationId": 1}, "not_found"),
    ({"action": "conversations.markRead"}, "invalid_request"),
    ({"action": "conversations.markRead", "conversationId": 170},
     "not_default_app"),
    ({"action": "contacts.list", "cursor": "x"}, "invalid_request"),
    ({"action": "messages.send", "to": ANNA}, "invalid_request"),
    ({"action": "messages.send", "to": ANNA, "body": "x", "requestId": "a b"},
     "invalid_request"),
    ({"action": "messages.status", "requestId": "none"}, "not_found"),
    ({"action": "subscribe", "events": "sms.received"}, "invalid_request"),
    ({"action": "subscribe"}, "invalid_request"),
])
def test_errors(
    server: FakeSmsDock, wires: Callable[..., Wire],
    request_fields: Any, code: str,
) -> None:
    wire = wires(server)
    wire.login()
    response = wire.call(**request_fields)
    assert response["ok"] is False
    assert response["error"]["code"] == code
    assert isinstance(response["error"]["message"], str)


def test_request_without_id(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    wire = wires(server)
    wire.login()
    response = wire.call(action="ping")
    assert response["id"] is None and response["ok"] is True
    # The field "id" names the request only, never a message.
    response = wire.call(id=2801, action="messages.get")
    assert response["id"] == 2801
    assert response["error"]["code"] == "invalid_request"


def test_sequence_numbers_count_all_events(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    wire = wires(server)
    wire.login()
    server.receive_sms(ANNA, "not subscribed yet")
    assert wire.call(id=1, action="subscribe", events=["sms.received"])["ok"]
    server.emit("conversations.changed", {})
    server.receive_sms(ANNA, "third event")
    event = wire.receive()
    assert event["event"] == "sms.received"
    assert event["seq"] == 3


def test_stopping_closes_with_1001(
    server_factory: ServerFactory, wires: Callable[..., Wire]
) -> None:
    server = server_factory()
    wire = wires(server)
    wire.login()
    server.stop()
    assert wire.close_code() == 1001


def test_restart_changes_the_epoch(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    wire = wires(server)
    wire.login()
    server.receive_sms(ANNA, "before")
    first = server.epoch
    server.restart()
    with pytest.raises(ConnectionClosed):
        wire.receive()
    wire = wires(server)
    wire.receive()
    result = wire.call(id=1, action="authenticate", password=PASSWORD)["result"]
    assert result["epoch"] == server.epoch != first
    assert result["latestSeq"] == 0
    assert wire.call(id=2, action="subscribe", events=["*"])["ok"]
    server.receive_sms(ANNA, "after")
    assert wire.receive()["seq"] == 1


def test_rate_limit(
    server_factory: ServerFactory, wires: Callable[..., Wire]
) -> None:
    server = server_factory(requests_per_second=5)
    wire = wires(server)
    wire.login()
    for number in range(8):
        wire.send(id=number, action="ping")
    responses = [wire.receive() for _ in range(8)]
    assert [r["id"] for r in responses] == list(range(8))
    # The login was the first request of this second.
    assert [r["ok"] for r in responses] == [True] * 4 + [False] * 4
    assert responses[4]["error"] == {
        "code": "rate_limited", "message": "Too many requests.", "retryAfter": 1,
    }
    assert server.refused_count == 4
    assert [r["action"] for r in server.requests].count("ping") == 4
    # Frames that cannot be read count as well and get the id null.
    wire.connection.send("not JSON")
    response = wire.receive()
    assert (response["id"], response["error"]["code"]) == (None, "rate_limited")
    time.sleep(1.1)
    assert wire.call(id=9, action="ping")["ok"]


def test_flooding_closes_with_4429(
    server_factory: ServerFactory, wires: Callable[..., Wire]
) -> None:
    server = server_factory(requests_per_second=3, max_refused=5)
    wire = wires(server)
    wire.login()
    for number in range(2 + 6):
        wire.send(id=number, action="ping")
    assert wire.close_code() == 4429
    # 5 refused requests in a row are tolerated, the 6th is one too many.
    assert server.refused_count == 6


def test_subscription_lists_names_instead_of_the_star(
    server: FakeSmsDock, wires: Callable[..., Wire]
) -> None:
    wire = wires(server)
    wire.login()
    result = wire.call(
        id=1, action="subscribe", events=["*"], address="0699 1234567"
    )["result"]
    assert result == {"events": list(EVENT_NAMES), "address": "0699 1234567"}
    result = wire.call(id=2, action="subscribe", events=["sms.sent"],
                       address=None)["result"]
    assert result == {"events": list(EVENT_NAMES), "address": None}
    result = wire.call(id=3, action="unsubscribe", events=["*"])["result"]
    assert result == {"events": [], "address": None}


@pytest.mark.parametrize("first, second, same", [
    ("+43 699 1234567", "0043 699 1234567", True),
    ("+43 699 1234567", "0699 1234567", True),
    ("0043 699 1234567", "0699 1234567", True),
    ("0699 1234567", "+43 699 1234567", True),
    ("0699/12 34-567", "(0699) 12.34.567", True),
    ("0699 1234567", "699 1234567", True),
    ("1234567", "+43 699 1234567", True),
    ("234567", "+43 699 1234567", False),
    ("0699 1234567", "0699 1234568", False),
    ("+43 699 1234567", "+49 699 1234567", False),
    ("+43 699 1234567", "+43 699 12345678", False),
    ("8810", "8810", True),
    ("8810", "08810", False),
    ("8810", "+43 699 1238810", False),
    ("MyBank", "mybank", True),
    (" MyBank ", "MYBANK", True),
    ("MyBank", "MyBank2", False),
    ("A1", "+431", False),
])
def test_same_peer(first: str, second: str, same: bool) -> None:
    assert same_peer(first, second) is same
    assert same_peer(second, first) is same


def test_helpers() -> None:
    assert normalize_number("+43 (699) 123-456.78") == "+4369912345678"
    assert normalize_number(" 0699/123 456 78 ") == "069912345678"
    assert count_parts("x" * 160) == 1
    assert count_parts("x" * 161) == 2
    assert count_parts("x" * 1530) == 10
    assert count_parts("ü" * 70) == 1
    assert count_parts("ü" * 71) == 2
    assert count_parts("ü" * 670) == 10
    assert count_parts("ü" * 671) == 11
