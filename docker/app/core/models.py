"""Pydantic models for request/response validation."""

from __future__ import annotations

from pydantic import BaseModel, Field


class DownloadRequest(BaseModel):
    """Start a download for selected episodes of an arc."""
    arc_title: str
    episode_nums: list[int]
    source: str = Field(default="onepace", pattern="^(onepace|muhn)$")
    version: str = "English Subtitles"
    quality: str = "1080p"


class DownloadStatus(BaseModel):
    id: str
    arc_title: str
    status: str  # queued | downloading | done | error | cancelled
    progress: float = 0.0       # 0.0 - 1.0
    speed: str = ""
    current_file: str = ""
    current_idx: int = 0
    total_files: int = 0
    error: str | None = None


class SettingsPayload(BaseModel):
    version: str | None = None
    quality: str | None = None
    # SABnzbd (Usenet hand-off)
    sabnzbd_url: str | None = None
    sabnzbd_api_key: str | None = None
    sabnzbd_category: str | None = None
    # qBittorrent (torrent hand-off)
    qbittorrent_url: str | None = None
    qbittorrent_user: str | None = None
    qbittorrent_pass: str | None = None
    qbittorrent_category: str | None = None
    # NZBGeek (Usenet indexer)
    nzbgeek_url: str | None = None
    nzbgeek_api_key: str | None = None


class UsenetSendRequest(BaseModel):
    """Queue selected episodes' NZBs into SABnzbd."""
    arc_title: str
    episode_nums: list[int]
    quality: str = "1080p"


class TorrentSendRequest(BaseModel):
    """Queue selected magnet links into qBittorrent."""
    magnets: list[str]


class SendResult(BaseModel):
    sent: int = 0
    failed: int = 0
    messages: list[str] = []
