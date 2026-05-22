FROM python:3.12-slim

# ── Environment ──────────────────────────────────────────────────────
# GIT_SHA is supplied by the publish workflow (--build-arg) and stamped
# into the image so the running app can detect when it's out of date.
ARG GIT_SHA=dev
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PORT=7654 \
    GIT_SHA=$GIT_SHA \
    PUID=1000 \
    PGID=1000 \
    CONFIG_DIR=/config \
    MEDIA_DIR=/media

# ── System deps ──────────────────────────────────────────────────────
# tini = init / zombie reaper · gosu = drop privileges to PUID:PGID
RUN apt-get update && \
    apt-get install -y --no-install-recommends tini gosu && \
    rm -rf /var/lib/apt/lists/*

# ── Python deps ──────────────────────────────────────────────────────
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# ── Application ──────────────────────────────────────────────────────
COPY app/ app/
COPY data/ data/
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# ── Volumes & port ───────────────────────────────────────────────────
VOLUME ["/config", "/media"]
EXPOSE 7654

# ── Run ──────────────────────────────────────────────────────────────
# The entrypoint makes /config writable by PUID:PGID, then runs the app
# as that user — so downloads and config aren't owned by root.
ENTRYPOINT ["tini", "--", "/entrypoint.sh"]
