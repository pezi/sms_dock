#!/usr/bin/env python3
"""Send one SMS and wait for the result.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    python send_sms.py wss://192.168.1.50:9443/ws +4369912345678 "Hello"
"""

import sys

from smsdock import ApiError, SmsDockClient, SmsDockError


def main() -> int:
    if len(sys.argv) != 4:
        print(__doc__)
        return 2
    url, number, text = sys.argv[1:]

    # Password and fingerprint come from the environment.
    with SmsDockClient(url) as dock:
        try:
            # send() returns as soon as the phone has queued the message.
            # The client adds a random requestId, so a repeat after a lost
            # connection cannot send the message twice. Pass your own
            # request_id= if the whole program may run again.
            result = dock.send(number, text)
        except ApiError as error:
            # For example invalid_number, message_too_long, quota_exceeded.
            print(f"Not sent: {error.code}: {error.message}")
            return 1
        print(f"{result['parts']} part(s) for {result['to']}")

        # Wait until the network accepted the message or refused it.
        outcome = dock.wait_for_result(result["requestId"], timeout=60)

    if outcome["status"] == "failed":
        print(f"Failed, error code {outcome['errorCode']}")
        return 1
    print(f"Sent, message id {outcome['messageId']}")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (SmsDockError, TimeoutError, ValueError) as error:
        sys.exit(f"send_sms: {error}")
