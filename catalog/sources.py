"""Scrapers and loaders for the catalog files in this folder.

Fetches the arc list from onepace.net and the torrent list from nyaa.si, and
reads the curated Muhn Pace and Usenet catalogs. build_index.py turns all of
it into episode_index.json, the file both apps download from the `data`
branch.
"""

from __future__ import annotations

import html
import json
import re
import time
import urllib.error
import urllib.request
from pathlib import Path

import tls_trust

DATA_DIR = Path(__file__).resolve().parent
ARCS_FILE = DATA_DIR / "arcs.json"
MUHN_FILE = DATA_DIR / "muhn_arcs.json"
NYAA_FILE = DATA_DIR / "nyaa_arcs.json"
USENET_FILE = DATA_DIR / "usenet_arcs.json"
INDEX_FILE = DATA_DIR / "episode_index.json"

# Last published copies, used when a live scrape is blocked.
REMOTE_DATA_BASE = (
    "https://raw.githubusercontent.com/Nicolaslahri/onepacedownloader/data")
REMOTE_ARCS_URL = f"{REMOTE_DATA_BASE}/arcs.json"
REMOTE_NYAA_URL = f"{REMOTE_DATA_BASE}/nyaa_arcs.json"

ONEPACE_URL = "https://onepace.net/en/watch"
PIXELDRAIN_API = "https://pixeldrain.com/api/list/{album_id}"
NYAA_URL = "https://nyaa.si/?f=0&c=0_0&q=one+pace&p={page}"
NYAA_MAX_PAGES = 10  # safety cap; query currently ~6 pages of 75 results

# Galaxy9000 is the One Pace project's official Nyaa uploader account. Torrents
# from this account get a green "Official" badge and sort to the top of every
# per-episode source list.
NYAA_OFFICIAL_UPLOADER = "Galaxy9000"
NYAA_OFFICIAL_URL = (
    "https://nyaa.si/user/" + NYAA_OFFICIAL_UPLOADER
    + "?f=0&c=0_0&q=one+pace&p={page}"
)

UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
)


_TRANSIENT = (
    urllib.error.URLError,
    ConnectionError,
    TimeoutError,
    OSError,  # WinError 10054 surfaces as ConnectionResetError -> OSError
)


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
            # tried — retrying just stalls the user for ~30s. Bail out.
            if tls_trust.is_cert_error(e):
                raise
            last = e
            time.sleep(min(2 ** attempt, 15))
    raise last if last else RuntimeError("http_get failed")


def parse_arcs_from_html(page_html: str) -> list[dict]:
    arc_re = re.compile(
        r"<h2[^>]*>\s*<a[^>]*>([^<]+?)</a>\s*</h2>(.*?)(?=<h2[^>]*>\s*<a|$)",
        re.DOTALL,
    )
    res_re = re.compile(
        r'<span class="flex-1">([^<]+)</span>(.*?)'
        r'(?=<span class="flex-1">|</ul></li><li |$)',
        re.DOTALL,
    )
    link_re = re.compile(
        r'href="(https://pixeldrain\.net/l/([A-Za-z0-9]+))"[^>]*>.*?'
        r'<span class="grow text-center tabular-nums">\s*([0-9]+p)\s*</span>',
        re.DOTALL,
    )

    arcs: list[dict] = []
    for m in arc_re.finditer(page_html):
        title = html.unescape(m.group(1).strip())
        block = m.group(2)
        if "pixeldrain" not in block:
            continue
        resources: dict[str, dict[str, str]] = {}
        for r in res_re.finditer(block):
            name = r.group(1).strip()
            qs: dict[str, str] = {}
            for lk in link_re.finditer(r.group(2)):
                qs[lk.group(3)] = lk.group(2)
            if qs:
                resources[name] = qs
        if resources:
            arcs.append({"title": title, "resources": resources})
    return arcs



def _load_json(path: Path):
    if not path.exists():
        return None
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError:
        return None


def load_arcs() -> list[dict]:
    return _load_json(ARCS_FILE) or []


def load_muhn_arcs() -> list[dict]:
    """Muhn Pace data is curated by hand; there is no live scrape."""
    return _load_json(MUHN_FILE) or []


