#!/usr/bin/env python3
"""Hook for incoming SMS: call a function for every SMS that arrives.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    python listen_incoming.py wss://192.168.1.50:9443/ws

Stop with Ctrl+C.
"""

import sys
from datetime import datetime, timezone

from smsdock import SmsDockClient, SmsDockError

# Ids of the messages the hook has seen, and the time of the newest one.
seen = set()
newest = 0


def on_sms(message: dict) -> None:
    """Your hook. ``message`` is a Message object of API.md."""

    # Timestamps are milliseconds since the epoch; show local time.
    received = datetime.fromtimestamp(
        message["timestamp"] / 1000, tz=timezone.utc
    ).astimezone()
    sender = message["contact"] or message["address"]
    print(f"{received:%H:%M:%S}  {sender}: {message['body']}", flush=True)


def handle(message: dict) -> None:
    """Call the hook once per message."""

    global newest
    if message["id"] in seen:
        return
    if message["id"] is not None:
        seen.add(message["id"])
    newest = max(newest, message["timestamp"])
    on_sms(message)


def catch_up(dock: SmsDockClient) -> None:
    """Read what arrived while the connection was down.

    The phone does not send events again. The client calls this function
    after every reconnect, when the subscription is in force again.
    """

    if dock.server_restarted or dock.missed_events:
        # Messages of the same millisecond come again; handle() knows them.
        missed = list(dock.iter_messages(direction="in", since=newest))
        for message in reversed(missed):  # oldest first
            handle(message)


def main() -> int:
    global newest
    if len(sys.argv) != 2:
        print(__doc__)
        return 2

    # Password and fingerprint come from the environment.
    with SmsDockClient(
        sys.argv[1],
        # Keep trying when the phone is out of reach for a while.
        reconnect_attempts=None,
        on_reconnect=catch_up,
    ) as dock:
        # Only messages from now on: remember the time of the phone.
        newest = dock.ping()["time"]
        # A new connection gets no events. Ask for the ones you need.
        dock.subscribe("sms.received")
        print("Waiting for SMS. Press Ctrl+C to stop.", flush=True)

        # events() yields forever. After a lost connection it reconnects,
        # logs in and subscribes again, then calls catch_up().
        for event in dock.events():
            handle(event["data"]["message"])
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
    except (SmsDockError, ValueError) as error:
        sys.exit(f"listen_incoming: {error}")
