#!/bin/sh
# Runs the app as PUID:PGID (default 1000:1000) instead of root, so that
# downloaded files and saved config are owned by your user — the same
# pattern linuxserver.io images use. Makes /config writable first.
set -e

PUID="${PUID:-1000}"
PGID="${PGID:-1000}"

mkdir -p /config
if ! chown -R "${PUID}:${PGID}" /config 2>/dev/null; then
    echo "[entrypoint] warning: could not chown /config — settings may not persist"
fi

echo "[entrypoint] starting One Pace Downloader as ${PUID}:${PGID}"
exec gosu "${PUID}:${PGID}" python -m uvicorn app.main:app \
    --host 0.0.0.0 --port "${PORT:-7654}"
