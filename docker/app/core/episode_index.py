"""Episode index — loading, remote refresh, and source selection.

The unified per-episode index (`episode_index.json`) is the single data
source for the whole app: arcs, episodes, and their One Pace / Muhn /
Nyaa / Usenet sources.
"""

from __future__ import annotations

import json
import re
import time
import urllib.request

from ..config import APP_VERSION, BUNDLED_INDEX_FILE, INDEX_FILE, REMOTE_INDEX_URL
from . import tls_trust


# ── Loading ───────────────────────────────────────────────────────────

def load_episode_index() -> dict:
    """Load the unified per-episode index (v2). Returns the envelope with
    'arcs' (each containing 'episodes' and 'arc_packs') and 'specials'.
    Prefers the refreshed copy in /config, falls back to the bundled one."""
    for p in (INDEX_FILE, BUNDLED_INDEX_FILE):
        if p.exists():
            try:
                return json.loads(p.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                pass
    return {"arcs": [], "specials": []}


def try_remote_refresh(config: dict, log=None) -> bool:
    """Fetch the pre-built index from the repo's `data` branch and cache it
    in /config. Returns True on success, False if anything goes wrong (the
    caller then falls back to the bundled copy)."""
    _log = log or (lambda m: None)
    _log("Fetching latest index from GitHub (data branch)...")
    try:
        req = urllib.request.Request(
            REMOTE_INDEX_URL,
            headers={"User-Agent": f"OnePaceDownloader/{APP_VERSION}"},
        )
        with tls_trust.urlopen(req, timeout=30) as r:
            etag = r.headers.get("ETag") or ""
            blob = r.read()
    except Exception as e:
        _log(f"Central refresh unavailable ({e}) -- using local data.")
        return False

    cached_etag = (config.get("refresh_cache") or {}).get("remote_etag")
    if etag and etag == cached_etag and INDEX_FILE.exists():
        _log("Already up to date.")
        return True

    try:
        data = json.loads(blob)
        if not isinstance(data.get("arcs"), list):
            raise ValueError("Remote payload missing 'arcs' list")
    except Exception as e:
        _log(f"Remote payload looks malformed ({e}) -- using local data.")
        return False

    try:
        INDEX_FILE.write_bytes(blob)
    except OSError as e:
        # /config not writable (bad volume permissions) — don't crash,
        # just fall back to the index bundled in the image.
        _log(f"Couldn't cache the index ({e}) -- using bundled copy.")
        return False

    rc = dict(config.get("refresh_cache", {}))
    if etag:
        rc["remote_etag"] = etag
    rc["last_refresh"] = time.strftime("%Y-%m-%dT%H:%M:%S")
    config["refresh_cache"] = rc

    scraped = data.get("scraped_at", "?")
    _log(f"Pulled fresh index (scraped {scraped}, {len(data['arcs'])} arcs).")
    return True


def episode_title(ep: dict, *, max_len: int = 0) -> str:
    """Preferred display string for an episode."""
    canonical = ep.get("canonical_title")
    if canonical:
        title = f"{ep.get('num', 0):02d}  {canonical}"
    else:
        title = ep.get("display_title", "?")
    if max_len and len(title) > max_len:
        return title[: max_len - 1] + "…"
    return title


def quality_rank(q: str) -> int:
    m = re.match(r"(\d+)p", q)
    return int(m.group(1)) if m else 0


# Closest substitute first. The two dubs share an audio track, so they stand
# in for each other before anything falls back to the Japanese audio.
_VERSION_FALLBACK = {
    "English Subtitles": ["English Subtitles", "English Dub",
                          "English Dub with Closed Captions"],
    "English Dub": ["English Dub", "English Dub with Closed Captions",
                    "English Subtitles"],
    "English Dub with Closed Captions": ["English Dub with Closed Captions",
                                         "English Dub", "English Subtitles"],
}


def is_dub(version: str | None) -> bool:
    return bool(version) and "Dub" in version


def is_sub_fallback(src: dict | None, version_pref: str) -> bool:
    """True when a Dub was asked for but `src` is the subtitled release."""
    return bool(src and src.get("version") and is_dub(version_pref)
                and not is_dub(src["version"]))


def best_source_for(ep: dict, kind: str, version_pref: str,
                    quality_pref: str, *,
                    allow_subs: bool = True) -> dict | None:
    """Pick the best source for an episode.

    Versions are tried closest-first (Dub and Dub-CC stand in for each other
    before Subtitles), and within a version the preferred quality wins, else
    the best one available. With `allow_subs=False` a Dub preference never
    falls back to the subtitled release."""
    candidates = [s for s in ep.get("sources", []) if s.get("kind") == kind]
    if not candidates:
        return None
    if not any(s.get("version") for s in candidates):
        # Muhn Pace: dub-only, no version field.
        order = [None]
    else:
        order = _VERSION_FALLBACK.get(version_pref, [version_pref])
        if not allow_subs and is_dub(version_pref):
            order = [v for v in order if is_dub(v)]
    for ver in order:
        same_ver = [s for s in candidates if s.get("version") == ver]
        for s in same_ver:
            if s.get("quality") == quality_pref:
                return s
        if same_ver:
            return max(same_ver,
                       key=lambda s: quality_rank(s.get("quality", "")))
    if allow_subs:
        # A version label we don't know: anything beats nothing.
        return max(candidates, key=lambda s: quality_rank(s.get("quality", "")))
    return None


def usenet_source_for(ep: dict, quality_pref: str) -> dict | None:
    """Pick the best Usenet (NZB) source for an episode. Usenet releases
    carry no 'version', so we only match on quality."""
    candidates = [s for s in ep.get("sources", []) if s.get("kind") == "usenet"]
    if not candidates:
        return None
    for s in candidates:
        if s.get("quality") == quality_pref:
            return s
    return max(candidates, key=lambda s: quality_rank(s.get("quality", "")))


def _btih(magnet: str) -> str:
    """Extract the BitTorrent info-hash from a magnet URI (lowercased)."""
    m = re.search(r"btih:([0-9a-fA-F]{40}|[A-Za-z2-7]{32})", magnet or "")
    return m.group(1).lower() if m else ""


def _fmt_size(n: int) -> str:
    n = float(n or 0)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024:
            return f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} PB"


def collect_torrents(arc: dict) -> list[dict]:
    """Gather every Nyaa torrent for an arc — whole-arc packs plus any
    per-episode torrents — deduped by info-hash, most-seeded first."""
    items: list[dict] = []
    seen: set[str] = set()

    def add(src: dict, fallback_label: str) -> None:
        magnet = src.get("magnet")
        if not magnet:
            return
        h = _btih(magnet)
        if h and h in seen:
            return
        if h:
            seen.add(h)
        items.append({
            "title": src.get("torrent_title") or fallback_label,
            "quality": src.get("quality", ""),
            "size": src.get("size_str") or _fmt_size(src.get("size_bytes", 0)),
            "seeders": src.get("seeders", 0),
            "uploader": src.get("uploader", ""),
            "magnet": magnet,
        })

    for pack in arc.get("arc_packs", []):
        if pack.get("kind") == "nyaa":
            add(pack, "Arc pack")
    for ep in arc.get("episodes", []):
        for s in ep.get("sources", []):
            if s.get("kind") == "nyaa":
                add(s, f"Episode {ep.get('num', 0):02d}")

    items.sort(key=lambda t: t.get("seeders", 0) or 0, reverse=True)
    return items
