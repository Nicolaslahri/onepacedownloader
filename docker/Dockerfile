FROM python:3.12-slim

# ── Environment ──────────────────────────────────────────────────────
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PORT=8080 \
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
EXPOSE 8080

# ── Run ──────────────────────────────────────────────────────────────
ENTRYPOINT ["tini", "--"]
CMD ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080"]
