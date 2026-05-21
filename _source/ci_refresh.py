"""CI entry point — refresh every catalog and rebuild the episode index.

Run by .github/workflows/refresh-index.yml on a weekly cron. Does exactly
what the in-app "Force full rebuild" does, except it does NOT scrape Usenet
(that needs a private NZBGeek API key). The committed usenet_arcs.json is
merged in as-is by build_episode_index.

Writes the refreshed files into _source/. The workflow then publishes them
to the `data` branch.
"""
from __future__ import annotations

import sys
from pathlib import Path

SRC = Path(__file__).resolve().parent
sys.path.insert(0, str(SRC))

# These imports pull in onepace_downloader (which imports customtkinter) —
# the workflow installs that before running this script.
from onepace_downloader import (  # noqa: E402
    refresh_arcs_from_web,
    refresh_nyaa_from_web,
    load_arcs,
)
import build_episode_index  # noqa: E402


def main() -> int:
    print("== Refresh arcs from onepace.net ==", flush=True)
    arcs = refresh_arcs_from_web()
    print(f"   {len(arcs)} arcs", flush=True)

    print("== Refresh torrents from nyaa.si ==", flush=True)
    arc_titles = [a["title"] for a in load_arcs()]
    buckets = refresh_nyaa_from_web(arc_titles)
    torrents = sum(len(b.get("torrents", [])) for b in buckets)
    print(f"   {len(buckets)} arc buckets, {torrents} torrents", flush=True)

    print("== Build episode index (full rebuild) ==", flush=True)
    build_episode_index.build(
        log=lambda m: print(f"   {m}", flush=True),
        force=True,
    )
    print("== CI refresh complete ==", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
