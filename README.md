# One Pace Downloader

Small Windows app I built for grabbing [One Pace](https://onepace.net) arcs. Click an arc, pick a quality, it dumps everything to a folder. Also handles [Muhn Pace](https://www.reddit.com/r/onepace/comments/1rtpukk/one_pace_dub_watch_guide/) for arcs One Pace hasn't dubbed yet.

![screenshot](assets/screenshot.png)

## Download

Latest .exe → **[Releases page](https://github.com/Nicolaslahri/onepace/releases/latest)**

Double-click and you're in. Nothing to install. Source is in [`_source/`](_source/onepace_downloader.py) under MIT if you'd rather read or run it yourself.

## What it does

Downloading arcs from onepace.net by hand is a pain — every arc is split across a pile of separate links with a daily limit that turns long arcs like Wano into a multi-day project. This grabs them in one go.

Two sources, switchable from the dropdown:

- **One Pace** *(default)* — main fan re-cut. Sub for every arc Romance Dawn → Egghead, Dub for the newer arcs. Most users start here.
- **Muhn Pace** — fan-made English dub fillers for arcs One Pace hasn't dubbed (Enies Lobby → Wano). Pair with One Pace if you're watching dubbed. Full watch order is in [u/KPGNL's guide](https://www.reddit.com/r/onepace/comments/1rtpukk/one_pace_dub_watch_guide/) — the app links to it.

![muhn pace screenshot](assets/screenshot_muhn.png)

The app shows which episodes each Muhn Pace album covers (e.g. *"Eps 11–22 only — pair with One Pace for the full arc"*) so you don't end up with a hole.

## Using it

1. Open the .exe.
2. Pick a folder (or use the default `downloads/` next to the .exe).
3. Pick version (Sub / Dub / Dub-CC) and quality (1080p / 720p / 480p).
4. Hit **Download** on an arc, or **Download all arcs** to queue everything.

Close it mid-download and the next launch picks up where it stopped. Already-finished episodes are skipped. When new arcs drop on onepace.net, hit **Refresh**.

## Heads up

- Windows only.
- First launch, SmartScreen will warn — the .exe isn't signed (certs are expensive). Click **More info → Run anyway**, or right-click → Properties → tick **Unblock**.

## Downloads not starting?

Mostly Indian ISPs (Jio, Airtel, BSNL, ACT) hit this — app opens, downloads never start. Almost always ISP-level DNS blocking of the CDN.

**Two-minute fix — switch Windows DNS to Cloudflare:**
Settings → Network & Internet → your active connection → DNS server assignment → **Edit** → Manual → IPv4 on → Preferred `1.1.1.1`, Alternate `1.0.0.1` → Save, reconnect.

Fixes ~90% of cases. Set-and-forget alternative: [Cloudflare WARP](https://1.1.1.1/). Last resort, any VPN works.

If none of those help, paste the Log panel contents into [Discord](https://discord.gg/JvaCyYbbSk).

## Is it safe?

Fair question. Verify yourself.

**SHA256:** `3fd42c1fe6186f1792e8d70b52f41fff8b6317ddb762bae4e61592bf42afc845`

[![VirusTotal](https://img.shields.io/badge/VirusTotal-6%2F75-yellow?logo=virustotal&logoColor=white)](https://www.virustotal.com/gui/file/3fd42c1fe6186f1792e8d70b52f41fff8b6317ddb762bae4e61592bf42afc845)

69 engines clean — Bitdefender, ESET, Sophos, Symantec, Avast, AVG, Malwarebytes, plus 60+ others. The 6 flags are heuristic / AI scanners (Cylance, CrowdStrike Falcon Static AI, SentinelOne Static AI, APEX) plus Microsoft Defender's cloud ML model flagging `Trojan:Win32/Wacatac.B!ml`. The `!ml` suffix means machine-learning guess, not a signature match — common false-positive label for unsigned solo-dev tools. I've submitted it to Microsoft; they usually whitelist within ~3 days. If Defender quarantines in the meantime, **Allow on device** in the notification, or add the .exe to Exclusions.

Don't trust the badge? Drop the .exe onto [virustotal.com](https://www.virustotal.com) yourself, or read the [full Python source](_source/onepace_downloader.py) — one file, stdlib only, MIT.

### What if my AV flags `cdn.pixeldrain.eu.cc` while the app is downloading?

Different question — that's the CDN, not the .exe. `pixeldrain.eu.cc` is the no-cap mirror of pixeldrain.com (same files, unofficial host), which is the only way to grab a full arc without hitting the 6 GB/day limit. Some AVs (Bitdefender, Norton seen so far) flag the domain on reputation because it has no track record, not because there's a payload on it. Allow the domain in your AV's web protection, or pause web shield during the download.

## Built on the work of

This app is just a downloader — none of the actual content is mine. Huge thanks to:

- **The [One Pace team](https://onepace.net)** — for ten-plus years of re-cutting One Piece into the version everyone wishes Toei would air. Every Sub episode comes from their releases.
- **Muhny (D Goat)** — for editing Muhn Pace, the dub fillers covering arcs One Pace hasn't dubbed (Enies Lobby → Wano). About 184 GB of careful audio work that's quietly saved a lot of dub watchers months of waiting.
- **[u/KPGNL](https://www.reddit.com/user/KPGNL/)** — for maintaining the [dub watch-order guide](https://www.reddit.com/r/onepace/comments/1rtpukk/one_pace_dub_watch_guide/) the app links to. Original version put together by **u/AlternativeAd1098**.

If you find One Pace or Muhn Pace useful, drop them a thank-you wherever they hang out — that's worth more than anything I could do.

## Found a bug / want to chat

Discord is fastest: **[discord.gg/JvaCyYbbSk](https://discord.gg/JvaCyYbbSk)**. Or open an [issue](https://github.com/Nicolaslahri/onepace/issues), or ping [u/nicolasenjah](https://www.reddit.com/user/nicolasenjah/) on Reddit.

— Nicolas
