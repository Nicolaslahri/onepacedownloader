# One Pace Downloader

Small Windows app I built for grabbing [One Pace](https://onepace.net) arcs. Click an arc, pick a quality, it dumps everything to a folder.

![screenshot](assets/screenshot.png)

## Download

Latest .exe → **[Releases page](https://github.com/Nicolaslahri/onepace/releases/latest)**

Double-click and you're in. Nothing to install.

## Why I made this

Downloading arcs from onepace.net is a pain. Every arc is split across a pile of separate links, and there's a daily limit that turns long arcs like Wano into a multi-day project. This just gets them in one go.

## Using it

1. Open the .exe.
2. Pick a folder (or use the default `downloads/` next to the .exe).
3. Pick the version (Sub / Dub / Dub-CC) and quality (1080p / 720p / 480p) at the top.
4. Hit **Download** on whichever arc you want, or **Download all arcs** to queue everything.

If you close it mid-download, the next launch picks up where it stopped. Already-finished episodes are skipped.

## Heads up

- Windows only for now.
- First time you run it, SmartScreen will throw a warning. The .exe isn't signed (signing certs are expensive). Click **More info → Run anyway**, or right-click the .exe → Properties → tick **Unblock**.
- When new arcs drop on onepace.net, hit the **Refresh** button in the app and they show up.

## Is it safe?

Fair question — unsigned .exe from the internet. Verify yourself:

**SHA256:** `e05e149f49ee5fbf4fa4cdf0c48cb5c3f9707fc043d584521173740c81bddd37`

**Microsoft Defender:** clean — *no threats found.*
```
> MpCmdRun.exe -Scan -ScanType 3 -File OnePaceDownloader.exe
Scan starting...
Scan finished.
Scanning C:\...\OnePaceDownloader.exe found no threats.
```

**VirusTotal:** [scan results by hash](https://www.virustotal.com/gui/file/e05e149f49ee5fbf4fa4cdf0c48cb5c3f9707fc043d584521173740c81bddd37) — if the page is empty, drop the .exe onto [virustotal.com](https://www.virustotal.com) and the link will populate in ~30 sec.

Heads up: some heuristic AV engines flag *any* small unsigned tool by default. Microsoft + the broad consensus on VT is what to trust.

## Found a bug / want to chat

Discord is the fastest: **[discord.gg/JvaCyYbbSk](https://discord.gg/JvaCyYbbSk)**

Or open an [issue](https://github.com/Nicolaslahri/onepace/issues) here. Reddit's [u/nicolasenjah](https://www.reddit.com/user/nicolasenjah/) too.

— Nicolas
