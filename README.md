<div align="center">

<img src="assets/icon.png" width="96" />

# One Pace Downloader

**Grab every One Pace arc in one click — Sub, Dub, torrents, or Usenet.**

[![Discord](https://img.shields.io/badge/Discord-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.gg/KHn6AbevZ2)
[![Reddit](https://img.shields.io/badge/Reddit-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/user/nicolasenjah/)
[![License: MIT](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)](LICENSE)

<br />

<a href="https://github.com/Nicolaslahri/onepacedownloader/releases/latest">
  <img src="https://img.shields.io/badge/Download_Latest-.exe-E44D26?style=for-the-badge&logo=windows&logoColor=white" alt="Download .exe" />
</a>
&nbsp;
<a href="https://github.com/Nicolaslahri/onepacedownloader/pkgs/container/onepacedownloader">
  <img src="https://img.shields.io/badge/Or_Self_host-Docker-2496ED?style=for-the-badge&logo=docker&logoColor=white" alt="Docker image" />
</a>

<br /><br />

<img src="assets/screenshot.png" width="750" alt="App screenshot" />

</div>

---

## Why?

Downloading arcs from onepace.net by hand is a pain — every arc is split across separate links with a daily limit that turns long arcs like Wano into a multi-day project. This grabs them in one go.

> Double-click the `.exe` and you're in. Nothing to install.
> Source is in [`_source/`](_source/onepace_downloader.py) under MIT if you'd rather read or run it yourself.

---

## Features

| | Source | What it is |
|---|---|---|
| **One Pace** *(default)* | Pixeldrain | Main fan re-cut. Sub for every arc Romance Dawn &rarr; Egghead, Dub for newer arcs. Most users start here. |
| **Muhn Pace** | Pixeldrain | Fan-made English dub fillers for arcs One Pace hasn't dubbed (Enies Lobby &rarr; Wano). Pair with One Pace for a full dub watch. [Watch order guide](https://www.reddit.com/r/onepace/comments/1rtpukk/one_pace_dub_watch_guide/). |
| **Nyaa** | Torrents | Torrents from [nyaa.si](https://nyaa.si/?q=one+pace), grouped by arc. Hands the magnet to your torrent client. Great when pixeldrain is throttled or you want to seed back. |
| **Usenet** | NZB | NZB files for One Pace releases via NZBGeek. For users with an existing SABnzbd / NZBGet setup. |

**Plus:**

- **Plex / Jellyfin auto-organize** &mdash; files renamed into `One Pace/Season N/One Pace - sNNeNN - Title.ext` with `.nfo` metadata, ready for your media server
- **Resume downloads** &mdash; close mid-download, next launch picks up where it stopped
- **Saved tracking** &mdash; green **Saved** chips per episode, per-arc progress counts (`12/35`)
- **Built-in DNS fix** &mdash; one-click Cloudflare/Google DNS switcher for ISPs that block the CDN

---

## Quick Start

<table>
<tr>
<td width="50%">

**1.** Open the `.exe`
**2.** Pick a save folder (or keep the default)
**3.** Click an arc &rarr; tick episodes &rarr; **Download selected**
**4.** Confirm version (Sub / Dub / Dub-CC) and quality (1080p / 720p / 480p)

</td>
<td width="50%">

<img src="assets/screenshot_dialog.png" width="360" alt="Batch download dialog" />

</td>
</tr>
</table>

Total size shown up front so you know what you're committing to. The right column turns into a live status panel while downloading:

<div align="center">
<img src="assets/screenshot_downloading.png" width="750" alt="Download in progress" />
</div>

---

## Or run it on a home server

Prefer to leave it running on a NAS, mini-PC, or Pi instead of opening the `.exe` on your desktop? A Docker version is published with the same arcs and the same Plex organize &mdash; **plus** direct API integration with **SABnzbd** (Usenet) and **qBittorrent** (torrents), so anything you queue runs server-side and lands straight in your library.

**One command:**

```bash
docker run -d \
  --name onepace-downloader \
  -p 7654:7654 \
  -e PUID=1000 -e PGID=1000 \
  -v /path/to/your/media:/media \
  -v onepace-config:/config \
  ghcr.io/nicolaslahri/onepacedownloader:latest
```

Then open **`http://YOUR-SERVER-IP:7654`**.

| Source | How it downloads |
|---|---|
| **One Pace** / **Muhn Pace** | Pulled directly into `/media`, auto-organized into `One Pace/Season N/` with `.nfo` |
| **Usenet** (NZBGeek) | Sent to your **SABnzbd** via its API |
| **Nyaa** torrents | Sent to your **qBittorrent** via its Web API |

Live progress for all three, a Saved indicator per episode, per-arc download progress (`12/35`), a Log panel, and an "Update available" pill when a newer image is published — all in the browser.

Full setup (Docker Compose, Portainer instructions, environment variables, volume layout): **[`docker/README.md`](docker/README.md)**.

> **PUID / PGID:** matches the linuxserver.io pattern — set them to the owner of your media folder so downloads aren't owned by root. The container makes `/config` writable on startup for you.

---

## Plex / Jellyfin Setup

Open **Settings &rarr; Output organization** and tick **Organize for Plex / Jellyfin**.

Every download is automatically renamed and sorted:

```
downloads/
  One Pace/
    Season 14/
      One Pace - s14e01 - Sir Crocodile, the Pirate.mkv
      One Pace - s14e01 - Sir Crocodile, the Pirate.nfo
      ...
```

Each `.nfo` carries the real episode title and plot from [SpykerNZ/one-pace-for-plex](https://github.com/SpykerNZ/one-pace-for-plex), so Plex and Jellyfin recognize the show and pull artwork automatically.

> **Tip:** Already downloaded files before enabling the toggle? No problem &mdash; saving the setting retroactively organizes everything in your save folder.

---

## Usenet Setup

<details>
<summary><b>Click to expand</b> &mdash; only needed if you have a Usenet subscription</summary>

<br />

Skip this if you don't use Usenet &mdash; Pixeldrain (One Pace) and Nyaa (torrents) work for everyone.

### What you need

1. A **Usenet provider** subscription (~$10/mo) &mdash; [Newshosting](https://www.newshosting.com/), [Eweka](https://www.eweka.nl/), or [UsenetServer](https://www.usenetserver.com/)
2. **[SABnzbd](https://sabnzbd.org/)** (free) or NZBGet installed and configured with your provider
3. An **[NZBGeek](https://nzbgeek.info/)** account + API key &mdash; the bundled release IDs are NZBGeek-specific

### In the app

1. **Settings** &rarr; scroll to the **Usenet** card
2. Paste `https://api.nzbgeek.info` into *Indexer URL* (it's the default)
3. Paste your NZBGeek API key (*[find it here](https://nzbgeek.info/dashboard.php?myaccount)*)
4. **Save & close**

Then switch the source dropdown to **Usenet**, pick an arc, tick episodes, and hit **Download selected**.

### Good to know

- Your API key never leaves your machine &mdash; it lives in `config.json` next to the `.exe`
- Coverage isn't complete &mdash; some arcs (Alabasta, Thriller Bark, Gaimon) aren't on NZBGeek. The app shows what's available
- Older releases (pre-2021) may have rotated off your provider's retention

</details>

---

## Downloads Not Starting?

Some ISPs block the CDN &mdash; the app opens but downloads never begin.

<details>
<summary><b>Two-minute fix &mdash; switch Windows DNS to Cloudflare</b></summary>

<br />

**Manual:** Settings &rarr; Network & Internet &rarr; your connection &rarr; DNS server assignment &rarr; **Edit** &rarr; Manual &rarr; IPv4 on &rarr; Preferred `1.1.1.1`, Alternate `1.0.0.1` &rarr; Save.

**Or use the built-in DNS switcher:** top-right corner &rarr; **DNS** &rarr; pick Cloudflare or Google. UAC prompt, done. Revert from the same panel.

<div align="center">
<img src="assets/screenshot_dns.png" width="500" alt="DNS switcher" />
</div>

Set-and-forget alternative: [Cloudflare WARP](https://1.1.1.1/). Last resort, any VPN works.

If none of those help, paste the Log panel contents into [Discord](https://discord.gg/KHn6AbevZ2).

</details>

---

## Is It Safe?

Fair question. Verify yourself.

<div align="center">

**SHA256:** `75935b45d4f589f501001743e5ab653a5f39c689c17615012e0d3dd88fa926ef`

[![VirusTotal](https://img.shields.io/badge/VirusTotal-Scan_Report-394EFF?style=for-the-badge&logo=virustotal&logoColor=white)](https://www.virustotal.com/gui/file/75935b45d4f589f501001743e5ab653a5f39c689c17615012e0d3dd88fa926ef)

</div>

Most engines clean &mdash; Bitdefender, ESET, Sophos, Symantec, Avast, AVG, Malwarebytes, Microsoft Defender all pass. A handful of heuristic scanners (APEX, Bkav, Cylance, SentinelOne, Yandex) sometimes flag PyInstaller `--onefile` builds based on packed-binary patterns rather than actual malicious behavior &mdash; a common false-positive for unsigned solo-dev tools.

> Don't trust the badge? Drop the `.exe` onto [virustotal.com](https://www.virustotal.com) yourself, or read the [full Python source](_source/onepace_downloader.py) &mdash; one file, stdlib only, MIT.

---

## Heads Up

- **Windows only** &mdash; single `.exe`, nothing to install
- **SmartScreen warning** on first launch &mdash; the `.exe` isn't signed (certs are expensive). Click **More info &rarr; Run anyway**, or right-click &rarr; Properties &rarr; tick **Unblock**

---

## Credits

This app is just a downloader &mdash; none of the actual content is mine. Huge thanks to:

| | |
|---|---|
| **[One Pace team](https://onepace.net)** | Ten-plus years of re-cutting One Piece into the version everyone wishes Toei would air. Every Sub episode comes from their releases. |
| **Muhny** | Editing Muhn Pace &mdash; the dub fillers covering Enies Lobby &rarr; Wano. ~184 GB of careful audio work that's saved dub watchers months of waiting. |
| **[u/KPGNL](https://www.reddit.com/user/KPGNL/)** | Maintaining the [dub watch-order guide](https://www.reddit.com/r/onepace/comments/1rtpukk/one_pace_dub_watch_guide/) the app links to. Original version by **u/AlternativeAd1098**. |
| **[SpykerNZ](https://github.com/SpykerNZ/one-pace-for-plex)** | Canonical episode titles, plots, and the Plex/Jellyfin season layout used by the *Organize for media server* option. |

If you find One Pace or Muhn Pace useful, drop them a thank-you wherever they hang out &mdash; that's worth more than anything I could do.

---

<div align="center">

### Found a bug? Want to chat?

Discord is fastest: **[discord.gg/KHn6AbevZ2](https://discord.gg/KHn6AbevZ2)**

Or open an [issue](https://github.com/Nicolaslahri/onepacedownloader/issues) &middot; ping [u/nicolasenjah](https://www.reddit.com/user/nicolasenjah/) on Reddit

<br />

*Made with care by Nicolas*

</div>
