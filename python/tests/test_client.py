"""Tests of :class:`smsdock.SmsDockClient` against the fake server."""

from __future__ import annotations

import hashlib
import json
import ssl
import threading
import time
from datetime import datetime, timezone
from typing import Any, Callable

import pytest
from conftest import ANNA, BASE_TIME, BEN, PASSWORD, wait_until
from fake_server import FakeSmsDock, TlsFiles

import smsdock.client
from smsdock import (
    ApiError,
    AuthenticationError,
    CertificateMismatchError,
    ConnectionInterrupted,
    LicenceRequiredError,
    ProtocolError,
    SmsDockClient,
    SmsDockError,
    certificate_fingerprint,
    fetch_fingerprint,
    format_fingerprint,
    normalize_fingerprint,
)

ServerFactory = Callable[..., FakeSmsDock]
ClientFactory = Callable[..., SmsDockClient]
FINGERPRINT = ":".join(["AB"] * 32)
URL = "wss://192.168.1.50:9443/ws"


def actions(server: FakeSmsDock) -> list[str]:
    """Return the actions the server received, in order."""

    return [request.get("action") for request in server.requests]


def last_request(server: FakeSmsDock, action: str) -> Any:
    """Return the newest request with this action."""

    return [r for r in server.requests if r.get("action") == action][-1]


def event_frame(sequence: int, body: str = "Hi") -> Any:
    """Return an ``sms.received`` event as the server would send it."""

    return {
        "event": "sms.received",
        "seq": sequence,
        "data": {"message": {
            "id": 9000 + sequence, "conversationId": 1, "address": ANNA,
            "contact": None, "direction": "in", "body": body,
            "timestamp": BASE_TIME, "sentTimestamp": BASE_TIME, "read": False,
            "status": "received", "sim": 1, "requestId": None,
        }},
    }


# ----------------------------------------------------------------------
# Construction
# ----------------------------------------------------------------------


@pytest.mark.parametrize("url", [
    "ws://192.168.1.50:9443/ws",
    "http://192.168.1.50:9443/ws",
    "https://192.168.1.50:9443/ws",
    "192.168.1.50:9443",
    "wss:///ws",
    "",
])
def test_refuses_everything_but_wss(url: str) -> None:
    with pytest.raises(ValueError, match="wss://"):
        SmsDockClient(url, PASSWORD, fingerprint=FINGERPRINT)


