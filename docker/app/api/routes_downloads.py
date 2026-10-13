"""Download management endpoints + SSE stream."""

from __future__ import annotations

import asyncio
import json

from fastapi import APIRouter, HTTPException
from fastapi.responses import StreamingResponse

from ..core.episode_index import (
    best_source_for,
    is_sub_fallback,
    load_episode_index,
)
from ..core.models import DownloadEstimate, DownloadRequest
from ..download_manager import manager

router = APIRouter(prefix="/api/downloads", tags=["downloads"])


@router.post("")
def start_download(req: DownloadRequest):
    """Queue a download for selected episodes of an arc."""
    index = load_episode_index()
    arc = None
    for a in index.get("arcs", []):
        if a.get("title") == req.arc_title:
            arc = a
            break
    if not arc:
        raise HTTPException(404, f"Arc {req.arc_title!r} not found")

    episodes = [
        ep for ep in arc.get("episodes", [])
        if ep.get("num") in req.episode_nums
    ]
    if not episodes:
        raise HTTPException(400, "No matching episodes found")

    job_id = manager.enqueue(
        arc=arc,
        episodes=episodes,
        source_kind=req.source,
        version=req.version,
        quality=req.quality,
        allow_subs=req.allow_subs,
        index=index,
    )
    return {"job_id": job_id}


@router.post("/estimate", response_model=DownloadEstimate)
def estimate(req: DownloadRequest) -> DownloadEstimate:
    """Accurate size estimate for the planned download — sums size_bytes of
    the source actually picked per episode (matches what we'll download)."""
    index = load_episode_index()
    arc = next((a for a in index.get("arcs", [])
                if a.get("title") == req.arc_title), None)
    if not arc:
        raise HTTPException(404, f"Arc {req.arc_title!r} not found")
    est = DownloadEstimate()
    for ep in arc.get("episodes", []):
        if ep.get("num") not in req.episode_nums:
            continue
        src = best_source_for(ep, req.source, req.version, req.quality)
        size = int((src or {}).get("size_bytes", 0) or 0)
        if is_sub_fallback(src, req.version):
            est.sub_only += 1
            est.sub_only_bytes += size
        elif src:
            est.matched += 1
            est.total_bytes += size
        else:
            est.missing += 1
    return est


@router.get("")
def list_downloads():
    """List all download jobs."""
    return [s.model_dump() for s in manager.all_statuses()]


@router.get("/{job_id}")
def get_download(job_id: str):
    """Get status of a specific download."""
    status = manager.get_status(job_id)
    if not status:
        raise HTTPException(404, "Job not found")
    return status.model_dump()


@router.delete("/{job_id}")
def cancel_download(job_id: str):
    """Cancel a queued or active download."""
    if not manager.cancel(job_id):
        raise HTTPException(404, "Job not found")
    return {"cancelled": True}


@router.get("/events/stream")
async def download_events():
    """SSE endpoint — streams real-time download progress updates."""
    q = manager.subscribe()

    async def event_stream():
        try:
            # Send current state as initial payload
            for status in manager.all_statuses():
                yield f"data: {json.dumps(status.model_dump())}\n\n"
            # Stream updates
            while True:
                try:
                    data = await asyncio.wait_for(q.get(), timeout=30)
                    yield f"data: {json.dumps(data)}\n\n"
                except asyncio.TimeoutError:
                    # Keep-alive
                    yield ": keepalive\n\n"
        except asyncio.CancelledError:
            pass
        finally:
            manager.unsubscribe(q)

    return StreamingResponse(
        event_stream(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
            "X-Accel-Buffering": "no",
        },
    )
