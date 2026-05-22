"""Settings endpoints — download defaults + external-app integrations."""

from __future__ import annotations

from fastapi import APIRouter

from ..config import (
    DEFAULT_QUALITY,
    DEFAULT_VERSION,
    QUALITIES,
    VERSIONS,
    get_setting,
    load_config,
    save_config,
)
from ..core.models import SettingsPayload

router = APIRouter(prefix="/api/settings", tags=["settings"])

# Integration keys persisted in config.json (resolved via get_setting()).
_INTEGRATION_KEYS = (
    "sabnzbd_url", "sabnzbd_api_key", "sabnzbd_category",
    "qbittorrent_url", "qbittorrent_user", "qbittorrent_pass",
    "qbittorrent_category",
    "nzbgeek_url", "nzbgeek_api_key",
)


@router.get("")
def get_settings():
    """Return current settings — download defaults + integration config."""
    cfg = load_config()
    out = {
        "version": cfg.get("default_version", DEFAULT_VERSION),
        "quality": cfg.get("default_quality", DEFAULT_QUALITY),
        "available_versions": VERSIONS,
        "available_qualities": QUALITIES,
    }
    for key in _INTEGRATION_KEYS:
        out[key] = get_setting(cfg, key)
    return out


@router.put("")
def update_settings(payload: SettingsPayload):
    """Persist any provided settings into config.json."""
    cfg = load_config()
    if payload.version is not None:
        cfg["default_version"] = payload.version
    if payload.quality is not None:
        cfg["default_quality"] = payload.quality
    for key in _INTEGRATION_KEYS:
        val = getattr(payload, key, None)
        if val is not None:
            cfg[key] = val
    save_config(cfg)
    return {"saved": True}
