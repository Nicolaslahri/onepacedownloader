#!/bin/sh
# Run the app as PUID:PGID (default 1000:1000) so downloads and config
# are owned by your user, not root — the linuxserver.io pattern.
#
# If the container was started as root (the normal case) we chown
# /config and drop privileges via gosu. If it was already started as a
# non-root user (e.g. a compose `user:` directive), we just run the app
# directly — gosu can't switch users without root, and doesn't need to.
set -e

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"
PORT="${PORT:-7654}"

start_app() {
    exec python -m uvicorn app.main:app --host 0.0.0.0 --port "${PORT}"
}

if [ "$(id -u)" = "0" ]; then
    mkdir -p /config
    if ! chown -R "${PUID}:${PGID}" /config 2>/dev/null; then
        echo "[entrypoint] warning: could not chown /config — settings may not persist"
    fi
    echo "[entrypoint] starting One Pace Downloader as ${PUID}:${PGID}"
    exec gosu "${PUID}:${PGID}" python -m uvicorn app.main:app \
        --host 0.0.0.0 --port "${PORT}"
else
    echo "[entrypoint] starting One Pace Downloader as $(id -u):$(id -g) (non-root)"
    start_app
fi
