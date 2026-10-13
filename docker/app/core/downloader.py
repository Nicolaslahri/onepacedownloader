"""Network helpers and the Pixeldrain downloader extracted from the desktop app."""

from __future__ import annotations

import json
import re
import threading
import time
import urllib.error
import urllib.request
import zlib
from pathlib import Path

from ..config import (
    BYPASS_FILE,
    PIXELDRAIN_API,
    PIXELDRAIN_FILE,
    UA,
)
from . import tls_trust

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
            req = urllib.request.Request(url, headers={
                "User-Agent": UA,
                "Accept": "text/html,application/json;q=0.9,*/*;q=0.8",
                "Accept-Language": "en-US,en;q=0.9",
            })
            with tls_trust.urlopen(req, timeout=timeout) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            # A 403/404 won't change in the next 30s of retries. Only
            # rate limits and server-side hiccups are worth waiting out.
            if e.code < 500 and e.code not in (408, 429):
                raise
            last = e
            time.sleep(min(2 ** attempt, 15))
        except _TRANSIENT as e:
            # A cert failure means every trust store we have was already
            # tried — retrying just stalls the caller for ~30s. Bail out.
            if tls_trust.is_cert_error(e):
                raise
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
            return tls_trust.urlopen(req, timeout=timeout)
        except _TRANSIENT as e:
            if tls_trust.is_cert_error(e):
                raise
            last = e
            time.sleep(min(2 ** attempt, 15))
    raise last if last else RuntimeError("open_stream failed")


def open_stream_dual(file_id: str, *, start: int = 0, timeout: int = 60,
                     on_log=None, force_bypass: bool = False):
    """Try pixeldrain.com first; fall back to the bypass CDN on rate-limit
    or unreachability. Pass `force_bypass=True` to skip pixeldrain.com
    entirely — used once the speed-based detector has already shown the
    upstream is throttled this session (saves an 8-second slow start per
    subsequent file)."""
    log = on_log or (lambda _m: None)
    if force_bypass:
        return (open_stream(BYPASS_FILE.format(file_id=file_id),
                            start=start, timeout=timeout),
                "cdn.pixeldrain.eu.cc")
    primary = PIXELDRAIN_FILE.format(file_id=file_id)
    headers = {"User-Agent": UA}
    if start:
        headers["Range"] = f"bytes={start}-"
    try:
        req = urllib.request.Request(primary, headers=headers)
        return tls_trust.urlopen(req, timeout=timeout), "pixeldrain.com"
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


# One Pace re-releases episodes in place: the album keeps its ID but the file
# is re-uploaded with a new Pixeldrain ID and a new CRC in its name, sometimes
# with a widened episode range. These are stripped so a re-release still
# matches what the index recorded.
_CRC_TAG_RE = re.compile(r"\[[0-9A-Fa-f]{8}\]")
_RANGE_TAG_RE = re.compile(r"\[\d+(?:[-,]\d+)*\]")


