#!/usr/bin/env python3
"""Hook that answers incoming SMS.

An SMS with the text STATUS gets the answer "All systems running.";
everything else gets a short help text.

An answering machine can end up in a loop, for example with another
answering machine or with a service that answers every SMS. Three guards
prevent that:

1. A number gets at most one answer in COOLDOWN_MINUTES.
2. Senders that are not ordinary phone numbers get no answer: names such
   as "A1" or "Post" and short codes such as 8810.
3. At most MAX_REPLIES_PER_HOUR answers are sent in total.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    python auto_reply.py wss://192.168.1.50:9443/ws

Stop with Ctrl+C. Every answer is an SMS that your tariff may charge.
"""

from __future__ import annotations

import re
import sys
import time
from collections import deque

from smsdock import ApiError, SmsDockClient, SmsDockError

COOLDOWN_MINUTES = 10
MAX_REPLIES_PER_HOUR = 20

# A phone number: optional +, then 7 to 15 digits. Shorter ones are short
# codes; anything with letters is a sender name that cannot be answered.
PHONE_NUMBER = re.compile(r"\+?[0-9]{7,15}")


class ReplyGuard:
    """Decides whether a sender may get an answer now."""

    def __init__(self, cooldown_seconds: float, replies_per_hour: int) -> None:
        self.cooldown_seconds = cooldown_seconds
        self.replies_per_hour = replies_per_hour
        self._last_reply: dict[str, float] = {}
        self._recent: deque[float] = deque()

    def refusal(self, address: str, now: float | None = None) -> str | None:
        """Return why this sender gets no answer, or None if it gets one."""

        now = time.monotonic() if now is None else now
        number = re.sub(r"[\s\-.()]", "", address)
        if not PHONE_NUMBER.fullmatch(number):
            return "not a phone number"
        last = self._last_reply.get(number)
        if last is not None and now - last < self.cooldown_seconds:
            return "answered a short time ago"
        while self._recent and now - self._recent[0] >= 3600:
            self._recent.popleft()
        if len(self._recent) >= self.replies_per_hour:
            return "too many answers in the last hour"
        # Count the answer before it is sent: a failure must not open
        # the door for a loop.
        self._last_reply[number] = now
        self._recent.append(now)
        return None


def answer_for(text: str) -> str:
    """Return the answer to a message text."""

    if text.strip().upper() == "STATUS":
        return "All systems running."
    return "Commands: STATUS"


def main() -> int:
    if len(sys.argv) != 2:
        print(__doc__)
        return 2

    guard = ReplyGuard(COOLDOWN_MINUTES * 60, MAX_REPLIES_PER_HOUR)
    with SmsDockClient(
        sys.argv[1],
        reconnect_attempts=None,
    ) as dock:
        dock.subscribe("sms.received")
        print("Answering SMS. Press Ctrl+C to stop.", flush=True)

        # SMS that arrive while the connection is down get no answer: the
        # phone does not send events again, and a late answer helps nobody.
        # listen_incoming.py shows how to read such messages.
        for event in dock.events():
            message = event["data"]["message"]
            sender = message["address"]
            refusal = guard.refusal(sender)
            if refusal is not None:
                print(f"{sender}: no answer ({refusal})")
                continue
            try:
                # Answer on the SIM that received the message.
                result = dock.send(
                    sender, answer_for(message["body"]), sim=message["sim"]
                )
            except ApiError as error:
                print(f"{sender}: not answered, {error.code}: {error.message}")
                continue
            print(f"{sender}: answered, requestId {result['requestId']}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
    except (SmsDockError, ValueError) as error:
        sys.exit(f"auto_reply: {error}")
