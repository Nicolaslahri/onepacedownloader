"""System / health endpoints."""

from __future__ import annotations

import os

from fastapi import APIRouter

from ..config import APP_VERSION, MEDIA_DIR, load_config, save_config
from ..core.episode_index import load_episode_index, try_remote_refresh

router = APIRouter(prefix="/api", tags=["system"])


@router.get("/health")
def health():
    return {
        "status": "ok",
        "version": APP_VERSION,
        "media_dir": str(MEDIA_DIR),
        "media_writable": os.access(MEDIA_DIR, os.W_OK),
    }


@router.post("/refresh")
def refresh_index():
    """Trigger a remote index refresh from the GitHub data branch."""
    cfg = load_config()
    messages: list[str] = []
    ok = try_remote_refresh(cfg, log=lambda m: messages.append(m))
    if ok:
        save_config(cfg)
    return {"success": ok, "messages": messages}


@router.get("/stats")
def stats():
    """Media library stats."""
    index = load_episode_index()
    total_arcs = len(index.get("arcs", []))
    total_eps = sum(
        len(a.get("episodes", [])) for a in index.get("arcs", [])
    )

    # Count downloaded files in the One Pace tree
    plex_dir = MEDIA_DIR / "One Pace"
    downloaded = 0
    if plex_dir.exists():
        for season_dir in plex_dir.iterdir():
            if season_dir.is_dir():
                downloaded += sum(
                    1 for f in season_dir.iterdir()
                    if f.is_file() and not f.suffix == ".nfo"
                )

    return {
        "total_arcs": total_arcs,
        "total_episodes": total_eps,
        "downloaded_episodes": downloaded,
    }
