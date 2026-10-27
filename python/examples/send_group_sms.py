#!/usr/bin/env python3
"""Send one text to several numbers, one SMS each, and wait for the results.

Usage:

    export SMSDOCK_PASSWORD='your password'
    export SMSDOCK_FINGERPRINT='AB:CD:…'      # from the Server screen of the app
    python send_group_sms.py wss://192.168.1.50:9443/ws "Hello all" \\
        +4369912345678 +436641112223
"""

import sys

from smsdock import ApiError, SmsDockClient, SmsDockError


def main() -> int:
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    url, text = sys.argv[1:3]
    numbers = sys.argv[3:]

    # Password and fingerprint come from the environment.
    with SmsDockClient(url) as dock:
        try:
            # send_group() returns as soon as the phone has queued one SMS
            # per recipient. The phone checks the whole group first: one
            # bad number refuses all of them, nothing is sent then. The
            # client adds a random requestId; the members get the ids
            # requestId.1, requestId.2 and so on.
            result = dock.send_group(numbers, text)
        except ApiError as error:
            # For example invalid_number, message_too_long, quota_exceeded.
            print(f"Not sent: {error.code}: {error.message}")
            return 1
        print(f"{len(result['to'])} recipient(s), {result['parts']} part(s) each")

        # Wait for every member; a member the phone refused at once is
        # already failed and needs no waiting.
        failed = 0
        for member in result["messages"]:
            if member["status"] == "failed":
                print(f"{member['to']}: refused by the phone")
                failed += 1
                continue
            outcome = dock.wait_for_result(member["requestId"], timeout=60)
            if outcome["status"] == "failed":
                print(f"{member['to']}: failed, error code {outcome['errorCode']}")
                failed += 1
            else:
                print(f"{member['to']}: {outcome['status']}, "
                      f"message id {outcome['messageId']}")

    return 1 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (SmsDockError, TimeoutError, ValueError) as error:
        sys.exit(f"send_group_sms: {error}")
