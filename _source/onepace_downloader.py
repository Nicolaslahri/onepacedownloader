"""One Pace Downloader — pulls full arcs from onepace.net via the
pixeldrain.eu.cc bypass CDN (no 6 GB/day cap)."""

from __future__ import annotations

import html
import json
import os
import queue
import re
import sys
import threading
import time
import tkinter as tk
import urllib.error
import urllib.parse
import urllib.request
import webbrowser
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

DISCORD_URL = "https://discord.gg/JvaCyYbbSk"
REDDIT_URL = "https://www.reddit.com/user/nicolasenjah/"

def _user_dir() -> Path:
    """Writable folder next to the .exe (or .py during dev)."""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def _bundle_dir() -> Path:
    """Read-only folder where bundled assets live (inside _MEIPASS when frozen)."""
    if getattr(sys, "frozen", False):
        return Path(getattr(sys, "_MEIPASS", _user_dir()))
    return Path(__file__).resolve().parent


APP_DIR = _user_dir()
BUNDLED_ARCS_FILE = _bundle_dir() / "arcs.json"
BUNDLED_MUHN_FILE = _bundle_dir() / "muhn_arcs.json"
BUNDLED_ICON_ICO = _bundle_dir() / "icon.ico"
BUNDLED_ICON_PNG = _bundle_dir() / "icon.png"
ARCS_FILE = APP_DIR / "arcs.json"
MUHN_FILE = APP_DIR / "muhn_arcs.json"
CONFIG_FILE = APP_DIR / "config.json"
DEFAULT_DOWNLOADS = APP_DIR / "downloads"

# Sources
SRC_ONE_PACE = "One Pace"
SRC_MUHN_PACE = "Muhn Pace"
SOURCE_LABELS = {
    SRC_ONE_PACE:  "One Pace  (Sub for every arc, Dub for newer arcs)",
    SRC_MUHN_PACE: "Muhn Pace  (English Dub fillers for arcs One Pace hasn't dubbed)",
}
SOURCE_INFO = {
    SRC_ONE_PACE:
        "Main fan re-cut.  Sub for every arc Romance Dawn → Egghead, "
        "Dub for the newer arcs.  Recommended for sub watchers.",
    SRC_MUHN_PACE:
        "Fan-made English Dub for arcs One Pace hasn't dubbed yet "
        "(Enies Lobby → Wano).  Most users pair this with One Pace "
        "— check the watch-order guide to know which arcs to grab from each.",
}
DUB_GUIDE_URL = "https://www.reddit.com/r/onepace/comments/1rtpukk/one_pace_dub_watch_guide/"

# Theme palette
HEADER_BG = "#0E1627"        # deep navy — ship hull
HEADER_FG = "#F8F4E8"        # cream
HEADER_ACCENT = "#E5B85A"    # straw gold (matches hat ribbon)
PRIMARY = "#C8232A"          # ribbon red — download buttons
PRIMARY_HOVER = "#A11C22"
MUTED = "#7B8597"

ONEPACE_URL = "https://onepace.net/en/watch"
PIXELDRAIN_API = "https://pixeldrain.com/api/list/{album_id}"
BYPASS_FILE = "https://cdn.pixeldrain.eu.cc/{file_id}"

UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
)

VERSIONS = ["English Subtitles", "English Dub", "English Dub with Closed Captions"]
QUALITIES = ["1080p", "720p", "480p"]


# ---------------------------------------------------------------- helpers ---

_TRANSIENT = (
    urllib.error.URLError,
    ConnectionError,
    TimeoutError,
    OSError,  # WinError 10054 surfaces as ConnectionResetError -> OSError
)


def http_get(url: str, *, timeout: int = 30, retries: int = 5) -> bytes:
    last: Exception | None = None
    for attempt in range(retries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read()
        except _TRANSIENT as e:
            last = e
            time.sleep(min(2 ** attempt, 15))
    raise last if last else RuntimeError("http_get failed")


def open_stream(url: str, *, start: int = 0, timeout: int = 60, retries: int = 5):
    headers = {"User-Agent": UA}
    if start:
        headers["Range"] = f"bytes={start}-"
    req = urllib.request.Request(url, headers=headers)
    last: Exception | None = None
    for attempt in range(retries):
        try:
            return urllib.request.urlopen(req, timeout=timeout)
        except _TRANSIENT as e:
            last = e
            time.sleep(min(2 ** attempt, 15))
    raise last if last else RuntimeError("open_stream failed")


def sanitize_filename(name: str) -> str:
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f]', "_", name).strip(" .")
    return name or "untitled"


