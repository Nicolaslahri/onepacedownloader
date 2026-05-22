"""Arc listing endpoints."""

from __future__ import annotations

from fastapi import APIRouter

from ..core.episode_index import load_episode_index

router = APIRouter(prefix="/api/arcs", tags=["arcs"])


@router.get("")
def list_arcs():
    """Return all arcs with episode counts and available sources."""
    index = load_episode_index()
    result = []
    for arc in index.get("arcs", []):
        episodes = arc.get("episodes", [])
        # Collect unique versions and qualities across all episodes
        versions = set()
        qualities = set()
        sources = set()
        for ep in episodes:
            for src in ep.get("sources", []):
                kind = src.get("kind", "")
                if kind:
                    sources.add(kind)
                v = src.get("version", "")
                if v:
                    versions.add(v)
                q = src.get("quality", "")
                if q:
                    qualities.add(q)
        # Whole-arc torrent packs also count as a Nyaa source even when no
        # per-episode torrents exist.
        for pack in arc.get("arc_packs", []):
            kind = pack.get("kind", "")
            if kind:
                sources.add(kind)

        result.append({
            "title": arc.get("title", ""),
            "episode_count": len(episodes),
            "sources": sorted(sources),
            "versions": sorted(versions),
            "qualities": sorted(qualities, key=lambda q: int(q.replace("p", "")) if q.replace("p", "").isdigit() else 0, reverse=True),
        })
    return result
