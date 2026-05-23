"""Episode listing endpoints."""

from __future__ import annotations

from fastapi import APIRouter, HTTPException

from ..core.downloader import fmt_bytes
from ..core.episode_index import episode_title, load_episode_index

router = APIRouter(prefix="/api/arcs", tags=["episodes"])


@router.get("/{arc_title}/episodes")
def list_episodes(arc_title: str):
    """Return episodes for a specific arc."""
    index = load_episode_index()
    for arc in index.get("arcs", []):
        if arc.get("title") == arc_title:
            episodes = arc.get("episodes", [])
            result = []
            for ep in episodes:
                versions = set()
                qualities = set()
                kinds = set()
                total_size = 0
                for src in ep.get("sources", []):
                    v = src.get("version", "")
                    if v:
                        versions.add(v)
                    q = src.get("quality", "")
                    if q:
                        qualities.add(q)
                    k = src.get("kind", "")
                    if k:
                        kinds.add(k)
                    total_size = max(total_size, src.get("size_bytes", 0))

                result.append({
                    "num": ep.get("num", 0),
                    "title": episode_title(ep),
                    "canonical_title": ep.get("canonical_title", ""),
                    "plot": ep.get("plot", ""),
                    "has_sub": "English Subtitles" in versions,
                    "has_dub": "English Dub" in versions,
                    "kinds": sorted(kinds),
                    "qualities": sorted(
                        qualities,
                        key=lambda q: int(q.replace("p", ""))
                        if q.replace("p", "").isdigit() else 0,
                        reverse=True,
                    ),
                    "size": fmt_bytes(total_size) if total_size else "",
                    "size_bytes": total_size,
                })
            return result
    raise HTTPException(404, f"Arc {arc_title!r} not found")