def fmt_bytes(n: float) -> str:
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024:
            return f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} PB"


# ------------------------------------------------------------- arc loader ---

def parse_arcs_from_html(page_html: str) -> list[dict]:
    arc_re = re.compile(
        r"<h2[^>]*>\s*<a[^>]*>([^<]+?)</a>\s*</h2>(.*?)(?=<h2[^>]*>\s*<a|$)",
        re.DOTALL,
    )
    res_re = re.compile(
        r'<span class="flex-1">([^<]+)</span>(.*?)'
        r'(?=<span class="flex-1">|</ul></li><li |$)',
        re.DOTALL,
    )
    link_re = re.compile(
        r'href="(https://pixeldrain\.net/l/([A-Za-z0-9]+))"[^>]*>.*?'
        r'<span class="grow text-center tabular-nums">\s*([0-9]+p)\s*</span>',
        re.DOTALL,
    )

    arcs: list[dict] = []
    for m in arc_re.finditer(page_html):
        title = html.unescape(m.group(1).strip())
        block = m.group(2)
        if "pixeldrain" not in block:
            continue
        resources: dict[str, dict[str, str]] = {}
        for r in res_re.finditer(block):
            name = r.group(1).strip()
            qs: dict[str, str] = {}
            for lk in link_re.finditer(r.group(2)):
                qs[lk.group(3)] = lk.group(2)
            if qs:
                resources[name] = qs
        if resources:
            arcs.append({"title": title, "resources": resources})
    return arcs


def load_arcs() -> list[dict]:
    # Prefer the user-writable copy (refreshed from the web); fall back to bundled.
    for p in (ARCS_FILE, BUNDLED_ARCS_FILE):
        if p.exists():
            return json.loads(p.read_text(encoding="utf-8"))
    return []


def load_muhn_arcs() -> list[dict]:
    """Muhn Pace data is curated (no live scrape) — bundled JSON only."""
    for p in (MUHN_FILE, BUNDLED_MUHN_FILE):
        if p.exists():
            return json.loads(p.read_text(encoding="utf-8"))
    return []


def refresh_arcs_from_web() -> list[dict]:
    page = http_get(ONEPACE_URL, timeout=60).decode("utf-8", errors="replace")
    arcs = parse_arcs_from_html(page)
    if not arcs:
        raise RuntimeError("Could not parse any arcs from onepace.net (page format changed?)")
    ARCS_FILE.write_text(json.dumps(arcs, indent=2, ensure_ascii=False), encoding="utf-8")
    return arcs


# -------------------------------------------------------------- downloads ---

class DownloadCancelled(Exception):
    pass


class Downloader:
    """Downloads a pixeldrain album via the bypass CDN, file by file."""

    def __init__(
        self,
        album_id: str,
        dest_dir: Path,
        *,
        on_status,
        on_progress,
        on_log,
        cancel_evt: threading.Event,
    ) -> None:
        self.album_id = album_id
        self.dest_dir = dest_dir
        self.on_status = on_status
        self.on_progress = on_progress
        self.on_log = on_log
        self.cancel_evt = cancel_evt

    def fetch_album(self) -> dict:
        data = json.loads(http_get(PIXELDRAIN_API.format(album_id=self.album_id)))
        if not data.get("success", True) and "files" not in data:
            raise RuntimeError(f"Pixeldrain API error: {data}")
        return data

    def run(self) -> None:
        album = self.fetch_album()
        files = album.get("files", [])
        title = album.get("title") or self.album_id
        target = self.dest_dir / sanitize_filename(title)
        target.mkdir(parents=True, exist_ok=True)
        self.on_log(f"Album: {title} ({len(files)} files) -> {target}")

        total = len(files)
        for idx, f in enumerate(files, 1):
            if self.cancel_evt.is_set():
                raise DownloadCancelled()
            name = sanitize_filename(f["name"])
            size = int(f.get("size", 0))
            url = BYPASS_FILE.format(file_id=f["id"])
            out = target / name
            self.on_status(f"[{idx}/{total}] {name}")
            self._download_one(url, out, size, idx, total)

        self.on_log(f"Done: {title}")

    def _download_one(self, url: str, out: Path, size: int, idx: int, total: int) -> None:
        partial = out.with_suffix(out.suffix + ".part")
        if out.exists() and (size == 0 or out.stat().st_size == size):
            self.on_log(f"  skip (already complete): {out.name}")
            self.on_progress(1.0, "already downloaded", idx, total, 0)
            return

        start = partial.stat().st_size if partial.exists() else 0
        attempt = 0
        last_exc: Exception | None = None
        while attempt < 5:
            attempt += 1
            try:
                self._stream_to(url, partial, start, size, idx, total)
                if size and partial.stat().st_size != size:
                    raise IOError(
                        f"size mismatch: expected {size}, got {partial.stat().st_size}"
                    )
                if out.exists():
                    out.unlink()
                partial.rename(out)
                return
            except DownloadCancelled:
                raise
            except Exception as e:
                last_exc = e
                start = partial.stat().st_size if partial.exists() else 0
                wait = min(2 ** attempt, 30)
                self.on_log(f"  retry {attempt}/5 after error: {e} (sleep {wait}s)")
                if self.cancel_evt.wait(wait):
                    raise DownloadCancelled()
        raise RuntimeError(f"failed after 5 attempts: {last_exc}")

    def _stream_to(
        self, url: str, partial: Path, start: int, size: int, idx: int, total: int
    ) -> None:
        with open_stream(url, start=start) as resp:
            mode = "ab" if start else "wb"
            t0 = time.time()
            t_last = t0
            bytes_now = start
            with open(partial, mode) as f:
                while True:
                    if self.cancel_evt.is_set():
                        raise DownloadCancelled()
                    chunk = resp.read(64 * 1024)
                    if not chunk:
                        break
                    f.write(chunk)
                    bytes_now += len(chunk)
                    now = time.time()
                    if now - t_last >= 0.2:
                        speed = (bytes_now - start) / max(now - t0, 0.001)
                        frac = bytes_now / size if size else 0.0
                        self.on_progress(frac, fmt_bytes(speed) + "/s", idx, total, bytes_now)
                        t_last = now
            speed = (bytes_now - start) / max(time.time() - t0, 0.001)
            self.on_progress(1.0, fmt_bytes(speed) + "/s", idx, total, bytes_now)


