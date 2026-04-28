# One Pace Downloader

A one-click Windows app to download full **One Pace** arcs without the 6 GB/day Pixeldrain limit.

> One Pace is a fan project that re-cuts One Piece to remove filler. The official downloads at [onepace.net](https://onepace.net/en/watch) are scattered across many Pixeldrain albums and capped at 6 GB per day per IP. This tool lists every arc, lets you pick a quality, and downloads everything to a folder of your choice — no cap, no fuss.

---

## Download

➡ **[OnePaceDownloader.exe](OnePaceDownloader.exe)** — single file, no install, no Python required.

Just download and double-click.

---

## How to use

1. Run **OnePaceDownloader.exe**.
2. Pick a save folder (or use the default `downloads/` next to the .exe).
3. Pick **Version** (English Subtitles / English Dub / English Dub with CC) and **Quality** (1080p / 720p / 480p).
4. Click **Download** on any arc — or **Download all arcs** to grab everything.

That's it.

---

## Features

- **All 36 arcs** from Romance Dawn through Egghead, sourced live from onepace.net.
- **No 6 GB/day cap** — uses the GameDrive bypass CDN.
- **Resumable** — kill the app mid-download and the next launch picks up exactly where it left off.
- **Auto-retry** on flaky connections (5 attempts with exponential backoff).
- **Auto-fallback** to the best available quality when your pick isn't available for an arc.
- **Refresh** button re-scrapes onepace.net so new arcs/qualities show up automatically.
- **Skip already-downloaded** files — re-running the app never re-downloads what's already complete.

---

## Follow for updates

- 💬 **Discord** — [discord.gg/JvaCyYbbSk](https://discord.gg/JvaCyYbbSk)
- 🟠 **Reddit** — [u/nicolasenjah](https://www.reddit.com/user/nicolasenjah/)

New tools and One Pace updates get posted there first.

---

## FAQ

**Is this safe?** Yes — it's just a downloader. It talks to the same Pixeldrain that onepace.net itself uses, just routed through a public bypass CDN.

**Why does Windows warn me when I run it?** Because the .exe isn't code-signed (signing certs cost $$$ for solo devs). Click **More info → Run anyway** in SmartScreen, or right-click → Properties → Unblock.

**Will downloaded files play in VLC / my TV?** Yes, they're standard `.mp4` files exactly as One Pace publishes them.

**The arc I want isn't in the list / a quality is missing.** Click **Refresh from onepace.net** in the app — that pulls the latest arc list and Pixeldrain links.

---

Made by **Nicolas** · Free forever.