def refresh_arcs_from_web() -> list[dict]:
    page = http_get(ONEPACE_URL, timeout=60).decode("utf-8", errors="replace")
    arcs = parse_arcs_from_html(page)
    if not arcs:
        raise RuntimeError("Could not parse any arcs from onepace.net (page format changed?)")
    ARCS_FILE.write_text(json.dumps(arcs, indent=2, ensure_ascii=False), encoding="utf-8")
    return arcs


# ----------------------------------------------------------------- nyaa ---

# Nyaa's "trusted only" (f=2) filter returns nothing for One Pace because the
# project doesn't upload from a Trusted account, so we pull the full list and
# rely on title-matching to drop unrelated results. Remake/red rows are skipped.

_NYAA_ROW_RE = re.compile(
    r'<tr class="(default|success|warning)">(.*?)</tr>', re.DOTALL
)
_NYAA_TITLE_RE = re.compile(
    r'<a href="/view/\d+"[^>]*title="([^"]+)"'
)
_NYAA_MAGNET_RE = re.compile(
    r'href="(magnet:\?xt=urn:btih:[^"]+)"'
)
_NYAA_TD_RE = re.compile(
    r'<td class="text-center"(?:\s+data-timestamp="\d+")?\s*>([^<]*)</td>'
)
# Uploaders mark superseded releases with "(Defunct)" / "(Deprecated)" /
# "(Old)" in the title. They sit on the same arc and add noise; filter them
# at parse time so they never reach the UI.
_NYAA_DEPRECATED_RE = re.compile(
    r"[\(\[]\s*(?:defunct|deprecated|outdated|old|superseded|obsolete)\s*[\)\]]",
    re.IGNORECASE,
)


def parse_nyaa_html(page_html: str) -> list[dict]:
    """Extract torrent rows from one nyaa.si search page."""
    rows: list[dict] = []
    for m in _NYAA_ROW_RE.finditer(page_html):
        block = m.group(2)
        title_m = _NYAA_TITLE_RE.search(block)
        magnet_m = _NYAA_MAGNET_RE.search(block)
        if not title_m or not magnet_m:
            continue
        title = html.unescape(title_m.group(1))
        # Skip uploads tagged as defunct / deprecated by the uploader
        if _NYAA_DEPRECATED_RE.search(title):
            continue
        tds = [html.unescape(t).strip() for t in _NYAA_TD_RE.findall(block)]
        # Expected: [size, date, seeders, leechers, downloads]
        size = tds[0] if len(tds) > 0 else ""
        date = tds[1] if len(tds) > 1 else ""
        seeders = int(tds[2]) if len(tds) > 2 and tds[2].isdigit() else 0
        leechers = int(tds[3]) if len(tds) > 3 and tds[3].isdigit() else 0
        rows.append({
            "title": title,
            "magnet": html.unescape(magnet_m.group(1)),
            "size": size,
            "date": date,
            "seeders": seeders,
            "leechers": leechers,
        })
    return rows


_NYAA_ARC_ALIASES: dict[str, list[str]] = {
    # Nyaa uploaders use the Japanese romanization "Arabasta" for Alabasta.
    "Alabasta": ["Arabasta"],
    # Some uploads spell the arc "Whiskey Peak" (extra e) instead of "Whisky Peak".
    "Whisky Peak": ["Whiskey Peak"],
}


def bucket_by_arc(torrents: list[dict], arc_titles: list[str]) -> list[dict]:
    """Group torrents into arc buckets. Longest arc-name match wins.
    Unmatched (or full-series packs) land in an 'Other / Packs' bucket."""
    # Build a (needle, arc) list of all match candidates — each canonical arc
    # plus its aliases. Match longest needle first so 'Whole Cake Island' wins
    # over 'Whole Cake', 'Post-Enies Lobby' wins over 'Enies Lobby', etc.
    needles: list[tuple[str, str]] = []
    for a in arc_titles:
        needles.append((a, a))
        for alias in _NYAA_ARC_ALIASES.get(a, []):
            needles.append((alias, a))
    needles.sort(key=lambda p: len(p[0]), reverse=True)

    buckets: dict[str, list[dict]] = {a: [] for a in arc_titles}
    other: list[dict] = []
    for t in torrents:
        low = t["title"].lower()
        arc = next((arc for needle, arc in needles if needle.lower() in low), None)
        if arc:
            buckets[arc].append(t)
        else:
            other.append(t)

    # Sort each bucket: official (Galaxy9000) first, then by seeders desc.
    def _sort_key(r: dict) -> tuple[int, int]:
        is_official = 1 if r.get("uploader") == NYAA_OFFICIAL_UPLOADER else 0
        return (-is_official, -int(r.get("seeders", 0)))

    out: list[dict] = []
    for arc in arc_titles:
        ts = sorted(buckets[arc], key=_sort_key)
        if ts:
            out.append({"title": arc, "torrents": ts})
    if other:
        out.append({
            "title": "Other / Packs",
            "torrents": sorted(other, key=_sort_key),
        })
    return out


