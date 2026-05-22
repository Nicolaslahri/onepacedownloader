# One Pace Downloader — Docker

A self-hosted web version of One Pace Downloader for your home server. Runs
in the browser, downloads arcs straight into your Plex / Jellyfin library,
and hands Usenet / torrent jobs off to SABnzbd / qBittorrent.

---

## Install — one command

You need [Docker](https://docs.docker.com/get-docker/). Then:

```bash
docker run -d \
  --name onepace-downloader \
  -p 8080:8080 \
  -v /path/to/your/media:/media \
  -v onepace-config:/config \
  ghcr.io/nicolaslahri/onepacedownloader:latest
```

Change `/path/to/your/media` to your Plex / Jellyfin library folder, then
open **http://localhost:8080** (or `http://YOUR-SERVER-IP:8080`).

That's it — the image is pre-built, nothing to compile.

---

## Install — Docker Compose (recommended)

Compose keeps your settings tidy and makes updates one command. Grab
[`docker-compose.yml`](docker-compose.yml) from this folder, edit the media
path, then:

```bash
docker compose up -d
```

To update later:

```bash
docker compose pull && docker compose up -d
```

---

## Volumes

| Path | What it holds |
|------|---------------|
| `/media` | Your Plex / Jellyfin library. One Pace / Muhn Pace downloads land here, auto-organized into `One Pace/Season N/`. |
| `/config` | Persistent settings + the cached episode index. Keep this so your config survives updates. |

## Settings

Open the **Settings** panel in the web UI to set your default version /
quality and connect SABnzbd, qBittorrent, and NZBGeek. You can also
pre-seed these via environment variables — see the commented block in
`docker-compose.yml`.

| Source | How it downloads |
|--------|------------------|
| One Pace / Muhn Pace | Downloaded directly into `/media`, organized for Plex / Jellyfin. |
| Usenet | Handed off to your **SABnzbd** — it downloads to its own folder. |
| Nyaa torrents | Handed off to your **qBittorrent** — it downloads to its own folder. |

---

## Build from source

If you'd rather build it yourself instead of pulling the image:

```bash
cd docker
docker build -t onepace-downloader .
docker run -d -p 8080:8080 -v /path/to/media:/media -v onepace-config:/config onepace-downloader
```

---

## Maintainer note

The publish workflow (`.github/workflows/docker-publish.yml`) pushes the
image to GitHub Container Registry on every change under `docker/`. After
the **first** run, the package is private by default — open it under your
GitHub profile's *Packages*, then *Package settings → Change visibility →
Public* so the one-command install works without a login.
