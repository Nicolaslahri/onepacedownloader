"""Tiny in-memory ring buffer of log lines for the web UI's Log panel.

Anywhere in the app that wants to surface activity calls `log(msg)`:
the line gets timestamped and appended to a circular buffer (last 250
entries), and also written to stdout so it shows up in `docker logs`.
"""

from __future__ import annotations

import threading
import time
from collections import deque

_BUFFER: deque[dict] = deque(maxlen=250)
_LOCK = threading.Lock()


def log(msg: str) -> None:
    entry = {"t": time.strftime("%H:%M:%S"), "msg": msg}
    with _LOCK:
        _BUFFER.append(entry)
    print(msg, flush=True)


def all_entries() -> list[dict]:
    with _LOCK:
        return list(_BUFFER)
