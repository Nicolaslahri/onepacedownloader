# One Pace Downloader

[![Discord](https://img.shields.io/badge/Discord-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.gg/KHn6AbevZ2)
[![Reddit](https://img.shields.io/badge/Reddit-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/user/nicolasenjah/)

Small Windows app I built for grabbing [One Pace](https://onepace.net) arcs. Pick the episodes you want, hit download — files land in a folder with real episode titles, or straight into your Plex / Jellyfin library if you'd rather. Also handles [Muhn Pace](https://www.reddit.com/r/onepace/comments/1rtpukk/one_pace_dub_watch_guide/) dub fillers and pulls torrents from [nyaa.si](https://nyaa.si/?q=one+pace) for users who'd rather seed.

![screenshot](assets/screenshot.png)

## Download

Latest .exe → **[Releases page](https://github.com/Nicolaslahri/onepacedownloader/releases/latest)**

Double-click and you're in. Nothing to install. Source is in [`_source/`](_source/onepace_downloader.py) under MIT if you'd rather read or run it yourself.

## What it does

Downloading arcs from onepace.net by hand is a pain — every arc is split across a pile of separate links with a daily limit that turns long arcs like Wano into a multi-day project. This grabs them in one go.

Three sources, switchable from the dropdown:

- **One Pace** *(default)* — main fan re-cut. Sub for every arc Romance Dawn → Egghead, Dub for the newer arcs. Most users start here.
- **Muhn Pace** — fan-made English dub fillers for arcs One Pace hasn't dubbed (Enies Lobby → Wano). Pair with One Pace if you're watching dubbed. Full watch order is in [u/KPGNL's guide](https://www.reddit.com/r/onepace/comments/1rtpukk/one_pace_dub_watch_guide/) — the app links to it.
- **Nyaa** — torrents from [nyaa.si](https://nyaa.si/?q=one+pace), grouped by arc. Click **Open magnet** and it hands the link to your default torrent client (qBittorrent / uTorrent / etc.). Useful when pixeldrain is throttled, or when you want to seed back. Needs a torrent client installed — magnets are copied to clipboard if none is registered.

The app shows which episodes each Muhn Pace album covers (e.g. *"Eps 11–22 only — pair with One Pace for the full arc"*) so you don't end up with a hole.

## Using it

1. Open the .exe.
2. Pick a folder (or use the default `downloads/` next to the .exe).
3. Click an arc on the left, tick the episodes you want in the middle.
4. Hit **Download selected** — confirm version (Sub / Dub / Dub-CC) and quality (1080p / 720p / 480p) in the dialog, go.

![confirm batch download dialog](assets/screenshot_dialog.png)

Total size up front so you know what you're committing to. Once it's going, the right column turns into a live status panel:

![download in progress](assets/screenshot_downloading.png)

Close it mid-download and the next launch picks up where it stopped. Already-finished episodes show a **✓ Saved** chip and arcs show a per-arc progress count (`✓ 12/35`). When new arcs drop on onepace.net, hit **Refresh**.

## Plex / Jellyfin users

Open **Settings → Output organization** and tick **Organize for Plex / Jellyfin**. From then on, every download lands in the layout your media server expects:

```
downloads/
  One Pace/
    Season 14/
      One Pace - s14e01 - Sir Crocodile, the Pirate.mkv
      One Pace - s14e01 - Sir Crocodile, the Pirate.nfo
      ...
```

Each `.nfo` carries the real episode title and plot (sourced from [SpykerNZ/one-pace-for-plex](https://github.com/SpykerNZ/one-pace-for-plex)), so Plex and Jellyfin recognize the show and pull artwork automatically. The arc list shows a per-arc progress badge (`✓ 12/35`) so you can see at a glance what's done.

## Heads up

- Windows only.
- First launch, SmartScreen will warn — the .exe isn't signed (certs are expensive). Click **More info → Run anyway**, or right-click → Properties → tick **Unblock**.

## Downloads not starting?

Mostly Indian ISPs (Jio, Airtel, BSNL, ACT) hit this — app opens, downloads never start. Almost always ISP-level DNS blocking of the CDN.

**Two-minute fix — switch Windows DNS to Cloudflare:**
Settings → Network & Internet → your active connection → DNS server assignment → **Edit** → Manual → IPv4 on → Preferred `1.1.1.1`, Alternate `1.0.0.1` → Save, reconnect.

Fixes ~90% of cases. Set-and-forget alternative: [Cloudflare WARP](https://1.1.1.1/). Last resort, any VPN works.

If you'd rather not edit Windows network settings yourself, the app has a one-click DNS switcher built in — top right → **DNS** → pick Cloudflare or Google. UAC prompt, done. Revert from the same panel.

![DNS switcher panel](assets/screenshot_dns.png)

If none of those help, paste the Log panel contents into [Discord](https://discord.gg/KHn6AbevZ2).

## Is it safe?

Fair question. Verify yourself.

**SHA256:** `e6f90aff7226c9ddf98c36419eac1b84fb11bdb614d978677747289e6dd3c9cf`

[![VirusTotal](https://img.shields.io/badge/VirusTotal-scan-blue?logo=virustotal&logoColor=white)](https://www.virustotal.com/gui/file/e6f90aff7226c9ddf98c36419eac1b84fb11bdb614d978677747289e6dd3c9cf)

Most engines clean — Bitdefender, ESET, Sophos, Symantec, Avast, AVG, Malwarebytes, Microsoft Defender, and others tend to pass. A handful of heuristic / static-analysis scanners (APEX, Bkav, CrowdStrike Falcon, Cylance, SentinelOne Static AI, Yandex) sometimes flag PyInstaller `--onefile` builds based on packed-binary patterns rather than actual malicious behavior — a common false-positive label for unsigned solo-dev tools. Click the badge to see the current scan.

Don't trust the badge? Drop the .exe onto [virustotal.com](https://www.virustotal.com) yourself, or read the [full Python source](_source/onepace_downloader.py) — one file, stdlib only, MIT.

### What if my AV flags `cdn.pixeldrain.eu.cc` while the app is downloading?

Different question — that's the CDN, not the .exe. `pixeldrain.eu.cc` is the no-cap mirror of pixeldrain.com (same files, unofficial host), which is the only way to grab a full arc without hitting the 6 GB/day limit. Some AVs (Bitdefender, Norton seen so far) flag the domain on reputation because it has no track record, not because there's a payload on it. Allow the domain in your AV's web protection, or pause web shield during the download.

## Built on the work of

This app is just a downloader — none of the actual content is mine. Huge thanks to:

- **The [One Pace team](https://onepace.net)** — for ten-plus years of re-cutting One Piece into the version everyone wishes Toei would air. Every Sub episode comes from their releases.
- **Muhny (D Goat)** — for editing Muhn Pace, the dub fillers covering arcs One Pace hasn't dubbed (Enies Lobby → Wano). About 184 GB of careful audio work that's quietly saved a lot of dub watchers months of waiting.
- **[u/KPGNL](https://www.reddit.com/user/KPGNL/)** — for maintaining the [dub watch-order guide](https://www.reddit.com/r/onepace/comments/1rtpukk/one_pace_dub_watch_guide/) the app links to. Original version put together by **u/AlternativeAd1098**.
- **[SpykerNZ](https://github.com/SpykerNZ/one-pace-for-plex)** — the canonical episode titles, plots, and Plex/Jellyfin season layout the *Organize for media server* option uses come straight from his repo. If you're a Plex/Jellyfin user, also check it out for the artwork.

If you find One Pace or Muhn Pace useful, drop them a thank-you wherever they hang out — that's worth more than anything I could do.

## Found a bug / want to chat

Discord is fastest: **[discord.gg/JvaCyYbbSk](https://discord.gg/KHn6AbevZ2)**. Or open an [issue](https://github.com/Nicolaslahri/onepacedownloader/issues), or ping [u/nicolasenjah](https://www.reddit.com/user/nicolasenjah/) on Reddit.

— Nicolas
