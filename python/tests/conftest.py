"""Fixtures: a fake SMS Dock server and clients connected to it.

The tests talk to the fake server over real TLS with a throwaway
certificate made by the ``openssl`` command. Without ``openssl`` (or with
``SMSDOCK_TEST_NO_TLS=1``) they fall back to plain WebSocket connections
and the TLS tests are skipped.
"""

from __future__ import annotations

import os
import sys
import time
from collections.abc import Iterator
from pathlib import Path
from typing import Any, Callable

import pytest
from websockets.sync.client import connect as websockets_connect

sys.path.insert(0, str(Path(__file__).parent))

from fake_server import FakeSmsDock, TlsFiles, make_certificate

import smsdock.client
from smsdock import SmsDockClient

PASSWORD = "secret123"
BASE_TIME = 1790672531000
ANNA = "+4369912345678"
BEN = "+4369087654321"
NO_TLS_REASON = (
    "needs the openssl command to create a throwaway certificate "
    "(not found, or SMSDOCK_TEST_NO_TLS is set)"
)


@pytest.fixture(autouse=True)
def clean_environment(monkeypatch: pytest.MonkeyPatch) -> None:
    """No test sees the password or fingerprint of the developer."""

    monkeypatch.delenv("SMSDOCK_PASSWORD", raising=False)
    monkeypatch.delenv("SMSDOCK_FINGERPRINT", raising=False)


def _certificate(directory: Path, name: str) -> TlsFiles | None:
    if os.environ.get("SMSDOCK_TEST_NO_TLS"):
        return None
    return make_certificate(directory, name)


@pytest.fixture(scope="session")
def tls_files(tmp_path_factory: pytest.TempPathFactory) -> TlsFiles | None:
    """Certificate of the fake server, or ``None`` without openssl."""

    return _certificate(tmp_path_factory.mktemp("tls"), "server")


@pytest.fixture(scope="session")
def other_tls_files(
    tmp_path_factory: pytest.TempPathFactory,
) -> TlsFiles | None:
    """A second certificate that the client does not pin."""

    return _certificate(tmp_path_factory.mktemp("tls-other"), "other")


@pytest.fixture
def need_tls(tls_files: TlsFiles | None) -> TlsFiles:
    """Skip the test when no certificate can be made."""

    if tls_files is None:
        pytest.skip(NO_TLS_REASON)
    return tls_files


def plain_connect(url: str, **options: Any) -> Any:
    """Stand-in for ``connect`` without TLS, used only without openssl."""

    options.pop("ssl", None)
    return websockets_connect(url.replace("wss://", "ws://", 1), **options)


def fill(server: FakeSmsDock) -> None:
    """Store 29 messages in three conversations."""

    server.add_contact("Anna Example", ANNA)
    server.add_contact("Ben Example", BEN, "Work")
    for index in range(25):
        incoming = index % 2 == 0
        server.add_message(
            ANNA, f"Anna {index}",
            direction="in" if incoming else "out",
            timestamp=BASE_TIME + index * 1000,
            read=index < 22,
        )
    server.add_message(BEN, "Your invoice is attached", direction="out",
                       timestamp=BASE_TIME - 5000, sim=2)
    server.add_message(BEN, "Thanks for the invoice", timestamp=BASE_TIME - 4000,
                       sim=2)
    server.add_message(BEN, "See you", timestamp=BASE_TIME - 3000, sim=2)
    server.add_message("A1", "Your bill is ready", timestamp=BASE_TIME - 9000,
                       read=False)


@pytest.fixture
def server_factory(
    tls_files: TlsFiles | None,
) -> Iterator[Callable[..., FakeSmsDock]]:
    """Return a function that starts fake servers; all stop at the end."""

    servers: list[FakeSmsDock] = []

    def start(**options: Any) -> FakeSmsDock:
        options.setdefault("tls", tls_files)
        options.setdefault("password", PASSWORD)
        server = FakeSmsDock(**options)
        fill(server)
        servers.append(server)
        return server.start()

    yield start
    for server in servers:
        server.stop()


@pytest.fixture
def server(server_factory: Callable[..., FakeSmsDock]) -> FakeSmsDock:
    """A running fake server with the data of :func:`fill`."""

    return server_factory()


@pytest.fixture
def client_factory(
    monkeypatch: pytest.MonkeyPatch,
) -> Iterator[Callable[..., SmsDockClient]]:
    """Return a function that makes clients for a server; all are closed."""

    clients: list[SmsDockClient] = []

    def make(server: FakeSmsDock, *, connect: bool = True, **options: Any) -> SmsDockClient:
        options.setdefault("reconnect_delay", 0.01)
        options.setdefault("timeout", 5.0)
        password = options.pop("password", PASSWORD)
        if server.tls is not None:
            options.setdefault("fingerprint", server.tls.fingerprint)
        else:
            monkeypatch.setattr(smsdock.client, "connect", plain_connect)
            options.setdefault("insecure", True)
        url = server.url.replace("ws://", "wss://", 1)
        client = SmsDockClient(url, password, **options)
        clients.append(client)
        if connect:
            client.connect()
        return client

    yield make
    for client in clients:
        client.close()


@pytest.fixture
def client(
    server: FakeSmsDock, client_factory: Callable[..., SmsDockClient]
) -> SmsDockClient:
    """A client that is connected and logged in."""

    return client_factory(server)


def wait_until(condition: Callable[[], bool], timeout: float = 3.0) -> bool:
    """Poll until the condition holds; return whether it did."""

    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if condition():
            return True
        time.sleep(0.01)
    return condition()