_NYAA_LAST_PAGE_RE = re.compile(
    r'<div class="pagination-page-info">[^<]*\bout of\s+(\d+)\s+results',
    re.IGNORECASE,
)


_BTIH_RE = re.compile(r"btih:([A-Fa-f0-9]+)")


def _extract_btih(magnet: str) -> str:
    """Pull the BitTorrent info-hash out of a magnet URI. Returns lowercase
    hex; empty string if the magnet is malformed."""
    m_ = _BTIH_RE.search(magnet or "")
    return m_.group(1).lower() if m_ else ""


def fetch_official_btihs() -> set[str]:
    """Scrape Galaxy9000's One Pace torrent listing and return the set of
    btih hashes. Used to mark torrents as 'Official' across our main scrape."""
    btihs: set[str] = set()
    for page in range(1, NYAA_MAX_PAGES + 1):
        try:
            html_page = http_get(
                NYAA_OFFICIAL_URL.format(page=page), timeout=60
            ).decode("utf-8", errors="replace")
        except Exception:
            continue
        rows = parse_nyaa_html(html_page)
        if not rows and ('<tr class="default">' not in html_page
                          and '<tr class="success">' not in html_page):
            break
        for r in rows:
            h = _extract_btih(r.get("magnet", ""))
            if h:
                btihs.add(h)
        time.sleep(0.5)
    return btihs


def refresh_nyaa_from_web(arc_titles: list[str]) -> list[dict]:
    """Scrape every page of the One Pace search on nyaa.si and bucket by arc.
    Also pulls Galaxy9000's uploads separately so we can mark them official."""
    all_rows: list[dict] = []
    expected_total: int | None = None
    for page in range(1, NYAA_MAX_PAGES + 1):
        try:
            html_page = http_get(NYAA_URL.format(page=page), timeout=60).decode(
                "utf-8", errors="replace"
            )
        except Exception:
            # One flaky page shouldn't abort the whole scrape; skip and continue.
            continue
        if expected_total is None:
            tm = _NYAA_LAST_PAGE_RE.search(html_page)
            if tm:
                expected_total = int(tm.group(1))
        rows = parse_nyaa_html(html_page)
        # End-of-results detection: only stop when the page actually has no
        # result rows at all (the "No results found" page). We can't use row
        # count thresholds because filtered-out `danger` rows make every page
        # look "short".
        if not rows and ('<tr class="default">' not in html_page
                          and '<tr class="success">' not in html_page):
            break
        all_rows.extend(rows)
        if expected_total is not None and len(all_rows) >= expected_total:
            break
        time.sleep(0.5)  # be polite, also helps with rate-limit hiccups

    if not all_rows:
        raise RuntimeError("Could not parse any torrents from nyaa.si (page format changed?)")

    # Tag torrents uploaded by the official One Pace account
    official_btihs = fetch_official_btihs()
    for r in all_rows:
        r["uploader"] = (NYAA_OFFICIAL_UPLOADER
                         if _extract_btih(r["magnet"]) in official_btihs
                         else "")

    bucketed = bucket_by_arc(all_rows, arc_titles)
    payload = {"scraped_at": time.strftime("%Y-%m-%dT%H:%M:%S"), "arcs": bucketed}
    NYAA_FILE.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")
    return bucketed



def _arcs_of(data) -> list[dict]:
    """Accept both an {"arcs": [...]} envelope and a bare list."""
    if isinstance(data, dict):
        return data.get("arcs") or []
    if isinstance(data, list):
        return data
    return []


def load_nyaa_arcs() -> list[dict]:
    return _arcs_of(_load_json(NYAA_FILE))


def load_usenet_arcs() -> list[dict]:
    return _arcs_of(_load_json(USENET_FILE))
