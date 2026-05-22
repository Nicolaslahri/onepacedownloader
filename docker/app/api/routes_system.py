"""System / health endpoints."""

from __future__ import annotations

import json
import os
import time
import urllib.request

from fastapi import APIRouter

from ..config import (
    APP_VERSION,
    GIT_SHA,
    GITHUB_REPO,
    MEDIA_DIR,
    load_config,
    save_config,
)
from ..core.episode_index import load_episode_index, try_remote_refresh

router = APIRouter(prefix="/api", tags=["system"])

# Cached update-check result — GitHub's API is only hit once an hour.
_update_cache: dict = {"checked_at": 0.0, "result": None}
_UPDATE_TTL = 3600


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


@router.get("/update")
def update_check():
    """Report whether a newer build of the Docker app is available.

    Compares the git commit this image was built from (GIT_SHA, baked in
    at build time) against the latest commit that touched `docker/` on
    GitHub. The result is cached for an hour so we barely touch the API.
    """
    now = time.time()
    cached = _update_cache["result"]
    if cached is not None and now - _update_cache["checked_at"] < _UPDATE_TTL:
        return cached

    result = {
        "current": GIT_SHA[:12] if GIT_SHA else "",
        "latest": None,
        "update_available": False,
    }

    # No meaningful check for local/dev builds — GIT_SHA isn't a real commit.
    if GIT_SHA and GIT_SHA != "dev":
        try:
            url = (f"https://api.github.com/repos/{GITHUB_REPO}"
                   f"/commits?path=docker&per_page=1")
            req = urllib.request.Request(url, headers={
                "User-Agent": "OnePaceDownloader",
                "Accept": "application/vnd.github+json",
            })
            with urllib.request.urlopen(req, timeout=10) as r:
                commits = json.loads(r.read())
            if commits:
                latest = commits[0].get("sha", "")
                result["latest"] = latest[:12]
                result["update_available"] = bool(
                    latest and latest != GIT_SHA)
        except Exception:
            # Offline / rate-limited — just report "no update known".
            pass

    _update_cache["result"] = result
    _update_cache["checked_at"] = now
    return result