# ------------------------------------------------------------- config -----

def load_config() -> dict:
    if CONFIG_FILE.exists():
        try:
            return json.loads(CONFIG_FILE.read_text(encoding="utf-8"))
        except Exception:
            pass
    return {}


def save_config(cfg: dict) -> None:
    CONFIG_FILE.write_text(json.dumps(cfg, indent=2), encoding="utf-8")


# ---------------------------------------------------------------- gui -----

class App(tk.Tk):
    def __init__(self) -> None:
        super().__init__()
        self.title("One Pace Downloader  —  Made by Nicolas")
        self.geometry("900x720")
        self.minsize(760, 560)
        self._apply_icon()
        self._configure_styles()

        self.config_data = load_config()
        self.arcs = load_arcs()
        self.muhn_arcs = load_muhn_arcs()

        self.save_dir = tk.StringVar(
            value=self.config_data.get("save_folder", str(DEFAULT_DOWNLOADS))
        )
        self.version = tk.StringVar(
            value=self.config_data.get("default_version", "English Subtitles")
        )
        self.quality = tk.StringVar(
            value=self.config_data.get("default_quality", "1080p")
        )
        self.source = tk.StringVar(
            value=self.config_data.get("source", SRC_ONE_PACE)
        )

        self.cancel_evt = threading.Event()
        self.worker: threading.Thread | None = None
        self.ui_queue: queue.Queue = queue.Queue()

        self._build_ui()
        self._apply_source_state()
        self._update_info_banner()
        self._refresh_arc_rows()
        self.after(80, self._drain_ui_queue)

        if not self.arcs:
            self._log("No local arcs.json found — click 'Refresh from onepace.net'.")

    # ---- UI construction ----

    def _apply_icon(self) -> None:
        # Title bar icon (Windows) — silently skip if asset missing in dev.
        if BUNDLED_ICON_ICO.exists():
            try:
                self.iconbitmap(default=str(BUNDLED_ICON_ICO))
            except tk.TclError:
                pass
        if BUNDLED_ICON_PNG.exists():
            try:
                self._app_icon = tk.PhotoImage(file=str(BUNDLED_ICON_PNG))
                self.iconphoto(True, self._app_icon)
            except tk.TclError:
                self._app_icon = None
        else:
            self._app_icon = None

    def _configure_styles(self) -> None:
        style = ttk.Style(self)
        try:
            style.theme_use("clam")  # most consistent across Windows for color overrides
        except tk.TclError:
            pass

        # Info banner under the Source picker.
        style.configure("Info.TFrame", background="#FBF6E8")
        style.configure("Info.TLabel",
                        background="#FBF6E8", foreground="#6B5A1F",
                        font=("Segoe UI", 9))
        style.configure("InfoLink.TLabel",
                        background="#FBF6E8", foreground="#0066CC",
                        font=("Segoe UI", 9, "underline"))

        # Primary action button (Download / Download all) — ribbon red.
        style.configure(
            "Primary.TButton",
            background=PRIMARY, foreground="#FFFFFF",
            borderwidth=0, focusthickness=0, padding=(14, 7),
            font=("Segoe UI", 10, "bold"),
        )
        style.map(
            "Primary.TButton",
            background=[("active", PRIMARY_HOVER), ("disabled", "#9CA3AF")],
            foreground=[("disabled", "#E5E7EB")],
        )

        # Subtle outline button (Browse, Open folder, Refresh).
        style.configure(
            "Ghost.TButton",
            padding=(10, 6),
            font=("Segoe UI", 9),
        )

        # Header label styles
        style.configure("Header.TFrame", background=HEADER_BG)
        style.configure("HeaderTitle.TLabel",
                        background=HEADER_BG, foreground=HEADER_FG,
                        font=("Segoe UI", 18, "bold"))
        style.configure("HeaderTagline.TLabel",
                        background=HEADER_BG, foreground=HEADER_ACCENT,
                        font=("Segoe UI", 10))
        style.configure("HeaderCredit.TLabel",
                        background=HEADER_BG, foreground=MUTED,
                        font=("Segoe UI", 9, "italic"))

    def _build_ui(self) -> None:
        pad = {"padx": 12, "pady": 6}

        # ---- HEADER ----
        header = ttk.Frame(self, style="Header.TFrame", padding=(16, 12, 16, 12))
        header.pack(fill="x")
        if self._app_icon is not None:
            # Resize the icon to a sane header size by using subsample
            # (PhotoImage subsample is integer factors; 512/72 ≈ 7).
            try:
                hdr_icon = self._app_icon.subsample(7, 7)
                self._hdr_icon_ref = hdr_icon  # keep reference
                tk.Label(
                    header, image=hdr_icon, bg=HEADER_BG, bd=0,
                ).pack(side="left", padx=(0, 14))
            except tk.TclError:
                pass
        text_block = ttk.Frame(header, style="Header.TFrame")
        text_block.pack(side="left", fill="y", expand=False)
        ttk.Label(text_block, text="One Pace Downloader",
                  style="HeaderTitle.TLabel").pack(anchor="w")
        ttk.Label(text_block,
                  text="Grab full arcs in one click.  No daily limit.",
                  style="HeaderTagline.TLabel").pack(anchor="w", pady=(2, 0))
        ttk.Label(header, text="Made by Nicolas",
                  style="HeaderCredit.TLabel").pack(side="right", anchor="se")

        # ---- SOURCE PICKER + INFO BANNER ----
        src = ttk.Frame(self)
        src.pack(fill="x", padx=12, pady=(10, 4))
        ttk.Label(src, text="Source:", font=("Segoe UI", 10, "bold")).pack(side="left")
        self.source_combo = ttk.Combobox(
            src, textvariable=self.source,
            values=[SOURCE_LABELS[SRC_ONE_PACE], SOURCE_LABELS[SRC_MUHN_PACE]],
            state="readonly", width=64,
        )
        # Set the visible text based on the stored source key
        self.source_combo.set(SOURCE_LABELS[self._current_source()])
        self.source_combo.pack(side="left", padx=(8, 0))
        self.source_combo.bind("<<ComboboxSelected>>", self._on_source_change)

        info = ttk.Frame(self, style="Info.TFrame", padding=(12, 8, 12, 8))
        info.pack(fill="x", padx=12, pady=(0, 6))
        self.info_label = ttk.Label(info, text="", style="Info.TLabel", wraplength=820, justify="left")
        self.info_label.pack(side="left", fill="x", expand=True, anchor="w")
        self.info_link = ttk.Label(info, text="Open dub watch-order guide ↗",
                                    style="InfoLink.TLabel", cursor="hand2")
        self.info_link.bind("<Button-1>", lambda _e: webbrowser.open(DUB_GUIDE_URL))
        # Link is shown only for Muhn Pace; gets packed/unpacked by _update_info_banner.
        self._info_link_packed = False

        top = ttk.Frame(self)
        top.pack(fill="x", **pad)

        ttk.Label(top, text="Save to:").grid(row=0, column=0, sticky="w")
        self.path_entry = ttk.Entry(top, textvariable=self.save_dir)
        self.path_entry.grid(row=0, column=1, sticky="ew", padx=(6, 6))
        ttk.Button(top, text="Browse…", command=self._pick_folder).grid(row=0, column=2)
        ttk.Button(top, text="Open folder", command=self._open_folder).grid(row=0, column=3, padx=(6, 0))
        top.columnconfigure(1, weight=1)

        opt = ttk.Frame(self)
        opt.pack(fill="x", **pad)
        ttk.Label(opt, text="Version:").grid(row=0, column=0, sticky="w")
        self.version_combo = ttk.Combobox(opt, textvariable=self.version,
                                          values=VERSIONS, state="readonly", width=34)
        self.version_combo.grid(row=0, column=1, padx=(6, 16))
        self.version_combo.bind("<<ComboboxSelected>>", lambda _e: self._refresh_arc_rows())

        ttk.Label(opt, text="Quality:").grid(row=0, column=2, sticky="w")
        self.quality_combo = ttk.Combobox(opt, textvariable=self.quality,
                                          values=QUALITIES, state="readonly", width=8)
        self.quality_combo.grid(row=0, column=3, padx=(6, 16))
        self.quality_combo.bind("<<ComboboxSelected>>", lambda _e: self._refresh_arc_rows())

        self.refresh_btn = ttk.Button(opt, text="Refresh from onepace.net",
                                       style="Ghost.TButton", command=self._refresh_arcs)
        self.refresh_btn.grid(row=0, column=4, padx=(0, 6))
        ttk.Button(opt, text="Download all arcs",
                   style="Primary.TButton",
                   command=self._download_all).grid(row=0, column=5)

        # Arc list
        body = ttk.LabelFrame(self, text="Arcs")
        body.pack(fill="both", expand=True, **pad)

        self.canvas = tk.Canvas(body, highlightthickness=0)
        scroll = ttk.Scrollbar(body, orient="vertical", command=self.canvas.yview)
        self.canvas.configure(yscrollcommand=scroll.set)
        self.canvas.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")

        self.rows_frame = ttk.Frame(self.canvas)
        self.rows_window = self.canvas.create_window((0, 0), window=self.rows_frame, anchor="nw")
        self.rows_frame.bind(
            "<Configure>",
            lambda _e: self.canvas.configure(scrollregion=self.canvas.bbox("all")),
        )
        self.canvas.bind(
            "<Configure>",
            lambda e: self.canvas.itemconfig(self.rows_window, width=e.width),
        )
        self.canvas.bind_all("<MouseWheel>", self._on_mousewheel)

        # Progress + log
        bot = ttk.Frame(self)
        bot.pack(fill="x", **pad)
        self.status_var = tk.StringVar(value="Ready.")
        self.progress_var = tk.DoubleVar(value=0)
        ttk.Label(bot, textvariable=self.status_var).pack(anchor="w")
        self.progress = ttk.Progressbar(bot, variable=self.progress_var, maximum=1.0)
        self.progress.pack(fill="x", pady=(2, 6))

        btnrow = ttk.Frame(bot)
        btnrow.pack(fill="x")
        self.cancel_btn = ttk.Button(btnrow, text="Cancel", command=self._cancel, state="disabled")
        self.cancel_btn.pack(side="right")

        log_frame = ttk.LabelFrame(self, text="Log")
        log_frame.pack(fill="both", expand=False, **pad)
        self.log_box = tk.Text(log_frame, height=7, state="disabled", wrap="word")
        self.log_box.pack(fill="both", expand=True)

        # Follow footer (credit moved into the header)
        footer = ttk.Frame(self)
        footer.pack(fill="x", padx=12, pady=(0, 10))
        ttk.Label(
            footer,
            text="Enjoying the tool?  Follow for updates  →",
            foreground=MUTED,
        ).pack(side="left")
        ttk.Button(
            footer, text="Discord", style="Ghost.TButton", width=10,
            command=lambda: webbrowser.open(DISCORD_URL),
        ).pack(side="left", padx=(8, 4))
        ttk.Button(
            footer, text="Reddit", style="Ghost.TButton", width=10,
            command=lambda: webbrowser.open(REDDIT_URL),
        ).pack(side="left", padx=4)

    def _on_mousewheel(self, event: tk.Event) -> None:
        self.canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")

    # ---- source / info banner ----

    def _current_source(self) -> str:
        """Return canonical source key (SRC_ONE_PACE / SRC_MUHN_PACE) regardless of
        whether `self.source` holds the key or the user-facing dropdown label."""
        v = self.source.get()
        if v in SOURCE_LABELS:  # canonical key
            return v
        for key, label in SOURCE_LABELS.items():
            if v == label:
                return key
        return SRC_ONE_PACE

    def _on_source_change(self, _event=None) -> None:
        # Normalize the picker text back to a canonical key so config saves cleanly.
        chosen = self._current_source()
        self.source.set(chosen)
        self.source_combo.set(SOURCE_LABELS[chosen])
        self._apply_source_state()
        self._update_info_banner()
        self._refresh_arc_rows()
        self._persist_settings()

    def _apply_source_state(self) -> None:
        """Enable/disable controls based on the active source."""
        src = self._current_source()
        if src == SRC_MUHN_PACE:
            # Muhn Pace is dub-only; quality varies per album so we let the
            # downloader use whatever the album publishes. Lock the controls
            # so the user sees they don't apply.
            self.version_combo.set("English Dub")
            self.version_combo.configure(state="disabled")
            self.quality_combo.set("(varies)")
            self.quality_combo.configure(state="disabled")
            self.refresh_btn.configure(state="disabled")
        else:
            self.version_combo.configure(state="readonly")
            if self.version.get() not in VERSIONS:
                self.version.set("English Subtitles")
            self.quality_combo.configure(state="readonly", values=QUALITIES)
            if self.quality.get() not in QUALITIES:
                self.quality.set("1080p")
            self.refresh_btn.configure(state="normal")

    def _update_info_banner(self) -> None:
        src = self._current_source()
        self.info_label.configure(text=SOURCE_INFO[src])
        if src == SRC_MUHN_PACE:
            if not self._info_link_packed:
                self.info_link.pack(side="right", padx=(8, 0))
                self._info_link_packed = True
        elif self._info_link_packed:
            self.info_link.pack_forget()
            self._info_link_packed = False

    # ---- arc rows ----

    def _refresh_arc_rows(self) -> None:
        for w in self.rows_frame.winfo_children():
            w.destroy()
        src = self._current_source()
        if src == SRC_MUHN_PACE:
            if not self.muhn_arcs:
                ttk.Label(self.rows_frame, text="(no Muhn Pace data bundled)").pack(padx=10, pady=10)
                return
            for idx, arc in enumerate(self.muhn_arcs):
                self._build_muhn_row(idx, arc)
            return
        # One Pace
        if not self.arcs:
            ttk.Label(self.rows_frame, text="(no data — click Refresh)").pack(padx=10, pady=10)
            return
        ver = self.version.get()
        qual = self.quality.get()
        for idx, arc in enumerate(self.arcs):
            self._build_arc_row(idx, arc, ver, qual)

    def _build_arc_row(self, idx: int, arc: dict, version: str, quality: str) -> None:
        row = ttk.Frame(self.rows_frame, padding=(8, 4))
        row.grid(row=idx, column=0, sticky="ew")
        self.rows_frame.columnconfigure(0, weight=1)
        bg_tag = "even" if idx % 2 == 0 else "odd"
        if bg_tag == "even":
            row.configure(style="Even.TFrame")

        ttk.Label(row, text=arc["title"], width=42, anchor="w").grid(row=0, column=0, sticky="w")

        chosen_ver, chosen_qual, chosen_id, note = self._resolve_album(arc, version, quality)

        badge = []
        for v in VERSIONS:
            if v in arc["resources"]:
                qs = sorted(arc["resources"][v].keys(), key=lambda q: int(q[:-1]))
                short = {"English Subtitles": "Sub", "English Dub": "Dub",
                         "English Dub with Closed Captions": "Dub-CC"}[v]
                badge.append(f"{short}: {','.join(qs)}")
        ttk.Label(row, text="  •  ".join(badge), foreground="#666").grid(row=0, column=1, sticky="w", padx=(8, 8))

        if chosen_id is None:
            btn = ttk.Button(row, text="Not available",
                             style="Ghost.TButton", state="disabled")
        else:
            label = "Download"
            if note:
                label = f"Download ({note})"
            btn = ttk.Button(
                row, text=label, style="Primary.TButton",
                command=lambda a=arc, alb=chosen_id, cv=chosen_ver, cq=chosen_qual:
                    self._start_download_one(a, alb, cv, cq),
            )
        btn.grid(row=0, column=2, sticky="e")
        row.columnconfigure(1, weight=1)

    def _build_muhn_row(self, idx: int, arc: dict) -> None:
        row = ttk.Frame(self.rows_frame, padding=(8, 4))
        row.grid(row=idx, column=0, sticky="ew")
        self.rows_frame.columnconfigure(0, weight=1)

        ttk.Label(row, text=arc["title"], width=42, anchor="w").grid(row=0, column=0, sticky="w")

        # Badge: episode count + total size, then notes (gap-fill range etc.)
        size_gb = arc.get("total_bytes", 0) / 1024 / 1024 / 1024
        meta = f"{arc.get('file_count', '?')} eps  •  {size_gb:.1f} GB"
        ttk.Label(row, text=meta, foreground="#444").grid(
            row=0, column=1, sticky="w", padx=(8, 8))
        notes = arc.get("notes") or ""
        if notes:
            ttk.Label(row, text=notes, foreground=MUTED,
                      font=("Segoe UI", 9, "italic")).grid(
                row=1, column=1, sticky="w", padx=(8, 8))

        btn = ttk.Button(
            row, text="Download", style="Primary.TButton",
            command=lambda a=arc: self._start_download_one(
                {"title": a["title"]}, a["album_id"], "English Dub", "muhn"),
        )
        btn.grid(row=0, column=2, sticky="e", rowspan=2)
        row.columnconfigure(1, weight=1)

    @staticmethod
    def _resolve_album(arc: dict, version: str, quality: str) -> tuple[str, str, str | None, str]:
        """Return (version_used, quality_used, album_id_or_None, note).
        Falls back to the next-best combination if the requested one isn't present."""
        if version in arc["resources"] and quality in arc["resources"][version]:
            return version, quality, arc["resources"][version][quality], ""

        # same version, best other quality
        if version in arc["resources"]:
            qs = arc["resources"][version]
            best = sorted(qs.keys(), key=lambda q: int(q[:-1]), reverse=True)[0]
            return version, best, qs[best], f"only {best}"

        # other version, requested quality if possible, else best
        for alt in VERSIONS:
            if alt in arc["resources"] and quality in arc["resources"][alt]:
                short = {"English Subtitles": "Sub", "English Dub": "Dub",
                         "English Dub with Closed Captions": "Dub-CC"}[alt]
                return alt, quality, arc["resources"][alt][quality], f"{short} only"

        for alt in VERSIONS:
            if alt in arc["resources"]:
                qs = arc["resources"][alt]
                best = sorted(qs.keys(), key=lambda q: int(q[:-1]), reverse=True)[0]
                short = {"English Subtitles": "Sub", "English Dub": "Dub",
                         "English Dub with Closed Captions": "Dub-CC"}[alt]
                return alt, best, qs[best], f"{short} {best}"

        return version, quality, None, ""

    # ---- handlers ----

    def _pick_folder(self) -> None:
        chosen = filedialog.askdirectory(initialdir=self.save_dir.get() or str(DEFAULT_DOWNLOADS))
        if chosen:
            self.save_dir.set(chosen)
            self._persist_settings()

    def _open_folder(self) -> None:
        path = Path(self.save_dir.get())
        path.mkdir(parents=True, exist_ok=True)
        try:
            os.startfile(path)  # Windows-only, fine here
        except AttributeError:
            self._log(f"Folder: {path}")

    def _persist_settings(self) -> None:
        self.config_data.update({
            "save_folder": self.save_dir.get(),
            "default_version": self.version.get() if self.version.get() in VERSIONS else "English Subtitles",
            "default_quality": self.quality.get() if self.quality.get() in QUALITIES else "1080p",
            "source": self._current_source(),
        })
        save_config(self.config_data)

    def _refresh_arcs(self) -> None:
        if self.worker and self.worker.is_alive():
            messagebox.showinfo("Busy", "A download is in progress. Cancel it first.")
            return
        self._set_status("Fetching onepace.net…")

        def task():
            try:
                arcs = refresh_arcs_from_web()
                self.ui_queue.put(("arcs", arcs))
                self.ui_queue.put(("log", f"Refreshed {len(arcs)} arcs."))
                self.ui_queue.put(("status", "Refreshed."))
            except Exception as e:
                self.ui_queue.put(("error", f"Refresh failed: {e}"))

        threading.Thread(target=task, daemon=True).start()

    def _start_download_one(self, arc: dict, album_id: str, version: str, quality: str) -> None:
        self._persist_settings()
        if self.worker and self.worker.is_alive():
            messagebox.showinfo("Busy", "A download is already in progress.")
            return
        dest = Path(self.save_dir.get())
        dest.mkdir(parents=True, exist_ok=True)
        self.cancel_evt.clear()
        self.cancel_btn.configure(state="normal")
        self._log(f"Starting: {arc['title']} — {version} {quality}")

        def task():
            try:
                Downloader(
                    album_id, dest,
                    on_status=lambda s: self.ui_queue.put(("status", s)),
                    on_progress=self._on_progress,
                    on_log=lambda m: self.ui_queue.put(("log", m)),
                    cancel_evt=self.cancel_evt,
                ).run()
                self.ui_queue.put(("status", f"Done: {arc['title']}"))
                self.ui_queue.put(("done", None))
            except DownloadCancelled:
                self.ui_queue.put(("log", "Cancelled."))
                self.ui_queue.put(("status", "Cancelled."))
                self.ui_queue.put(("done", None))
            except Exception as e:
                self.ui_queue.put(("error", str(e)))
                self.ui_queue.put(("done", None))

        self.worker = threading.Thread(target=task, daemon=True)
        self.worker.start()

    def _download_all(self) -> None:
        self._persist_settings()
        if self.worker and self.worker.is_alive():
            messagebox.showinfo("Busy", "A download is already in progress.")
            return

        src = self._current_source()
        plan: list[tuple[dict, str, str, str]] = []

        if src == SRC_MUHN_PACE:
            if not self.muhn_arcs:
                return
            total_gb = sum(a.get("total_bytes", 0) for a in self.muhn_arcs) / 1024**3
            if not messagebox.askyesno(
                "Download all Muhn Pace arcs",
                f"Queue every Muhn Pace arc — {len(self.muhn_arcs)} albums, "
                f"about {total_gb:.0f} GB total. Continue?"
            ):
                return
            for arc in self.muhn_arcs:
                plan.append(({"title": arc["title"]}, arc["album_id"], "English Dub", "muhn"))
        else:
            if not self.arcs:
                return
            if not messagebox.askyesno(
                "Download all arcs",
                "This will queue every arc using your selected Version + Quality "
                "(falling back per-arc if missing). It can be 100+ GB. Continue?"
            ):
                return
            version = self.version.get()
            quality = self.quality.get()
            for arc in self.arcs:
                v, q, aid, note = self._resolve_album(arc, version, quality)
                if aid:
                    plan.append((arc, aid, v, q))

        dest = Path(self.save_dir.get())
        dest.mkdir(parents=True, exist_ok=True)
        self.cancel_evt.clear()
        self.cancel_btn.configure(state="normal")

        self._log(f"Queue: {len(plan)} arcs.")

        def task():
            for i, (arc, album_id, v, q) in enumerate(plan, 1):
                if self.cancel_evt.is_set():
                    break
                self.ui_queue.put(("log", f"[{i}/{len(plan)}] {arc['title']} — {v} {q}"))
                try:
                    Downloader(
                        album_id, dest,
                        on_status=lambda s, a=arc: self.ui_queue.put(
                            ("status", f"{a['title']}: {s}")),
                        on_progress=self._on_progress,
                        on_log=lambda m: self.ui_queue.put(("log", m)),
                        cancel_evt=self.cancel_evt,
                    ).run()
                except DownloadCancelled:
                    self.ui_queue.put(("log", "Cancelled."))
                    break
                except Exception as e:
                    self.ui_queue.put(("log", f"  ERROR on {arc['title']}: {e}"))
            self.ui_queue.put(("status", "All done." if not self.cancel_evt.is_set() else "Cancelled."))
            self.ui_queue.put(("done", None))

        self.worker = threading.Thread(target=task, daemon=True)
        self.worker.start()

    def _cancel(self) -> None:
        self.cancel_evt.set()
        self._log("Cancel requested…")

    def _on_progress(self, frac: float, speed: str, idx: int, total: int, bytes_now: int) -> None:
        self.ui_queue.put(("progress", (frac, speed, idx, total, bytes_now)))

    def _drain_ui_queue(self) -> None:
        try:
            while True:
                kind, payload = self.ui_queue.get_nowait()
                if kind == "status":
                    self._set_status(payload)
                elif kind == "progress":
                    frac, speed, idx, total, bytes_now = payload
                    self.progress_var.set(min(max(frac, 0.0), 1.0))
                    self._set_status(
                        f"[{idx}/{total}]  {fmt_bytes(bytes_now)}  •  {speed}"
                    )
                elif kind == "log":
                    self._log(payload)
                elif kind == "arcs":
                    self.arcs = payload
                    self._refresh_arc_rows()
                elif kind == "error":
                    self._log("ERROR: " + payload)
                    messagebox.showerror("Error", payload)
                elif kind == "done":
                    self.cancel_btn.configure(state="disabled")
        except queue.Empty:
            pass
        self.after(80, self._drain_ui_queue)

    def _set_status(self, text: str) -> None:
        self.status_var.set(text)

    def _log(self, msg: str) -> None:
        self.log_box.configure(state="normal")
        self.log_box.insert("end", msg + "\n")
        self.log_box.see("end")
        self.log_box.configure(state="disabled")


def main() -> None:
    App().mainloop()


if __name__ == "__main__":
    main()
