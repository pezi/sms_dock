#!/usr/bin/env python3
"""Print the last 10 incoming SMS.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    python list_messages.py wss://192.168.1.50:9443/ws
"""

import sys
from datetime import datetime, timezone

from smsdock import SmsDockClient, SmsDockError


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2

    # The password comes from SMSDOCK_PASSWORD, the fingerprint from
    # SMSDOCK_FINGERPRINT. It pins the self-signed certificate of the phone.
    with SmsDockClient(sys.argv[1]) as dock:
        page = dock.messages(direction="in", limit=10)

    for message in page["items"]:
        # Timestamps are milliseconds since the epoch; show local time.
        received = datetime.fromtimestamp(
            message["timestamp"] / 1000, tz=timezone.utc
        ).astimezone()
        sender = message["contact"] or message["address"]
        print(f"{received:%Y-%m-%d %H:%M}  {sender}: {message['body']}")

    # page["nextCursor"] leads to the next 10; see iter_messages() for a
    # loop that follows it.
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (SmsDockError, ValueError) as error:
        sys.exit(f"list_messages: {error}")