def episode_key(filename: str) -> str:
    """Identity of an episode file that survives a re-release, e.g.
    '[One Pace][115-117] Little Garden 01 [1080p][En Sub][DEC3026A].mp4'
    -> 'little garden 01 [1080p][en sub].mp4'."""
    k = _CRC_TAG_RE.sub("", filename)
    k = _RANGE_TAG_RE.sub("", k)
    k = re.sub(r"(?i)\[one pace\]", "", k)
    return re.sub(r"\s+", " ", k).strip().lower()


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
        expected_names: dict[str, str] | None = None,
        subfolder: str | None = None,
    ) -> None:
        self.album_id = album_id
        self.dest_dir = dest_dir
        self.on_status = on_status
        self.on_progress = on_progress
        self.on_log = on_log
        self.cancel_evt = cancel_evt
        self.file_filter = file_filter
        # file_id -> indexed filename. Lets run() find an episode that was
        # re-uploaded under a new file ID since the index was built.
        self.expected_names = expected_names or {}
        # Filtered files the album no longer has, filled in by run().
        self.missing: list[str] = []
        self.subfolder = subfolder
        # Flipped on the moment the speed-based detector judges pixeldrain.com
        # too slow — subsequent files in the same album skip it outright.
        self._prefer_bypass = False

    def fetch_album(self) -> dict:
        data = json.loads(http_get(PIXELDRAIN_API.format(album_id=self.album_id)))
        if not data.get("success", True) and "files" not in data:
            raise RuntimeError(f"Pixeldrain API error: {data}")
        return data

    def run(self) -> Path:
        """Download the (filtered) album. Returns the on-disk target folder
        so callers can pass it straight to the organize step without
        re-fetching the album metadata."""
        album = self.fetch_album()
        files = album.get("files", [])
        title = album.get("title") or self.album_id
        if self.file_filter is not None:
            files = self._select_files(files, title)
        folder_name = sanitize_filename(self.subfolder or title)
        target = self.dest_dir / folder_name
        if not files:
            return target
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
        return target

    def _select_files(self, files: list[dict], title: str) -> list[dict]:
        """Files in the album matching file_filter. IDs the album no longer
        has are matched by episode_key on the indexed filename instead."""
        wanted = self.file_filter or set()
        present = {f.get("id") for f in files}
        missing = wanted - present
        keys = {episode_key(self.expected_names[i]): i
                for i in missing if self.expected_names.get(i)}
        picked = []
        for f in files:
            if f.get("id") in wanted:
                picked.append(f)
                continue
            fid = keys.pop(episode_key(f.get("name", "")), None)
            if fid is not None:
                picked.append(f)
                missing.discard(fid)
        rematched = len(picked) - len(wanted & present)
        if rematched:
            self.on_log(
                f"  {rematched} episode(s) were re-released since the episode "
                f"list was built -- matched them by name.")
        if missing:
            names = [self.expected_names.get(i, i) for i in sorted(missing)]
            self.missing = names
            self.on_log(
                f"Album {title!r}: {len(missing)} episode(s) are no longer in "
                f"this album: {', '.join(names[:3])}"
                f"{'...' if len(names) > 3 else ''}.")
        return picked

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
                if size and start > size:
                    # Left over from a resume the server didn't honour.
                    self.on_log(f"  discarding oversized partial file "
                                f"for {out.name}")
                    partial.unlink()
                    start = 0
                if not (size and start == size):
                    # A .part already at full size (closed before the
                    # rename) only needs verifying — asking the server for
                    # bytes past the end just earns a 416.
                    self._stream_to(file_id, partial, start, size, idx, total)
                got = partial.stat().st_size
                if size and got != size:
                    if got > size:
                        partial.unlink()
                    raise IOError(f"size mismatch: expected {size}, got {got}")
                self._verify_crc(partial, out.name)
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

    @staticmethod
    def _verify_crc(path: Path, name: str) -> None:
        """One Pace names every file with its CRC32, e.g. [DEC3026A]. A file
        of the right size can still be damaged; this catches it."""
        m = _CRC_TAG_RE.search(name)
        if not m:
            return
        expected = int(m.group(0)[1:-1], 16)
        crc = 0
        with open(path, "rb") as f:
            while chunk := f.read(1024 * 1024):
                crc = zlib.crc32(chunk, crc)
        if crc != expected:
            path.unlink()
            raise IOError(f"checksum mismatch (got {crc:08X}, expected "
                          f"{expected:08X}) — file was damaged, re-downloading")

    def _stream_to(self, file_id: str, partial: Path, start: int,
                   size: int, idx: int, total: int) -> None:
        # Switch off pixeldrain.com to the bypass CDN if it's running slow —
        # catches the daily 6 GB cap (which just throttles, doesn't error)
        # AND any other source of sluggishness. Judges after a brief
        # warm-up so a slow TCP ramp-up doesn't trigger the switch.
        SLOW_AFTER_SEC = 8.0
        SLOW_BYTES_PER_SEC = 500 * 1024   # 500 KB/s — clearly throttled

        resp, host = open_stream_dual(
            file_id, start=start, on_log=self.on_log,
            force_bypass=self._prefer_bypass)
        if start and getattr(resp, "status", 206) != 206:
            # The server ignored the Range header and is sending the whole
            # file. Appending it would corrupt the .part, so start over.
            self.on_log(f"  {host} can't resume this file — restarting it")
            start = 0
        if start == 0:
            suffix = " (forced — pixeldrain.com was slow earlier)" \
                if self._prefer_bypass else ""
            self.on_log(f"  source: {host}{suffix}")

        mode = "ab" if start else "wb"
        t0 = time.time()
        t_last = t0
        bytes_now = start
        display_start = start

        try:
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

                    # Mid-stream switch when pixeldrain.com is slow.
                    if (host == "pixeldrain.com"
                            and not self._prefer_bypass
                            and (now - t0) >= SLOW_AFTER_SEC):
                        avg_bps = (bytes_now - display_start) / max(now - t0, 0.001)
                        if avg_bps < SLOW_BYTES_PER_SEC:
                            self.on_log(
                                f"  pixeldrain.com is slow "
                                f"({fmt_bytes(avg_bps)}/s) — switching to "
                                f"bypass CDN at {fmt_bytes(bytes_now)}")
                            self._prefer_bypass = True
                            try:
                                try:
                                    resp.close()
                                except Exception:
                                    pass
                                resp = open_stream(
                                    BYPASS_FILE.format(file_id=file_id),
                                    start=bytes_now, timeout=60)
                                if getattr(resp, "status", 206) != 206:
                                    raise IOError(
                                        "bypass CDN ignored the resume offset")
                                host = "cdn.pixeldrain.eu.cc"
                                display_start = bytes_now
                                t0 = now
                                t_last = now
                            except Exception as e:
                                self.on_log(
                                    f"  bypass switch failed ({e}) -- "
                                    f"continuing on pixeldrain.com")

                    if now - t_last >= 0.2:
                        speed = (bytes_now - display_start) / max(now - t0, 0.001)
                        frac = bytes_now / size if size else 0.0
                        self.on_progress(
                            frac, fmt_bytes(speed) + "/s", idx, total, bytes_now
                        )
                        t_last = now

            speed = (bytes_now - display_start) / max(time.time() - t0, 0.001)
            self.on_progress(1.0, fmt_bytes(speed) + "/s", idx, total, bytes_now)
        finally:
            try:
                resp.close()
            except Exception:
                pass