def test_reads_the_password_from_the_environment(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("SMSDOCK_PASSWORD", "from-the-environment")
    client = SmsDockClient(URL, fingerprint=FINGERPRINT)
    assert client._password == "from-the-environment"


def test_needs_a_password(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("SMSDOCK_PASSWORD", raising=False)
    with pytest.raises(ValueError, match="SMSDOCK_PASSWORD"):
        SmsDockClient(URL, fingerprint=FINGERPRINT)


def test_needs_a_fingerprint_unless_insecure() -> None:
    with pytest.raises(ValueError, match="SMSDOCK_FINGERPRINT"):
        SmsDockClient(URL, PASSWORD)
    assert SmsDockClient(URL, PASSWORD, insecure=True).insecure


def test_reads_the_fingerprint_from_the_environment(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("SMSDOCK_FINGERPRINT", FINGERPRINT.lower())
    client = SmsDockClient(URL, PASSWORD)
    assert client._fingerprint == bytes([0xAB] * 32)
    monkeypatch.setenv("SMSDOCK_FINGERPRINT", "AB:CD")
    with pytest.raises(ValueError, match="SHA-256"):
        SmsDockClient(URL, PASSWORD)


@pytest.mark.parametrize("fingerprint", [
    "", "AB:CD", "zz" * 32, "AB" * 31, "AB" * 33, "AB" * 20,
])
def test_refuses_malformed_fingerprints(fingerprint: str) -> None:
    with pytest.raises(ValueError, match="SHA-256"):
        SmsDockClient(URL, PASSWORD, fingerprint=fingerprint)


@pytest.mark.parametrize("options", [
    {"timeout": 0},
    {"connect_timeout": -1},
    {"reconnect_attempts": -1},
    {"reconnect_delay": -1},
    {"event_queue_size": 0},
])
def test_refuses_impossible_options(options: Any) -> None:
    with pytest.raises(ValueError):
        SmsDockClient(URL, PASSWORD, fingerprint=FINGERPRINT, **options)


def test_fingerprint_forms() -> None:
    digits = "0123456789abcdef" * 4
    colons = ":".join(digits[i:i + 2] for i in range(0, 64, 2))
    assert normalize_fingerprint(digits.upper()) == digits
    assert normalize_fingerprint(colons) == digits
    assert normalize_fingerprint(f" {colons.upper()}\n") == digits
    assert normalize_fingerprint(f"sha256 Fingerprint={colons.upper()}") == digits
    assert format_fingerprint(digits) == colons.upper()
    assert certificate_fingerprint(b"certificate") == format_fingerprint(
        hashlib.sha256(b"certificate").hexdigest()
    )


def test_repr_does_not_show_the_password() -> None:
    client = SmsDockClient(URL, "very-secret", fingerprint=FINGERPRINT)
    assert "very-secret" not in repr(client)
    assert URL in repr(client)


# ----------------------------------------------------------------------
# Handshake
# ----------------------------------------------------------------------


def test_logs_in(server: FakeSmsDock, client: SmsDockClient) -> None:
    assert client.connected
    assert client.server_info == {
        "name": "SMS Dock", "version": "1.0.0", "protocolVersion": 1,
    }
    assert client.connection_count == 1
    assert actions(server) == ["authenticate"]
    assert server.requests[0]["password"] == PASSWORD
    assert server.login_count == 1


def test_context_manager_closes(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    with client_factory(server, connect=False) as client:
        assert client.ping()["time"] > 0
        assert server.client_count == 1
    assert not client.connected
    assert wait_until(lambda: server.client_count == 0)


def test_wrong_password(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    client = client_factory(server, connect=False, password="wrong-password")
    with pytest.raises(AuthenticationError) as raised:
        client.connect()
    assert raised.value.code == "invalid_password"
    assert isinstance(raised.value, ApiError)
    assert not client.connected
    # A wrong password counts towards the lock: it must not be repeated.
    assert server.login_count == 1


def test_wrong_password_is_not_retried_by_requests(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    client = client_factory(server, connect=False, password="wrong-password")
    with pytest.raises(AuthenticationError):
        client.status()
    assert server.login_count == 1


def test_locked_out(server: FakeSmsDock, client_factory: ClientFactory) -> None:
    server.lock_logins(30)
    client = client_factory(server, connect=False)
    with pytest.raises(AuthenticationError) as raised:
        client.connect()
    assert raised.value.code == "locked_out"
    assert raised.value.retry_after is not None
    assert 1 <= raised.value.retry_after <= 30
    assert raised.value.details == {"retryAfter": raised.value.retry_after}
    assert server.login_count == 1


def test_licence_required(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    server.licence_enforced = True
    client = client_factory(server, connect=False)
    with pytest.raises(LicenceRequiredError) as raised:
        client.connect()
    assert raised.value.code == "licence_required"
    assert isinstance(raised.value, ApiError)


def test_refuses_other_protocol_versions(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    server.protocol_version = 2
    client = client_factory(server, connect=False)
    with pytest.raises(ProtocolError, match="version 2"):
        client.connect()
    # The password is not sent to a server of another protocol version.
    assert server.requests == []


def test_close_code_4401_while_logging_in(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(auth_timeout=0.2)
    server.ignore_next("authenticate")
    client = client_factory(server, connect=False)
    with pytest.raises(AuthenticationError) as raised:
        client.connect()
    assert raised.value.code == "not_authenticated"
    assert raised.value.details["closeCode"] == 4401
    assert client.connection_count == 1


def test_too_many_clients(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(max_clients=1)
    client_factory(server)
    second = client_factory(server, connect=False)
    with pytest.raises(ConnectionInterrupted) as raised:
        second.connect()
    assert raised.value.close_code == 4409


def test_nothing_listens(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory()
    client = client_factory(server, connect=False, connect_timeout=2.0)
    server.stop()
    with pytest.raises(ConnectionInterrupted, match="Cannot connect"):
        client.connect()


def test_all_errors_share_a_base_class() -> None:
    for kind in (
        ApiError, AuthenticationError, LicenceRequiredError, ProtocolError,
        ConnectionInterrupted, CertificateMismatchError,
    ):
        assert issubclass(kind, SmsDockError)


# ----------------------------------------------------------------------
# Certificate pinning over real TLS
# ----------------------------------------------------------------------


def test_pins_the_certificate(
    need_tls: TlsFiles, server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    for form in (
        need_tls.fingerprint,
        need_tls.fingerprint.lower(),
        need_tls.fingerprint.replace(":", ""),
        need_tls.fingerprint.replace(":", "").lower(),
    ):
        client = client_factory(server, fingerprint=form)
        assert client.ping()["time"] > 0


def test_peer_certificate_is_reachable_through_the_connection(
    need_tls: TlsFiles, client: SmsDockClient
) -> None:
    tls_socket = client._websocket.socket
    assert isinstance(tls_socket, ssl.SSLSocket)
    der = tls_socket.getpeercert(binary_form=True)
    assert der == ssl.PEM_cert_to_DER_cert(need_tls.certificate.read_text())
    assert certificate_fingerprint(der) == need_tls.fingerprint
    assert smsdock.client._peer_certificate(client._websocket) == der


def test_certificate_of_a_connection_that_closed_already(
    need_tls: TlsFiles, client: SmsDockClient
) -> None:
    websocket = client._websocket
    der = websocket.socket.getpeercert(binary_form=True)
    websocket.close()
    # The certificate recorded in the TLS handshake of this socket.
    assert smsdock.client._peer_certificate(websocket) == der
    del websocket.socket.__dict__["_smsdock_peer_certificate"]
    with pytest.raises(OSError, match="closed before"):
        smsdock.client._peer_certificate(websocket)


def test_certificate_mismatch(
    need_tls: TlsFiles,
    other_tls_files: TlsFiles,
    server: FakeSmsDock,
    client_factory: ClientFactory,
) -> None:
    client = client_factory(
        server, connect=False, fingerprint=other_tls_files.fingerprint
    )
    with pytest.raises(CertificateMismatchError) as raised:
        client.connect()
    assert raised.value.expected == other_tls_files.fingerprint
    assert raised.value.actual == need_tls.fingerprint
    assert other_tls_files.fingerprint in str(raised.value)
    assert need_tls.fingerprint in str(raised.value)
    assert not client.connected
    time.sleep(0.1)
    # Nothing reached the server: no WebSocket, no password.
    assert server.connection_count == 0
    assert server.requests == []


def test_certificate_mismatch_is_found_through_the_connection(
    need_tls: TlsFiles,
    other_tls_files: TlsFiles,
    server: FakeSmsDock,
    client_factory: ClientFactory,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    # Without the check inside the TLS context, the check of the
    # certificate read from the connection must still refuse the server.
    monkeypatch.setattr(
        smsdock.client._PinnedContext, "wrap_socket", ssl.SSLContext.wrap_socket
    )
    client = client_factory(
        server, connect=False, fingerprint=other_tls_files.fingerprint
    )
    with pytest.raises(CertificateMismatchError) as raised:
        client.connect()
    assert raised.value.actual == need_tls.fingerprint
    time.sleep(0.1)
    assert server.requests == []
    assert server.login_count == 0


def test_certificate_mismatch_is_not_retried(
    need_tls: TlsFiles,
    other_tls_files: TlsFiles,
    server: FakeSmsDock,
    client_factory: ClientFactory,
) -> None:
    client = client_factory(
        server, connect=False, fingerprint=other_tls_files.fingerprint
    )
    with pytest.raises(CertificateMismatchError):
        client.status()
    assert client.connection_count == 0


def test_new_certificate_after_reconnect(
    need_tls: TlsFiles,
    other_tls_files: TlsFiles,
    server_factory: ServerFactory,
    client_factory: ClientFactory,
) -> None:
    first = server_factory()
    client = client_factory(first)
    assert client.ping()
    first.stop()
    second = server_factory(tls=other_tls_files, port=first.port)
    with pytest.raises(CertificateMismatchError):
        client.status()
    assert second.requests == []


def test_refuses_a_connection_without_certificate() -> None:
    class Connection:
        closed = False

        def close(self) -> None:
            self.closed = True

    connection = Connection()
    client = SmsDockClient(
        URL, PASSWORD, fingerprint=FINGERPRINT,
        connect_factory=lambda *_, **__: connection,
    )
    with pytest.raises(ProtocolError, match="certificate"):
        client.connect()
    assert connection.closed


def test_fetch_fingerprint(need_tls: TlsFiles, server: FakeSmsDock) -> None:
    assert fetch_fingerprint("127.0.0.1", server.port) == need_tls.fingerprint
    time.sleep(0.1)
    assert server.requests == []


def test_insecure_skips_the_check_and_warns(
    need_tls: TlsFiles,
    server: FakeSmsDock,
    client_factory: ClientFactory,
    capsys: pytest.CaptureFixture[str],
) -> None:
    client = client_factory(server, fingerprint=None, insecure=True)
    assert client.ping()["time"] > 0
    server.drop_connections()
    assert client.ping()["time"] > 0
    captured = capsys.readouterr()
    assert captured.err.count("WARNING") == 1
    assert "not checked" in captured.err
    assert captured.out == ""


# ----------------------------------------------------------------------
# Requests
# ----------------------------------------------------------------------


def test_status(client: SmsDockClient) -> None:
    status = client.status()
    assert status["app"] == {
        "name": "SMS Dock", "version": "1.0.0", "protocolVersion": 1,
    }
    assert status["deleteMode"] == "hidden"
    assert status["sims"][0]["subscriptionId"] == 1
    assert status["permissions"]["sendSms"] is True
    assert status["licence"]["remaining"] is None


def test_ping(client: SmsDockClient) -> None:
    assert abs(client.ping()["time"] - time.time() * 1000) < 60_000


def test_conversations(server: FakeSmsDock, client: SmsDockClient) -> None:
    page = client.conversations()
    assert [item["address"] for item in page["items"]] == [ANNA, BEN, "A1"]
    assert page["nextCursor"] is None
    anna = page["items"][0]
    assert anna["contact"] == "Anna Example"
    assert anna["messageCount"] == 25
    assert anna["unreadCount"] == 3
    assert anna["snippet"] == "Anna 24"
    assert last_request(server, "conversations.list") == {
        "id": "py-2", "action": "conversations.list", "limit": 50,
    }


def test_conversations_filters(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    assert [c["address"] for c in client.conversations(q="invoice")["items"]] == [BEN]
    assert [c["address"] for c in client.conversations(q="anna")["items"]] == [ANNA]
    unread = client.conversations(unread=True)["items"]
    assert [c["address"] for c in unread] == [ANNA, "A1"]
    assert [c["address"] for c in client.conversations(sim=2)["items"]] == [BEN]
    request = last_request(server, "conversations.list")
    assert request["sim"] == 2 and "unread" not in request and "q" not in request


def test_conversations_paging(client: SmsDockClient) -> None:
    first = client.conversations(limit=2)
    assert len(first["items"]) == 2
    assert first["nextCursor"] is not None
    second = client.conversations(limit=2, cursor=first["nextCursor"])
    assert [c["address"] for c in second["items"]] == ["A1"]
    assert second["nextCursor"] is None
    assert [c["address"] for c in client.iter_conversations(page_size=1)] == [
        ANNA, BEN, "A1",
    ]


def test_last_ten_incoming_messages(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    page = client.messages(direction="in", limit=10)
    assert [m["body"] for m in page["items"]] == [
        f"Anna {index}" for index in range(24, 4, -2)
    ]
    assert all(m["direction"] == "in" for m in page["items"])
    assert page["nextCursor"] is not None
    assert last_request(server, "messages.list") == {
        "id": "py-2", "action": "messages.list", "direction": "in", "limit": 10,
    }


@pytest.mark.parametrize("address, count", [
    (BEN, 3),
    ("+43 690 876-54321", 3),
    ("0043 690 87654321", 3),
    ("0690 / 876 54 321", 3),
    ("(0690) 876.54.321", 3),
    ("87654321", 3),
    ("654321", 0),
    ("+49 690 87654321", 0),
    ("a1", 1),
    ("A1", 1),
    ("A1 Telekom", 0),
])
def test_messages_of_a_number(
    client: SmsDockClient, address: str, count: int
) -> None:
    assert len(client.messages(address=address)["items"]) == count


def test_conversation_wins_over_address(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    ben = client.conversations(q="invoice")["items"][0]["id"]
    page = client.messages(conversation_id=ben, address=ANNA)
    assert {m["address"] for m in page["items"]} == {BEN}
    request = last_request(server, "messages.list")
    assert (request["conversationId"], request["address"]) == (ben, ANNA)


def test_messages_filters(server: FakeSmsDock, client: SmsDockClient) -> None:
    assert len(client.messages(address=BEN)["items"]) == 3
    conversation = client.conversations(q="invoice")["items"][0]["id"]
    by_conversation = client.messages(conversation_id=conversation)["items"]
    assert {m["address"] for m in by_conversation} == {BEN}
    assert len(client.messages(direction="out", limit=200)["items"]) == 13
    assert [m["body"] for m in client.messages(q="INVOICE")["items"]] == [
        "Thanks for the invoice", "Your invoice is attached",
    ]
    assert len(client.messages(sim=2)["items"]) == 3
    unread = client.messages(unread=True)["items"]
    assert [m["body"] for m in unread] == [
        "Anna 24", "Anna 23", "Anna 22", "Your bill is ready",
    ]
    combined = client.messages(address=ANNA, direction="in", q="Anna 1", limit=3)
    assert [m["body"] for m in combined["items"]] == ["Anna 18", "Anna 16", "Anna 14"]
    assert last_request(server, "messages.list") == {
        "id": "py-9", "action": "messages.list", "address": ANNA,
        "direction": "in", "q": "Anna 1", "limit": 3,
    }


def test_messages_time_range(server: FakeSmsDock, client: SmsDockClient) -> None:
    page = client.messages(since=BASE_TIME + 3000, until=BASE_TIME + 5000)
    assert [m["body"] for m in page["items"]] == ["Anna 5", "Anna 4", "Anna 3"]
    request = last_request(server, "messages.list")
    assert request["from"] == BASE_TIME + 3000
    assert request["to"] == BASE_TIME + 5000

    moment = datetime.fromtimestamp(
        (BASE_TIME + 23_000) / 1000, tz=timezone.utc
    )
    page = client.messages(since=moment)
    assert [m["body"] for m in page["items"]] == ["Anna 24", "Anna 23"]
    assert last_request(server, "messages.list")["from"] == BASE_TIME + 23_000


def test_messages_paging(client: SmsDockClient) -> None:
    first = client.messages(address=ANNA, limit=10)
    second = client.messages(address=ANNA, limit=10, cursor=first["nextCursor"])
    third = client.messages(address=ANNA, limit=10, cursor=second["nextCursor"])
    bodies = [m["body"] for page in (first, second, third) for m in page["items"]]
    assert bodies == [f"Anna {index}" for index in range(24, -1, -1)]
    assert third["nextCursor"] is None


def test_iter_messages_follows_the_cursor(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    messages = list(client.iter_messages(address=ANNA, page_size=10))
    assert [m["body"] for m in messages] == [
        f"Anna {index}" for index in range(24, -1, -1)
    ]
    requests = [r for r in server.requests if r["action"] == "messages.list"]
    assert [r.get("cursor") for r in requests] == [
        None, f"{BASE_TIME + 15000}:{messages[9]['id']}",
        f"{BASE_TIME + 5000}:{messages[19]['id']}",
    ]
    assert all(r["limit"] == 10 for r in requests)


def test_iter_messages_is_lazy_and_bounded(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    iterator = client.iter_messages(page_size=10, max_items=12)
    assert actions(server).count("messages.list") == 0
    assert len(list(iterator)) == 12
    requests = [r for r in server.requests if r["action"] == "messages.list"]
    assert [r["limit"] for r in requests] == [10, 2]
    assert list(client.iter_messages(max_items=0)) == []
    assert len(list(client.iter_messages(page_size=200))) == 29


def test_iter_messages_refuses_a_cursor_that_does_not_move(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    page = {"items": [{"id": 1}], "nextCursor": "again"}
    server.answer_next("messages.list", page)
    server.answer_next("messages.list", page)
    with pytest.raises(ProtocolError, match="cursor"):
        list(client.iter_messages())
    assert actions(server).count("messages.list") == 2


def test_iter_messages_refuses_a_page_without_items(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.answer_next("messages.list", {"nextCursor": None})
    with pytest.raises(ProtocolError, match="items"):
        list(client.iter_messages())


def test_pages_may_be_shorter_than_the_limit(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.answer_next(
        "messages.list", {"items": [{"id": 1}], "nextCursor": "1:1"}
    )
    server.answer_next("messages.list", {"items": [], "nextCursor": "1:0"})
    server.answer_next("messages.list", {"items": [{"id": 2}], "nextCursor": None})
    assert list(client.iter_messages(page_size=50)) == [{"id": 1}, {"id": 2}]


@pytest.mark.parametrize("arguments", [
    {"direction": "sideways"},
    {"limit": 0},
    {"limit": 201},
    {"limit": True},
    {"sim": "1"},
    {"conversation_id": "7"},
    {"since": "yesterday"},
])
def test_messages_refuses_wrong_arguments(
    server: FakeSmsDock, client: SmsDockClient, arguments: Any
) -> None:
    with pytest.raises((ValueError, TypeError)):
        client.messages(**arguments)
    assert actions(server) == ["authenticate"]


def test_iterators_check_their_arguments_at_once(client: SmsDockClient) -> None:
    with pytest.raises(ValueError):
        client.iter_messages(page_size=500)
    with pytest.raises(ValueError):
        client.iter_messages(max_items=-1)
    with pytest.raises(ValueError):
        client.iter_messages(direction="sideways")


def test_get_message(server: FakeSmsDock, client: SmsDockClient) -> None:
    newest = client.messages(limit=1)["items"][0]
    assert client.get_message(newest["id"]) == newest
    assert set(newest) == {
        "id", "conversationId", "address", "contact", "direction", "body",
        "timestamp", "sentTimestamp", "read", "status", "sim", "requestId",
    }
    assert last_request(server, "messages.get") == {
        "id": "py-3", "action": "messages.get", "messageId": newest["id"],
    }


def test_get_message_not_found(client: SmsDockClient) -> None:
    with pytest.raises(ApiError) as raised:
        client.get_message(1)
    assert raised.value.code == "not_found"
    assert "not_found" in str(raised.value)
    assert not isinstance(raised.value, AuthenticationError)
    assert client.ping()


def test_send(server: FakeSmsDock, client: SmsDockClient) -> None:
    result = client.send("+43 690 876-54321", "All systems running.")
    assert result["to"] == BEN
    assert result["parts"] == 1
    assert result["status"] == "queued"
    request_id = result["requestId"]
    assert len(request_id) == 32 and int(request_id, 16) >= 0
    assert last_request(server, "messages.send") == {
        "id": "py-2", "action": "messages.send", "to": "+43 690 876-54321",
        "body": "All systems running.", "requestId": request_id,
    }
    assert server.sent == [{
        "requestId": request_id, "to": BEN, "body": "All systems running.",
        "sim": 1,
    }]


def test_send_creates_a_new_request_id_each_time(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    first = client.send(BEN, "one")
    second = client.send(BEN, "one")
    assert first["requestId"] != second["requestId"]
    assert len(server.sent) == 2


def test_send_with_own_request_id_and_sim(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    result = client.send(BEN, "Grüße", sim=2, request_id="order-42.confirm_1")
    assert result["requestId"] == "order-42.confirm_1"
    assert server.sent[0]["sim"] == 2
    assert server.sent[0]["body"] == "Grüße"
    assert result["status"] == "queued"
    assert client.wait_for_result("order-42.confirm_1", 5)["status"] == "sent"
    # A repeat is not sent again; it tells the status of today.
    again = client.send(BEN, "Grüße", sim=2, request_id="order-42.confirm_1")
    assert again == {**result, "status": "sent"}
    assert len(server.sent) == 1


def test_repeat_with_the_number_written_differently(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    first = client.send("+43 690 876 54 321", "Hello", request_id="r-1")
    assert first["to"] == BEN
    for number in ("0043 (690) 876-54321", "0690/876.54.321", BEN):
        assert client.send(number, "Hello", request_id="r-1")["to"] == BEN
    assert len(server.sent) == 1


@pytest.mark.parametrize("to, body", [
    (BEN, "Another text"),
    (ANNA, "Hello"),
])
def test_request_id_of_another_message(
    server: FakeSmsDock, client: SmsDockClient, to: str, body: str
) -> None:
    client.send(BEN, "Hello", request_id="r-1")
    with pytest.raises(ApiError) as raised:
        client.send(to, body, request_id="r-1")
    assert raised.value.code == "invalid_request"
    assert len(server.sent) == 1


def test_send_group(server: FakeSmsDock, client: SmsDockClient) -> None:
    result = client.send_group(
        ["+43 690 876-54321", ANNA, BEN], "Hello all", sim=2, request_id="news-1"
    )
    assert result["requestId"] == "news-1"
    assert result["to"] == [BEN, ANNA]
    assert result["parts"] == 1
    assert result["status"] == "queued"
    assert result["messages"] == [
        {"requestId": "news-1.1", "to": BEN, "parts": 1, "status": "queued"},
        {"requestId": "news-1.2", "to": ANNA, "parts": 1, "status": "queued"},
    ]
    assert last_request(server, "messages.sendGroup") == {
        "id": "py-2", "action": "messages.sendGroup",
        "to": ["+43 690 876-54321", ANNA, BEN], "body": "Hello all",
        "sim": 2, "requestId": "news-1",
    }
    assert [(m["requestId"], m["to"], m["sim"], m["group"]) for m in server.sent] == [
        ("news-1.1", BEN, 2, "news-1"), ("news-1.2", ANNA, 2, "news-1"),
    ]
    assert client.wait_for_result("news-1.2", 5)["status"] == "sent"
    assert client.message_status("news-1.1")["to"] == BEN
    with pytest.raises(ApiError) as raised:
        client.message_status("news-1")
    assert raised.value.code == "not_found"


def test_send_group_creates_a_request_id(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    result = client.send_group([BEN], "one")
    request_id = result["requestId"]
    assert len(request_id) == 32 and int(request_id, 16) >= 0
    assert result["messages"][0]["requestId"] == request_id + ".1"


def test_send_group_repeats_only_once(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    first = client.send_group([BEN, ANNA], "Same", request_id="news-2")
    again = client.send_group(["0690/876.54.321", ANNA], "Same", request_id="news-2")
    assert again == first
    assert len(server.sent) == 2
    assert client.wait_for_result("news-2.1", 5)["status"] == "sent"
    later = client.send_group([ANNA, BEN], "Same", request_id="news-2")
    assert later["messages"][0]["status"] in ("sent", "delivered")
    assert later["status"] in ("queued", "sent", "delivered")
    assert [m["requestId"] for m in later["messages"]] == ["news-2.1", "news-2.2"]
    for to, body in ([BEN, ANNA], "Other"), ([BEN], "Same"):
        with pytest.raises(ApiError) as raised:
            client.send_group(to, body, request_id="news-2")
        assert raised.value.code == "invalid_request"
    assert len(server.sent) == 2


def test_send_group_refuses_the_whole_group(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    for to, body, code in [
        ([BEN, "abc"], "Hello", "invalid_number"),
        ([BEN, ANNA], "x" * 1531, "message_too_long"),
        ([BEN, ANNA, "+43123456789"], "x" * 1000, "invalid_request"),
    ]:
        with pytest.raises(ApiError) as raised:
            client.send_group(to, body)
        assert raised.value.code == code
    assert server.sent == []


def test_send_group_goes_on_after_a_refused_member(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.refuse_numbers.add(ANNA)
    result = client.send_group([BEN, ANNA], "Hello", request_id="news-3")
    assert [m["status"] for m in result["messages"]] == ["queued", "failed"]
    assert result["status"] == "queued"
    assert client.wait_for_result("news-3.1", 5)["status"] == "sent"
    assert client.message_status("news-3.2")["status"] == "failed"
    server.refuse_numbers.add(BEN)
    assert client.send_group([BEN, ANNA], "Hello", request_id="news-4")["status"] == "failed"


@pytest.mark.parametrize("to, body, request_id", [
    ("+43123", "Hello", None),
    ([], "Hello", None),
    ([BEN, ""], "Hello", None),
    ([BEN, 5], "Hello", None),
    ([BEN], "", None),
    ([BEN], "Hello", "x" * 62),
    ([BEN], "Hello", "has space"),
    ([f"+4369900000{i:02d}" for i in range(21)], "Hello", None),
])
def test_send_group_refuses_bad_values(
    client: SmsDockClient, to: Any, body: str, request_id: str | None
) -> None:
    with pytest.raises(ValueError):
        client.send_group(to, body, request_id=request_id)


def test_repeat_after_a_failure_does_not_send_again(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.fail_numbers.add("+43123456789")
    first = client.send("+43123456789", "Hello", request_id="r-1")
    assert first["status"] == "queued"
    assert client.wait_for_result("r-1", 5)["status"] == "failed"
    server.fail_numbers.clear()
    again = client.send("+43123456789", "Hello", request_id="r-1")
    assert again == {**first, "status": "failed"}
    assert len(server.sent) == 1
    # A new attempt needs a new request id.
    retry = client.send("+43123456789", "Hello", request_id="r-2")
    assert client.wait_for_result(retry["requestId"], 5)["status"] == "sent"
    assert len(server.sent) == 2


def test_request_ids_outlive_a_restart_of_the_app(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.send(BEN, "Hello", request_id="r-1")
    assert client.wait_for_result("r-1", 5)["status"] == "sent"
    server.restart()
    assert client.send(BEN, "Hello", request_id="r-1")["status"] == "sent"
    assert len(server.sent) == 1


def test_status_of_a_long_message(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(send_delay=1.0)
    client = client_factory(server)
    result = client.send(BEN, "x" * 400)
    assert (result["parts"], result["status"]) == (3, "queued")
    assert wait_until(
        lambda: server.outbox[result["requestId"]]["status"] == "sending"
    )
    entry = client.message_status(result["requestId"])
    assert (entry["status"], entry["partsSent"]) == ("sending", 1)
    # Some parts are left: this is not the result yet.
    entry = client.wait_for_result(result["requestId"], 5)
    assert (entry["status"], entry["partsSent"]) == ("sent", 3)


@pytest.mark.parametrize("arguments", [
    ("", "text"),
    ("   ", "text"),
    (BEN, ""),
    (None, "text"),
    (BEN, None),
])
def test_send_refuses_empty_values(
    server: FakeSmsDock, client: SmsDockClient, arguments: Any
) -> None:
    with pytest.raises(ValueError):
        client.send(*arguments)
    assert server.sent == []


@pytest.mark.parametrize("request_id", ["", "a b", "ä", "x" * 65, "a/b"])
def test_send_refuses_malformed_request_ids(
    server: FakeSmsDock, client: SmsDockClient, request_id: str
) -> None:
    with pytest.raises(ValueError, match="request_id"):
        client.send(BEN, "text", request_id=request_id)
    assert server.sent == []


@pytest.mark.parametrize("to, body, sim, code", [
    ("not a number", "text", None, "invalid_number"),
    (BEN, "x" * 1531, None, "message_too_long"),
    (BEN, "ü" * 671, None, "message_too_long"),
    (BEN, "text", 7, "sim_unavailable"),
])
def test_send_errors(
    server: FakeSmsDock, client: SmsDockClient,
    to: str, body: str, sim: Any, code: str,
) -> None:
    with pytest.raises(ApiError) as raised:
        client.send(to, body, sim=sim)
    assert raised.value.code == code
    assert server.sent == []


def test_longest_message(server: FakeSmsDock, client: SmsDockClient) -> None:
    assert client.send(BEN, "x" * 1530)["parts"] == 10
    assert client.send(BEN, "ü" * 670)["parts"] == 10


def test_error_details(server: FakeSmsDock, client: SmsDockClient) -> None:
    server.fail_next("messages.send", "permission_missing",
                     "The permission to send SMS is missing.",
                     permission="sendSms")
    with pytest.raises(ApiError) as raised:
        client.send(BEN, "text")
    assert raised.value.code == "permission_missing"
    assert raised.value.message == "The permission to send SMS is missing."
    assert raised.value.permission == "sendSms"
    assert raised.value.retry_after is None
    assert raised.value.details == {"permission": "sendSms"}

    server.fail_next("messages.send", "quota_exceeded")
    with pytest.raises(ApiError) as raised:
        client.send(BEN, "text")
    assert raised.value.code == "quota_exceeded"
    assert client.status()


def test_message_status(server: FakeSmsDock, client: SmsDockClient) -> None:
    request_id = client.send(BEN, "x" * 200)["requestId"]
    assert wait_until(lambda: server.outbox[request_id]["status"] == "sent")
    entry = client.message_status(request_id)
    assert entry["status"] == "sent"
    assert entry["parts"] == 2 and entry["partsSent"] == 2
    assert entry["source"] == "api"
    assert entry["errorCode"] is None
    assert client.get_message(entry["messageId"])["requestId"] == request_id
    with pytest.raises(ApiError) as raised:
        client.message_status("unknown")
    assert raised.value.code == "not_found"


def test_wait_for_result_by_polling(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(send_delay=0.2)
    client = client_factory(server)
    request_id = client.send(BEN, "text")["requestId"]
    entry = client.wait_for_result(request_id, 5, poll_interval=0.05)
    assert entry["status"] == "sent"
    assert entry["requestId"] == request_id
    assert client.subscription is None
    assert actions(server).count("messages.status") >= 2


def test_wait_for_result_wakes_up_on_the_event(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(send_delay=2.0)
    client = client_factory(server)
    client.subscribe(["sms.queued", "sms.sent", "sms.failed"])
    request_id = client.send(BEN, "text")["requestId"]
    started = time.monotonic()
    entry = client.wait_for_result(request_id, 10, poll_interval=8)
    # The client asks after 0.1, 0.3, 0.7, 1.5 and 3.1 seconds. The
    # message is sent after 2 seconds; only the event can tell that early.
    assert 1.9 < time.monotonic() - started < 2.9
    assert entry["status"] == "sent"
    # The events are still there for the application.
    names = [event["event"] for event in client.events(timeout=0.05)]
    assert names == ["sms.queued", "sms.sent"]


def test_wait_for_result_failed(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.fail_numbers.add("+43123456")
    client.subscribe("sms.failed")
    request_id = client.send("+43123456", "text")["requestId"]
    entry = client.wait_for_result(request_id, 5)
    assert entry["status"] == "failed"
    assert entry["errorCode"] == 1
    event = client.wait_for_event("sms.failed", 1)
    assert event["data"]["requestId"] == request_id
    assert event["data"]["reason"] == "generic_failure"


def test_wait_for_result_delivered(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(send_delay=0.1, delivery_reports=True)
    client = client_factory(server)
    client.subscribe("*")
    request_id = client.send(BEN, "text")["requestId"]
    entry = client.wait_for_result(request_id, 5, delivered=True, poll_interval=0.05)
    assert entry["status"] == "delivered"
    event = client.wait_for_event("sms.delivered", 1)
    message = client.get_message(entry["messageId"])
    assert event["data"] == {
        "requestId": request_id, "id": entry["messageId"],
        "conversationId": message["conversationId"], "to": BEN,
    }
    assert message["status"] == "delivered"


def test_wait_for_result_times_out(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(send_delay=30)
    client = client_factory(server)
    request_id = client.send(BEN, "text")["requestId"]
    with pytest.raises(TimeoutError, match="queued"):
        client.wait_for_result(request_id, 0.3, poll_interval=0.1)
    assert actions(server).count("messages.status") >= 2
    with pytest.raises(ValueError):
        client.wait_for_result(request_id, 1, poll_interval=0)
    assert client.ping()


def test_delete_message(server: FakeSmsDock, client: SmsDockClient) -> None:
    newest = client.messages(limit=1)["items"][0]
    assert client.delete_message(newest["id"]) == {"deleted": 1, "mode": "hidden"}
    assert last_request(server, "messages.delete") == {
        "id": "py-3", "action": "messages.delete", "messageId": newest["id"],
    }
    assert len(server.messages) == 28
    with pytest.raises(ApiError) as raised:
        client.delete_message(newest["id"])
    assert raised.value.code == "not_found"
    with pytest.raises(TypeError):
        client.delete_message("12")  # type: ignore[arg-type]


def test_delete_conversation(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.delete_mode = "provider"
    anna = client.conversations(q="anna")["items"][0]
    assert client.delete_conversation(anna["id"]) == {
        "deleted": 25, "mode": "provider",
    }
    assert last_request(server, "conversations.delete") == {
        "id": "py-3", "action": "conversations.delete",
        "conversationId": anna["id"],
    }
    assert [c["address"] for c in client.conversations()["items"]] == [BEN, "A1"]
    with pytest.raises(ApiError) as raised:
        client.delete_conversation(anna["id"])
    assert raised.value.code == "not_found"


def test_delete_sends_an_event(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("sms.deleted")
    newest = client.messages(limit=1)["items"][0]
    client.delete_message(newest["id"])
    event = client.wait_for_event("sms.deleted", 2)
    assert event["data"] == {
        "id": newest["id"], "conversationId": newest["conversationId"],
        "address": newest["address"], "mode": "hidden",
    }
    client.delete_conversation(newest["conversationId"])
    event = client.wait_for_event("sms.deleted", 2)
    assert event["data"] == {
        "id": None, "conversationId": newest["conversationId"],
        "address": ANNA, "mode": "hidden",
    }


def test_mark_read(server: FakeSmsDock, client: SmsDockClient) -> None:
    server.delete_mode = "provider"
    anna = client.conversations(q="anna")["items"][0]
    assert anna["unreadCount"] == 3
    assert client.mark_read(anna["id"]) == {"ok": True}
    assert last_request(server, "conversations.markRead") == {
        "id": "py-3", "action": "conversations.markRead",
        "conversationId": anna["id"],
    }
    assert client.conversations(q="anna")["items"][0]["unreadCount"] == 0
    assert client.messages(address=ANNA, unread=True)["items"] == []
    with pytest.raises(ApiError) as raised:
        client.mark_read(1)
    assert raised.value.code == "not_found"
    with pytest.raises(TypeError):
        client.mark_read("179")  # type: ignore[arg-type]


def test_mark_read_needs_the_default_sms_app(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    assert client.status()["defaultSmsApp"] is False
    anna = client.conversations(q="anna")["items"][0]
    with pytest.raises(ApiError) as raised:
        client.mark_read(anna["id"])
    assert raised.value.code == "not_default_app"
    assert client.conversations(q="anna")["items"][0]["unreadCount"] == 3


def test_contacts(server: FakeSmsDock, client: SmsDockClient) -> None:
    server.add_contact("Zoe Example", "+43660111222")
    page = client.contacts()
    assert [c["name"] for c in page["items"]] == [
        "Anna Example", "Ben Example", "Zoe Example",
    ]
    assert page["items"][1]["numbers"] == [{"number": BEN, "label": "Work"}]
    assert [c["name"] for c in client.contacts(q="zoe")["items"]] == ["Zoe Example"]
    assert [c["name"] for c in client.contacts(q="5678")["items"]] == ["Anna Example"]
    first = client.contacts(limit=2)
    assert first["nextCursor"] is not None
    second = client.contacts(limit=2, cursor=first["nextCursor"])
    assert [c["name"] for c in second["items"]] == ["Zoe Example"]
    assert [c["name"] for c in client.iter_contacts(page_size=1, max_items=2)] == [
        "Anna Example", "Ben Example",
    ]


def test_generic_request(server: FakeSmsDock, client: SmsDockClient) -> None:
    assert client.request("messages.list", limit=1, direction="out")["items"]
    with pytest.raises(ApiError) as raised:
        client.request("messages.archive")
    assert raised.value.code == "unknown_action"


def test_the_request_id_belongs_to_the_client(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    with pytest.raises(ValueError, match="id"):
        client.request("messages.get", id=1)
    with pytest.raises(ValueError, match="id"):
        client.send_request("ping", id="mine")
    assert actions(server) == ["authenticate"]


def test_answers_may_be_larger_than_64_kib(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    for index in range(200):
        server.add_message("+436601234567", f"{index} " + "x" * 1500)
    page = client.messages(address="+436601234567", limit=200)
    assert len(page["items"]) == 200
    assert len(json.dumps(page)) > 300_000


def test_refuses_requests_over_64_kib(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    with pytest.raises(ValueError, match="64 KiB"):
        client.send(BEN, "x" * 70_000)
    assert actions(server) == ["authenticate"]
    assert client.ping()


def test_close_code_1009(
    server: FakeSmsDock, client: SmsDockClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(smsdock.client, "MAX_FRAME_BYTES", 1024 * 1024)
    with pytest.raises(ProtocolError, match="64 KiB"):
        client.send(BEN, "x" * 70_000)
    # The same frame would fail again: no second connection.
    assert client.connection_count == 1
    assert not client.connected


# ----------------------------------------------------------------------
# Events
# ----------------------------------------------------------------------


def test_subscribe_and_unsubscribe(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    assert client.subscription is None
    assert client.subscribe("sms.received") == {
        "events": ["sms.received"], "address": None,
    }
    # subscribe adds names; its address replaces the filter.
    assert client.subscribe(["sms.sent", "sms.failed"], address=ANNA) == {
        "events": ["sms.received", "sms.sent", "sms.failed"], "address": ANNA,
    }
    assert client.subscription == {
        "events": ["sms.received", "sms.sent", "sms.failed"], "address": ANNA,
    }
    assert client.unsubscribe(["sms.sent"]) == {
        "events": ["sms.received", "sms.failed"], "address": ANNA,
    }
    # Without address the filter is removed.
    assert client.subscribe("sms.queued") == {
        "events": ["sms.received", "sms.queued", "sms.failed"], "address": None,
    }
    assert "address" not in last_request(server, "subscribe")
    # When no name is left, the filter is removed too.
    client.subscribe("sms.queued", address=BEN)
    assert client.unsubscribe("*") == {"events": [], "address": None}
    assert client.subscription == {"events": [], "address": None}
    # In the answer * is replaced by the names it stands for.
    assert client.subscribe("*") == {
        "events": list(smsdock.EVENT_NAMES), "address": None,
    }
    assert last_request(server, "subscribe") == {
        "id": "py-8", "action": "subscribe", "events": ["*"],
    }
    assert client.unsubscribe(["sms.deleted", "conversations.changed"])[
        "events"
    ] == ["sms.received", "sms.queued", "sms.sent", "sms.delivered", "sms.failed"]
    with pytest.raises(ValueError):
        client.subscribe([])
    with pytest.raises(ApiError) as raised:
        client.subscribe("sms.unknown")
    assert raised.value.code == "invalid_request"


def test_a_new_connection_gets_no_events(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.receive_sms(ANNA, "Nobody listens")
    assert list(client.events(timeout=0.2)) == []


def test_incoming_sms(server: FakeSmsDock, client: SmsDockClient) -> None:
    client.subscribe("sms.received")
    stored = server.receive_sms(ANNA, "STATUS")
    event = client.wait_for_event("sms.received", 2)
    assert event["event"] == "sms.received"
    assert event["seq"] == 1
    message = event["data"]["message"]
    assert message == stored
    assert message["address"] == ANNA
    assert message["contact"] == "Anna Example"
    assert message["direction"] == "in"
    assert message["read"] is False
    assert client.get_message(message["id"])["body"] == "STATUS"


def test_react_to_an_incoming_sms(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("sms.received")
    server.receive_sms(ANNA, "STATUS")
    for event in client.events(timeout=2):
        sender = event["data"]["message"]["address"]
        client.send(sender, "All systems running.")
        break
    assert [(m["to"], m["body"]) for m in server.sent] == [
        (ANNA, "All systems running."),
    ]


def test_events_between_request_and_response_are_queued(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("sms.received")
    server.before_next_response(event_frame(1, "first"), event_frame(2, "second"))
    status = client.status()
    assert status["deleteMode"] == "hidden"
    assert client.pending_events == 2
    server.before_next_response(event_frame(3, "third"))
    assert client.ping()["time"] > 0
    assert client.pending_events == 3
    events = list(client.events(timeout=0.1))
    assert [event["seq"] for event in events] == [1, 2, 3]
    assert [e["data"]["message"]["body"] for e in events] == [
        "first", "second", "third",
    ]
    assert client.pending_events == 0


def test_events_end_after_the_timeout(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("*")
    started = time.monotonic()
    assert list(client.events(timeout=0.2)) == []
    assert 0.15 < time.monotonic() - started < 2
    assert client.connected


def test_events_arrive_in_order(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("sms.received")
    for index in range(5):
        server.receive_sms(ANNA, f"Message {index}")
    events = list(client.events(timeout=0.3))
    assert [e["data"]["message"]["body"] for e in events] == [
        f"Message {index}" for index in range(5)
    ]
    assert [e["seq"] for e in events] == [1, 2, 3, 4, 5]


def test_wait_for_event_keeps_the_others(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("*")
    server.receive_sms(ANNA, "one")
    server.emit("conversations.changed", {})
    server.receive_sms(BEN, "two")
    changed = client.wait_for_event("conversations.changed", 2)
    assert changed["data"] == {}
    assert client.pending_events == 1
    from_ben = client.wait_for_event(
        "sms.received", 2,
        predicate=lambda e: e["data"]["message"]["address"] == BEN,
    )
    assert from_ben["data"]["message"]["body"] == "two"
    assert client.pending_events == 1
    assert client.wait_for_event(["sms.sent", "sms.received"], 2)[
        "data"]["message"]["body"] == "one"
    assert client.pending_events == 0


def test_wait_for_event_times_out(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("*")
    server.emit("conversations.changed", {})
    with pytest.raises(TimeoutError, match="sms.received"):
        client.wait_for_event("sms.received", 0.2)
    assert client.pending_events == 1
    assert client.connected


def test_address_filter(server: FakeSmsDock, client: SmsDockClient) -> None:
    client.subscribe("sms.received", address=BEN)
    server.receive_sms(ANNA, "from Anna")
    server.receive_sms(BEN, "from Ben")
    events = list(client.events(timeout=0.3))
    assert [e["data"]["message"]["body"] for e in events] == ["from Ben"]


def test_address_filter_compares_like_the_phone(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    # A number without country code matches every number it ends.
    client.subscribe("sms.received", address="0690 876 54 321")
    server.receive_sms(BEN, "international")
    server.receive_sms("0043 690 87654321", "with 00")
    server.receive_sms("+49 690 87654321", "another country")
    server.receive_sms("0690 876 54 322", "another number")
    events = list(client.events(timeout=0.3))
    assert [e["data"]["message"]["body"] for e in events] == [
        "international", "with 00", "another country",
    ]
    # Two numbers with country code are the same or they are not.
    client.subscribe("sms.received", address="0043 690 876 54 321")
    server.receive_sms(BEN, "international")
    server.receive_sms("0690/87654321", "national")
    server.receive_sms("+49 690 87654321", "another country")
    events = list(client.events(timeout=0.3))
    assert [e["data"]["message"]["body"] for e in events] == [
        "international", "national",
    ]
    client.subscribe("sms.received", address="mybank")
    server.receive_sms("MyBank", "Your code is 1234")
    server.receive_sms("MyBank2", "other sender")
    events = list(client.events(timeout=0.3))
    assert [e["data"]["message"]["body"] for e in events] == ["Your code is 1234"]


def test_address_filter_on_all_events_that_name_a_peer(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(delivery_reports=True)
    server.fail_numbers.add("+43123456789")
    client = client_factory(server)
    client.subscribe("*", address=BEN)
    for number in (ANNA, BEN, "+43123456789"):
        client.wait_for_result(client.send(number, "text")["requestId"], 5)
    assert wait_until(lambda: all(
        entry["status"] in ("delivered", "failed")
        for entry in server.outbox.values()
    ))
    ben = client.messages(address=BEN, limit=1)["items"][0]
    anna = client.messages(address=ANNA, limit=1)["items"][0]
    client.delete_message(anna["id"])
    client.delete_message(ben["id"])
    # Events that name no peer always pass.
    server.emit("conversations.changed", {})
    server.emit("sms.deleted", {
        "id": None, "conversationId": 1, "address": None, "mode": "hidden",
    })
    events = list(client.events(timeout=0.3))
    assert [event["event"] for event in events] == [
        "sms.queued", "sms.sent", "sms.delivered", "sms.deleted",
        "conversations.changed", "sms.deleted",
    ]
    assert [e["data"].get("to", e["data"].get("address")) for e in events] == [
        BEN, BEN, BEN, BEN, None, None,
    ]


def test_events_of_a_sent_message(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(delivery_reports=True)
    server.fail_numbers.add("+43123456789")
    client = client_factory(server)
    client.subscribe(["sms.queued", "sms.sent", "sms.delivered", "sms.failed"])
    good = client.send(BEN, "text")["requestId"]
    entry = client.wait_for_result(good, 5, delivered=True)
    conversation = client.get_message(entry["messageId"])["conversationId"]
    queued = client.wait_for_event("sms.queued", 1)
    assert queued["data"] == {
        "requestId": good, "to": BEN, "parts": 1, "sim": 1, "source": "api",
    }
    sent = client.wait_for_event("sms.sent", 1)
    assert set(sent["data"]) == {"requestId", "conversationId", "to", "message"}
    assert sent["data"]["conversationId"] == conversation
    assert sent["data"]["to"] == BEN
    assert sent["data"]["message"]["id"] == entry["messageId"]

    bad = client.send("+43123456789", "text")["requestId"]
    assert client.wait_for_result(bad, 5)["status"] == "failed"
    failed = client.wait_for_event("sms.failed", 1)
    # The phone has not stored the message: no id, no conversation.
    assert failed["data"] == {
        "requestId": bad, "id": None, "conversationId": None,
        "to": "+43123456789", "errorCode": 1, "reason": "generic_failure",
    }


def test_full_event_queue_drops_the_oldest(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    client = client_factory(server, event_queue_size=2)
    server.before_next_response(*(event_frame(n) for n in (1, 2, 3, 4)))
    client.ping()
    assert client.pending_events == 2
    assert client.dropped_events == 2
    assert [e["seq"] for e in client.events(timeout=0.05)] == [3, 4]


def test_responses_in_another_order(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.reverse_next_responses(3)
    first = client.send_request("messages.list", limit=1)
    second = client.send_request("status")
    third = client.send_request("contacts.list")
    assert len({first, second, third}) == 3
    assert "deleteMode" in client.wait_for_response(second, 2)
    assert client.wait_for_response(first, 2)["items"][0]["body"] == "Anna 24"
    assert len(client.wait_for_response(third, 2)["items"]) == 2
    with pytest.raises(ValueError):
        client.wait_for_response(first)
    assert client.ping()


def test_response_error_of_a_pipelined_request(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.reverse_next_responses(2)
    missing = client.send_request("messages.get", messageId=1)
    fine = client.send_request("ping")
    with pytest.raises(ApiError) as raised:
        client.wait_for_response(missing, 2)
    assert raised.value.code == "not_found"
    assert client.wait_for_response(fine, 2)["time"] > 0


def test_stale_responses_are_ignored(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.before_next_response(
        {"id": "py-0", "ok": True, "result": {"stale": True}},
        {"id": 77, "ok": False,
         "error": {"code": "internal_error", "message": "stale"}},
    )
    assert client.status()["deleteMode"] == "hidden"
    assert client.ping()["time"] > 0


def test_response_without_id_answers_the_open_request(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.before_next_response({
        "id": None, "ok": False,
        "error": {"code": "invalid_json", "message": "Not a JSON object."},
    })
    with pytest.raises(ApiError) as raised:
        client.status()
    assert raised.value.code == "invalid_json"
    # The late real response is dropped; the next request works.
    assert client.ping()["time"] > 0


@pytest.mark.parametrize("frame, message", [
    ("this is not JSON", "invalid JSON"),
    ("[1, 2, 3]", "not a JSON object"),
])
def test_malformed_frames(
    server: FakeSmsDock, client: SmsDockClient, frame: str, message: str
) -> None:
    server.before_next_response(frame)
    with pytest.raises(ProtocolError, match=message):
        client.status()


def test_malformed_response(server: FakeSmsDock, client: SmsDockClient) -> None:
    server.before_next_response({"id": "py-2", "ok": False})
    with pytest.raises(ProtocolError, match="Malformed"):
        client.status()


# ----------------------------------------------------------------------
# Reconnect
# ----------------------------------------------------------------------


def test_reconnects_and_logs_in_again(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    assert client.ping()
    server.drop_connections()
    assert client.status()["deleteMode"] == "hidden"
    assert client.connection_count == 2
    assert server.login_count == 2
    assert actions(server) == ["authenticate", "ping", "authenticate", "status"]


def test_reconnects_after_a_close_frame(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.drop_connections(1001, "server is stopping")
    assert client.ping()
    server.drop_connections(4408, "no answer to pings")
    assert client.ping()
    server.drop_connections(4429, "too many requests")
    assert client.ping()
    assert client.connection_count == 4


def test_request_lost_before_it_was_handled(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.drop_on("status", "before")
    server.drop_on("status", "before")
    assert client.status()["deleteMode"] == "hidden"
    assert actions(server).count("status") == 3
    assert client.connection_count == 3


def test_restores_the_subscription(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe(["sms.received", "sms.failed"], address=BEN)
    server.drop_connections()
    assert client.ping()
    assert actions(server) == [
        "authenticate", "subscribe", "authenticate", "subscribe", "ping",
    ]
    restored = server.requests[3]
    assert restored["events"] == ["sms.received", "sms.failed"]
    assert restored["address"] == BEN
    server.receive_sms(ANNA, "filtered out")
    server.receive_sms(BEN, "after the reconnect")
    event = client.wait_for_event("sms.received", 2)
    assert event["data"]["message"]["body"] == "after the reconnect"
    assert client.pending_events == 0


def test_does_not_restore_an_empty_subscription(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("sms.received")
    client.unsubscribe("sms.received")
    server.drop_connections()
    assert client.ping()
    assert actions(server).count("subscribe") == 1


def test_waiting_for_events_reconnects(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("sms.received")
    server.drop_connections()

    def arrive() -> None:
        if wait_until(lambda: actions(server).count("subscribe") == 2):
            server.receive_sms(ANNA, "after the reconnect")

    thread = threading.Thread(target=arrive)
    thread.start()
    try:
        events = []
        for event in client.events(timeout=5):
            events.append(event)
            break
    finally:
        thread.join()
    assert events[0]["data"]["message"]["body"] == "after the reconnect"
    assert client.connection_count == 2


def test_send_is_not_repeated_after_a_lost_response(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.drop_on("messages.send", "after")
    result = client.send(BEN, "Only once")
    # The repeat tells the status of now; the message may be out already.
    assert result["status"] in ("queued", "sent")
    requests = [r for r in server.requests if r["action"] == "messages.send"]
    assert len(requests) == 2
    assert requests[0]["requestId"] == requests[1]["requestId"] == result["requestId"]
    assert requests[0]["id"] != requests[1]["id"]
    assert client.connection_count == 2
    assert client.wait_for_result(result["requestId"], 5)["status"] == "sent"
    # The radio was used once although the request arrived twice.
    assert [m["body"] for m in server.sent] == ["Only once"]
    sent = [m for m in server.messages if m["body"] == "Only once"]
    assert len(sent) == 1


def test_wait_for_result_survives_a_reconnect(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(send_delay=0.3)
    client = client_factory(server)
    client.subscribe(["sms.sent", "sms.failed"])
    request_id = client.send(BEN, "text")["requestId"]
    server.drop_connections()
    entry = client.wait_for_result(request_id, 5, poll_interval=0.1)
    assert entry["status"] == "sent"
    assert len(server.sent) == 1


def test_gives_up_after_the_last_attempt(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory()
    client = client_factory(server, reconnect_attempts=3, connect_timeout=2.0)
    server.stop()
    with pytest.raises(ConnectionInterrupted, match="'status'") as raised:
        client.status()
    assert isinstance(raised.value.__cause__, OSError)
    assert not client.connected


def test_backoff_is_bounded(
    server_factory: ServerFactory,
    client_factory: ClientFactory,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    server = server_factory()
    client = client_factory(
        server, reconnect_attempts=6, reconnect_delay=1.0,
        max_reconnect_delay=10.0, connect_timeout=2.0,
    )
    server.stop()
    delays: list[float] = []
    monkeypatch.setattr(smsdock.client.time, "sleep", delays.append)
    with pytest.raises(ConnectionInterrupted):
        client.status()
    assert delays == [1.0, 2.0, 4.0, 8.0, 10.0, 10.0]


def test_no_retry_on_request(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.drop_on("status", "before")
    with pytest.raises(ConnectionInterrupted):
        client.request("status", retry=False)
    assert client.connection_count == 1
    assert client.status()
    assert client.connection_count == 2


def test_zero_reconnect_attempts(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    client = client_factory(server, reconnect_attempts=0)
    server.drop_on("status", "before")
    with pytest.raises(ConnectionInterrupted):
        client.status()
    assert client.connection_count == 1


def test_pipelined_requests_do_not_reconnect(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.drop_on("status", "after")
    request_id = client.send_request("status")
    with pytest.raises(ConnectionInterrupted):
        client.wait_for_response(request_id, 2)
    assert client.connection_count == 1


@pytest.mark.parametrize("code, kind, error_code", [
    (4410, AuthenticationError, "not_authenticated"),
    (4401, AuthenticationError, "not_authenticated"),
    (4403, LicenceRequiredError, "licence_required"),
])
def test_close_codes_that_forbid_a_retry(
    server: FakeSmsDock, client: SmsDockClient,
    code: int, kind: type, error_code: str,
) -> None:
    server.drop_connections(code)
    with pytest.raises(kind) as raised:
        client.status()
    assert raised.value.code == error_code
    assert raised.value.details["closeCode"] == code
    # No new login with a password that may be wrong by now.
    assert server.login_count == 1
    assert client.connection_count == 1


def test_password_changed_while_listening(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("sms.received")
    timer = threading.Timer(0.2, server.drop_connections, (4410, "password changed"))
    timer.start()
    try:
        with pytest.raises(AuthenticationError, match="password changed"):
            list(client.events(timeout=5))
    finally:
        timer.join()
    assert server.login_count == 1


def test_protocol_error_close_code(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    server.drop_connections(4400, "binary frame")
    with pytest.raises(ProtocolError, match="binary frame"):
        client.status()
    assert client.connection_count == 1


def test_request_timeout_reconnects(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    client = client_factory(server, timeout=0.3, reconnect_attempts=1)
    server.ignore_next("status")
    assert client.status()["deleteMode"] == "hidden"
    assert client.connection_count == 2
    server.ignore_next("status", 2)
    with pytest.raises(ConnectionInterrupted, match="No response"):
        client.status()
    assert client.connection_count == 3


def test_frames_are_compact_json(
    server: FakeSmsDock, client: SmsDockClient,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    frames: list[str] = []
    websocket = client._websocket
    original = websocket.send
    monkeypatch.setattr(
        websocket, "send", lambda frame: (frames.append(frame), original(frame))[1]
    )
    client.send(BEN, "Grüße", request_id="r1")
    assert frames == [(
        '{"id":"py-2","action":"messages.send","to":"+4369087654321",'
        '"body":"Grüße","requestId":"r1"}'
    )]
    assert json.loads(frames[0])["body"] == "Grüße"


# ----------------------------------------------------------------------
# Epoch and sequence numbers
# ----------------------------------------------------------------------


def test_login_tells_epoch_and_sequence_number(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    server.receive_sms(ANNA, "one")
    server.receive_sms(ANNA, "two")
    client = client_factory(server, connect=False)
    assert client.epoch is None
    assert client.latest_seq is None
    client.connect()
    assert client.epoch == server.epoch
    assert client.latest_seq == 2
    assert client.server_restarted is False
    assert client.missed_events == 0


def test_latest_seq_follows_the_events(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("conversations.changed")
    server.receive_sms(ANNA, "not subscribed")
    server.emit("conversations.changed", {})
    assert client.wait_for_event("conversations.changed", 2)["seq"] == 2
    assert client.latest_seq == 2
    # Events read while a request waits for its response count as well.
    server.before_next_response(event_frame(7))
    client.ping()
    assert client.latest_seq == 7


def test_counts_the_events_missed_while_disconnected(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    calls: list[Any] = []
    client = client_factory(
        server,
        on_reconnect=lambda c: calls.append(
            (c.missed_events, c.server_restarted, c.connected, c.subscription)
        ),
    )
    assert calls == []
    client.subscribe("sms.received")
    server.receive_sms(ANNA, "seen")
    assert client.wait_for_event("sms.received", 2)["seq"] == 1
    server.drop_connections()
    assert wait_until(lambda: server.client_count == 0)
    for body in ("lost one", "lost two", "lost three"):
        server.receive_sms(ANNA, body)
    assert client.ping()
    assert client.missed_events == 3
    assert client.server_restarted is False
    assert client.latest_seq == 4
    # The hook runs once the subscription is in force again.
    assert calls == [
        (3, False, True, {"events": ["sms.received"], "address": None}),
    ]
    server.drop_connections()
    assert client.ping()
    assert client.missed_events == 0
    assert len(calls) == 2


def test_notices_a_restart_of_the_app(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    client.subscribe("sms.received")
    for index in range(5):
        server.receive_sms(ANNA, f"before {index}")
    assert len(list(client.events(timeout=0.3))) == 5
    first = client.epoch
    server.restart()
    server.receive_sms(ANNA, "lost")
    assert client.ping()
    assert client.server_restarted is True
    assert client.missed_events is None
    assert client.epoch == server.epoch != first
    assert client.latest_seq == 1
    server.receive_sms(ANNA, "after")
    assert client.wait_for_event("sms.received", 2)["seq"] == 2
    server.drop_connections()
    assert client.ping()
    assert client.server_restarted is False
    assert client.missed_events == 0


def test_hook_reads_what_was_missed(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    seen: list[str] = []
    newest = [0]

    def handle(message: Any) -> None:
        seen.append(message["body"])
        newest[0] = max(newest[0], message["timestamp"])

    def catch_up(dock: SmsDockClient) -> None:
        missed = list(dock.iter_messages(direction="in", since=newest[0] + 1))
        for message in reversed(missed):
            handle(message)

    client = client_factory(server, on_reconnect=catch_up)
    client.subscribe("sms.received")
    server.receive_sms(ANNA, "first")
    handle(client.wait_for_event("sms.received", 2)["data"]["message"])
    server.drop_connections()
    assert wait_until(lambda: server.client_count == 0)
    time.sleep(0.01)
    server.receive_sms(ANNA, "while away 1")
    time.sleep(0.01)
    server.receive_sms(BEN, "while away 2")

    def arrive() -> None:
        if wait_until(lambda: actions(server).count("subscribe") == 2):
            time.sleep(0.05)
            server.receive_sms(ANNA, "live again")

    thread = threading.Thread(target=arrive)
    thread.start()
    try:
        for event in client.events(timeout=5):
            handle(event["data"]["message"])
            break
    finally:
        thread.join()
    assert seen == ["first", "while away 1", "while away 2", "live again"]


def test_hook_can_be_set_later(
    server: FakeSmsDock, client: SmsDockClient
) -> None:
    calls: list[int] = []
    client.on_reconnect = lambda c: calls.append(c.connection_count)
    server.drop_connections()
    client.ping()
    assert calls == [2]


def test_login_without_epoch_is_refused(
    server: FakeSmsDock, client_factory: ClientFactory
) -> None:
    client = client_factory(server, connect=False)
    server.before_next_response({
        "id": "py-1", "ok": True,
        "result": {"protocolVersion": 1,
                   "app": {"name": "SMS Dock", "version": "0.9.0"}},
    })
    with pytest.raises(ProtocolError, match="epoch"):
        client.connect()
    assert not client.connected


# ----------------------------------------------------------------------
# Rate limit
# ----------------------------------------------------------------------


@pytest.fixture
def waits(monkeypatch: pytest.MonkeyPatch) -> list[float]:
    """Replace the pauses of the client by a list of their lengths."""

    pauses: list[float] = []
    monkeypatch.setattr(smsdock.client.time, "sleep", pauses.append)
    return pauses


def test_waits_and_repeats_when_rate_limited(
    server: FakeSmsDock, client: SmsDockClient, waits: list[float]
) -> None:
    server.fail_next("status", "rate_limited", "Too many requests.", retryAfter=1)
    assert client.status()["deleteMode"] == "hidden"
    assert waits == [1]
    server.fail_next("status", "rate_limited", retryAfter=1)
    server.fail_next("status", "rate_limited", retryAfter=3)
    assert client.status()["deleteMode"] == "hidden"
    assert waits == [1, 1, 3]
    assert actions(server).count("status") == 5
    assert client.connection_count == 1


def test_gives_up_when_rate_limited_again_and_again(
    server: FakeSmsDock, client: SmsDockClient, waits: list[float]
) -> None:
    for _ in range(3):
        server.fail_next("status", "rate_limited", retryAfter=1)
    with pytest.raises(ApiError) as raised:
        client.status()
    assert raised.value.code == "rate_limited"
    assert raised.value.retry_after == 1
    assert waits == [1, 1]
    assert actions(server).count("status") == 3
    assert client.ping()


def test_limit_of_messages_per_minute_is_reported_at_once(
    server: FakeSmsDock, client: SmsDockClient, waits: list[float]
) -> None:
    # The limit of 20 SMS parts per minute names no seconds to wait.
    server.fail_next(
        "messages.send", "rate_limited",
        "Too many messages per minute. Try again shortly.",
    )
    with pytest.raises(ApiError) as raised:
        client.send(BEN, "text")
    assert raised.value.code == "rate_limited"
    assert raised.value.retry_after is None
    assert waits == []
    assert server.sent == []


@pytest.mark.parametrize("options, arguments", [
    ({"rate_limit_retries": 0}, {}),
    ({"timeout": 5.0}, {"retry_after": 6}),
    ({}, {"retry": False}),
])
def test_rate_limit_without_a_repeat(
    server: FakeSmsDock, client_factory: ClientFactory, waits: list[float],
    options: Any, arguments: Any,
) -> None:
    client = client_factory(server, **options)
    server.fail_next(
        "status", "rate_limited", retryAfter=arguments.get("retry_after", 1)
    )
    with pytest.raises(ApiError) as raised:
        client.request("status", retry=arguments.get("retry", True))
    assert raised.value.code == "rate_limited"
    assert waits == []


def test_send_is_not_repeated_when_rate_limited(
    server: FakeSmsDock, client: SmsDockClient, waits: list[float]
) -> None:
    server.fail_next("messages.send", "rate_limited", retryAfter=1)
    result = client.send(BEN, "Only once")
    assert waits == [1]
    requests = [r for r in server.requests if r["action"] == "messages.send"]
    assert [r["requestId"] for r in requests] == [result["requestId"]] * 2
    assert [m["body"] for m in server.sent] == ["Only once"]


def test_stays_within_the_limit_of_the_server(
    server_factory: ServerFactory, client_factory: ClientFactory
) -> None:
    server = server_factory(requests_per_second=5)
    client = client_factory(server)
    started = time.monotonic()
    for _ in range(9):
        assert client.ping()["time"] > 0
    # The login and 4 requests fit into the first second, 5 into the next.
    assert server.refused_count >= 1
    assert time.monotonic() - started >= 0.9
    assert actions(server).count("ping") == 9
    assert client.connection_count == 1


def test_rejects_negative_rate_limit_retries() -> None:
    with pytest.raises(ValueError):
        SmsDockClient(
            URL, PASSWORD, fingerprint=FINGERPRINT, rate_limit_retries=-1
        )
