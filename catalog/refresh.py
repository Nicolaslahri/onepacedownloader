"""CI entry point — refresh every catalog and rebuild the episode index.

Run by .github/workflows/refresh-index.yml on a weekly cron. It does not
scrape Usenet
(that needs a private NZBGeek API key). The committed usenet_arcs.json is
merged in as-is by build_index.

Writes the refreshed files into catalog/. The workflow then publishes them
to the `data` branch.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

SRC = Path(__file__).resolve().parent
sys.path.insert(0, str(SRC))

from sources import (  # noqa: E402
    ARCS_FILE,
    NYAA_FILE,
    REMOTE_ARCS_URL,
    REMOTE_NYAA_URL,
    http_get,
    refresh_arcs_from_web,
    refresh_nyaa_from_web,
    load_arcs,
)
import build_index  # noqa: E402


def warn(msg: str) -> None:
    # Shows as a yellow annotation on the run summary.
    print(f"::warning::{msg}", flush=True)


def restore_published(url: str, path) -> bool:
    """Overwrite `path` with the copy last published to the data branch,
    which is newer than the one committed on main."""
    try:
        data = http_get(url, timeout=30)
        if json.loads(data):
            path.write_bytes(data)
            return True
    except Exception as e:  # noqa: BLE001
        warn(f"Couldn't fetch {url} ({e}); using the committed copy")
    return False


def refresh_arcs() -> None:
    """onepace.net blocks some datacenter IPs, GitHub's runners included.
    Album IDs rarely change, so when the scrape fails we carry on with the
    last published arcs.json — the Pixeldrain walk below still picks up
    every re-released episode inside those albums."""
    try:
        arcs = refresh_arcs_from_web()
        print(f"   {len(arcs)} arcs", flush=True)
        return
    except Exception as e:  # noqa: BLE001
        warn(f"onepace.net scrape failed ({e}); reusing the last arc list")
    restore_published(REMOTE_ARCS_URL, ARCS_FILE)
    print(f"   {len(load_arcs())} arcs (previous list)", flush=True)


def onepace_source_count(path) -> int:
    idx = json.loads(path.read_text(encoding="utf-8"))
    return sum(
        1
        for arc in idx.get("arcs", [])
        for ep in arc.get("episodes", [])
        for src in ep.get("sources", [])
        if src.get("kind") == "onepace"
    )


def main() -> int:
    print("== Refresh arcs from onepace.net ==", flush=True)
    refresh_arcs()

    print("== Refresh torrents from nyaa.si ==", flush=True)
    try:
        arc_titles = [a["title"] for a in load_arcs()]
        buckets = refresh_nyaa_from_web(arc_titles)
        torrents = sum(len(b.get("torrents", [])) for b in buckets)
        print(f"   {len(buckets)} arc buckets, {torrents} torrents", flush=True)
    except Exception as e:  # noqa: BLE001
        warn(f"nyaa.si refresh failed ({e}); reusing the last torrent list")
        restore_published(REMOTE_NYAA_URL, NYAA_FILE)

    print("== Build episode index (full rebuild) ==", flush=True)
    out = build_index.build(
        log=lambda m: print(f"   {m}", flush=True),
        force=True,
    )
    # Publishing an index with no Pixeldrain episodes would break downloads
    # for every user at once. Fail loudly and keep last week's instead.
    count = onepace_source_count(out)
    print(f"   {count} One Pace episode files indexed", flush=True)
    if count == 0:
        print("::error::Index has no One Pace episodes; not publishing.")
        return 1
    print("== CI refresh complete ==", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
