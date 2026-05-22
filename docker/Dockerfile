FROM python:3.12-slim

# ── Environment ──────────────────────────────────────────────────────
# GIT_SHA is supplied by the publish workflow (--build-arg) and stamped
# into the image so the running app can detect when it's out of date.
ARG GIT_SHA=dev
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PORT=7654 \
    GIT_SHA=$GIT_SHA \
    CONFIG_DIR=/config \
    MEDIA_DIR=/media

# ── System deps ──────────────────────────────────────────────────────
RUN apt-get update && \
    apt-get install -y --no-install-recommends tini && \
    rm -rf /var/lib/apt/lists/*

# ── Python deps ──────────────────────────────────────────────────────
WORKDIR /app
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# ── Application ──────────────────────────────────────────────────────
COPY app/ app/
COPY data/ data/

# ── Volumes & port ───────────────────────────────────────────────────
VOLUME ["/config", "/media"]
EXPOSE 7654

# ── Run ──────────────────────────────────────────────────────────────
# Shell form so the PORT env var is honoured; `exec` keeps uvicorn as
# PID 1's direct child for clean signal handling under tini.
ENTRYPOINT ["tini", "--"]
CMD ["sh", "-c", "exec python -m uvicorn app.main:app --host 0.0.0.0 --port ${PORT:-7654}"]
