"""Network helpers and the Pixeldrain downloader extracted from the desktop app."""

from __future__ import annotations

import json
import re
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

from ..config import (
    BYPASS_FILE,
    PIXELDRAIN_API,
    PIXELDRAIN_FILE,
    UA,
)

# ── Transient errors that justify a retry ─────────────────────────────

_TRANSIENT = (
    urllib.error.URLError,
    ConnectionError,
    TimeoutError,
    OSError,
)


# ── Network helpers ───────────────────────────────────────────────────

def http_get(url: str, *, timeout: int = 30, retries: int = 5) -> bytes:
    last: Exception | None = None
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except _TRANSIENT as e:
            last = e
            time.sleep(min(2 ** attempt, 15))
    raise last if last else RuntimeError("http_get failed")


def open_stream(url: str, *, start: int = 0, timeout: int = 60, retries: int = 5):
    headers = {"User-Agent": UA}
    if start:
        headers["Range"] = f"bytes={start}-"
    req = urllib.request.Request(url, headers=headers)
    last: Exception | None = None
    for attempt in range(retries):
        try:
            return urllib.request.urlopen(req, timeout=timeout)
        except _TRANSIENT as e:
            last = e
            time.sleep(min(2 ** attempt, 15))
    raise last if last else RuntimeError("open_stream failed")


def open_stream_dual(file_id: str, *, start: int = 0, timeout: int = 60,
                     on_log=None):
    """Try pixeldrain.com first; fall back to the bypass CDN on rate-limit."""
    log = on_log or (lambda _m: None)
    primary = PIXELDRAIN_FILE.format(file_id=file_id)
    headers = {"User-Agent": UA}
    if start:
        headers["Range"] = f"bytes={start}-"
    try:
        req = urllib.request.Request(primary, headers=headers)
        return urllib.request.urlopen(req, timeout=timeout), "pixeldrain.com"
    except urllib.error.HTTPError as e:
        if e.code in (403, 429):
            log(f"  pixeldrain.com rate-limited (HTTP {e.code}) -- "
                f"falling back to bypass CDN")
        else:
            log(f"  pixeldrain.com error {e.code} -- falling back to bypass CDN")
    except _TRANSIENT as e:
        log(f"  pixeldrain.com unreachable ({e}) -- falling back to bypass CDN")
    return (open_stream(BYPASS_FILE.format(file_id=file_id),
                        start=start, timeout=timeout),
            "cdn.pixeldrain.eu.cc")


# ── Utilities ─────────────────────────────────────────────────────────

def sanitize_filename(name: str) -> str:
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip(" .")
    return name or "untitled"


def fmt_bytes(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024:
            return f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} PB"


# ── Exceptions ────────────────────────────────────────────────────────

class DownloadCancelled(Exception):
    pass


# ── Downloader ────────────────────────────────────────────────────────

class Downloader:
    """Downloads a pixeldrain album file by file with resume support."""

    def __init__(
        self,
        album_id: str,
        dest_dir: Path,
        *,
        on_status,
        on_progress,
        on_log,
        cancel_evt: threading.Event,
        file_filter: set[str] | None = None,
        subfolder: str | None = None,
    ) -> None:
        self.album_id = album_id
        self.dest_dir = dest_dir
        self.on_status = on_status
        self.on_progress = on_progress
        self.on_log = on_log
        self.cancel_evt = cancel_evt
        self.file_filter = file_filter
        self.subfolder = subfolder

    def fetch_album(self) -> dict:
        data = json.loads(http_get(PIXELDRAIN_API.format(album_id=self.album_id)))
        if not data.get("success", True) and "files" not in data:
            raise RuntimeError(f"Pixeldrain API error: {data}")
        return data

    def run(self) -> None:
        album = self.fetch_album()
        files = album.get("files", [])
        title = album.get("title") or self.album_id
        if self.file_filter is not None:
            files = [f for f in files if f.get("id") in self.file_filter]
            if not files:
                self.on_log(
                    f"Album {title!r}: no files matched the filter -- nothing to do."
                )
                return
        folder_name = sanitize_filename(self.subfolder or title)
        target = self.dest_dir / folder_name
        target.mkdir(parents=True, exist_ok=True)
        self.on_log(
            f"Album: {title} ({len(files)} file{'s' if len(files) != 1 else ''}) "
            f"-> {target}"
        )
        total = len(files)
        for idx, f in enumerate(files, 1):
            if self.cancel_evt.is_set():
                raise DownloadCancelled()
            name = sanitize_filename(f["name"])
            size = int(f.get("size", 0))
            out = target / name
            self.on_status(f"[{idx}/{total}] {name}")
            self._download_one(f["id"], out, size, idx, total)
        self.on_log(f"Done: {title}")

    def _download_one(self, file_id: str, out: Path, size: int,
                      idx: int, total: int) -> None:
        partial = out.with_suffix(out.suffix + ".part")
        if out.exists() and (size == 0 or out.stat().st_size == size):
            self.on_log(f"  skip (already complete): {out.name}")
            self.on_progress(1.0, "already downloaded", idx, total, 0)
            return
        start = partial.stat().st_size if partial.exists() else 0
        attempt = 0
        last_exc: Exception | None = None
        while attempt < 5:
            attempt += 1
            try:
                self._stream_to(file_id, partial, start, size, idx, total)
                if size and partial.stat().st_size != size:
                    raise IOError(
                        f"size mismatch: expected {size}, got {partial.stat().st_size}"
                    )
                if out.exists():
                    out.unlink()
                partial.rename(out)
                return
            except DownloadCancelled:
                raise
            except Exception as e:
                last_exc = e
                start = partial.stat().st_size if partial.exists() else 0
                wait = min(2 ** attempt, 30)
                self.on_log(f"  retry {attempt}/5 after error: {e} (sleep {wait}s)")
                if self.cancel_evt.wait(wait):
                    raise DownloadCancelled()
        raise RuntimeError(f"failed after 5 attempts: {last_exc}")

    def _stream_to(self, file_id: str, partial: Path, start: int,
                   size: int, idx: int, total: int) -> None:
        resp, host = open_stream_dual(file_id, start=start, on_log=self.on_log)
        if start == 0:
            self.on_log(f"  source: {host}")
        with resp:
            mode = "ab" if start else "wb"
            t0 = time.time()
            t_last = t0
            bytes_now = start
            with open(partial, mode) as f:
                while True:
                    if self.cancel_evt.is_set():
                        raise DownloadCancelled()
                    chunk = resp.read(64 * 1024)
                    if not chunk:
                        break
                    f.write(chunk)
                    bytes_now += len(chunk)
                    now = time.time()
                    if now - t_last >= 0.2:
                        speed = (bytes_now - start) / max(now - t0, 0.001)
                        frac = bytes_now / size if size else 0.0
                        self.on_progress(
                            frac, fmt_bytes(speed) + "/s", idx, total, bytes_now
                        )
                        t_last = now
            speed = (bytes_now - start) / max(time.time() - t0, 0.001)
            self.on_progress(1.0, fmt_bytes(speed) + "/s", idx, total, bytes_now)
