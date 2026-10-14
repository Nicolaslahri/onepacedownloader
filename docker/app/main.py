"""FastAPI entry point for the One Pace Downloader Docker container."""

from __future__ import annotations

from contextlib import asynccontextmanager

import base64
import secrets

from fastapi import FastAPI, Request
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse, Response
from pathlib import Path

from .config import APP_VERSION, AUTO_REFRESH, PORT, WEB_PASSWORD, load_config, save_config
from .core.episode_index import try_remote_refresh
from .core.log import log as _log

STATIC_DIR = Path(__file__).resolve().parent / "static"


@asynccontextmanager
async def lifespan(app: FastAPI):
    """Startup: refresh the index from GitHub if AUTO_REFRESH is on.

    Any failure here is non-fatal — the app falls back to the index
    bundled in the image rather than refusing to start."""
    if AUTO_REFRESH:
        try:
            cfg = load_config()
            if try_remote_refresh(cfg, log=_log):
                save_config(cfg)
        except Exception as e:
            _log(f"[startup] index refresh skipped: {e}")
    yield


app = FastAPI(
    title="One Pace Downloader",
    version=APP_VERSION,
    lifespan=lifespan,
)

# ── Optional password (WEB_PASSWORD) ─────────────────────────────────
# HTTP Basic auth: the browser shows its own login prompt and then sends
# the credentials with every request, the live progress stream included.
# Any username works. /api/health stays open for Docker's health check.
@app.middleware("http")
async def require_password(request: Request, call_next):
    if not WEB_PASSWORD or request.url.path == "/api/health":
        return await call_next(request)
    scheme, _, token = request.headers.get("authorization", "").partition(" ")
    if scheme.lower() == "basic":
        try:
            _, _, given = base64.b64decode(token).decode("utf-8").partition(":")
        except ValueError:
            given = ""
        if secrets.compare_digest(given.encode(), WEB_PASSWORD.encode()):
            return await call_next(request)
    return Response(
        status_code=401,
        headers={"WWW-Authenticate": 'Basic realm="One Pace Downloader"'},
    )


# ── Register API routers ─────────────────────────────────────────────
from .api.routes_arcs import router as arcs_router
from .api.routes_episodes import router as episodes_router
from .api.routes_downloads import router as downloads_router
from .api.routes_settings import router as settings_router
from .api.routes_system import router as system_router
from .api.routes_integrations import router as integrations_router

app.include_router(arcs_router)
app.include_router(episodes_router)
app.include_router(downloads_router)
app.include_router(settings_router)
app.include_router(system_router)
app.include_router(integrations_router)


# ── Static files (Web UI) ────────────────────────────────────────────
@app.get("/")
def serve_index():
    return FileResponse(STATIC_DIR / "index.html")


app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=PORT)
