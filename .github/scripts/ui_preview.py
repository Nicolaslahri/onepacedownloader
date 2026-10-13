"""Screenshots of the desktop app on a real Windows machine (CI only).

    python .github/scripts/ui_preview.py OUT_DIR

Each shot runs the app in its own process with a throwaway config and no
network calls, then grabs the window including its title bar.
"""

import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]

SHOTS = [
    # name, appearance, source, arc, episode, ticked, extra
    ("main-dark", "Dark", "onepace", "Alabasta", 3, "1,2,3", ""),
    ("main-light", "Light", "onepace", "Alabasta", 3, "1,2,3", ""),
    ("welcome-dark", "Dark", "onepace", "", 0, "", ""),
    ("muhn-light", "Light", "muhn", "Enies Lobby", 13, "11,12", ""),
    ("nyaa-dark", "Dark", "nyaa", "Alabasta", 1, "", ""),
    ("downloading-dark", "Dark", "onepace", "Alabasta", 3, "1,2,3", "dl"),
    ("dialog-light", "Light", "onepace", "Alabasta", 3, "1,2,3", "batch"),
    ("settings-dark", "Dark", "onepace", "Alabasta", 3, "", "settings"),
    ("update-light", "Light", "onepace", "", 0, "", "update"),
    ("narrow-dark", "Dark", "onepace", "Alabasta", 3, "1", "narrow"),
    # Display scaling: logical size x scale is roughly a 1080p screen.
    ("scale125-dark", "Dark", "onepace", "Romance Dawn", 2, "", "s125"),
    ("scale150-light", "Light", "onepace", "Romance Dawn", 2, "", "s150"),
    ("wide-dark", "Dark", "onepace", "Romance Dawn", 2, "", "wide"),
]

GEOMETRY = {"narrow": "1060x760+20+20", "s125": "1500x780+10+10",
            "s150": "1250x690+10+10", "wide": "1880x1000+10+10"}
SCALE = {"s125": 1.25, "s150": 1.5}


def run_one(out, mode, src, arc_t, epn, ticks, extra):
    sys.path.insert(0, str(ROOT / "_source"))
    import ctypes
    from ctypes import wintypes

    from PIL import ImageGrab

    import onepace_downloader as od

    od.load_config = lambda: {"appearance": mode, "source": src,
                              "save_folder": r"D:\Media\One Pace",
                              "geometry": GEOMETRY.get(extra, "1360x860+40+40")}
    od.save_config = lambda c: None
    od.App._check_for_updates_async = lambda self: None
    if extra in SCALE:
        od.ctk.set_widget_scaling(SCALE[extra])
        od.ctk.set_window_scaling(SCALE[extra])
    app = od.App()
    ticks = [int(t) for t in ticks.split(",") if t]

    def step():
        if arc_t:
            arc = next(a for a in app.index["arcs"] if a["title"] == arc_t)
            app._on_arc_selected(arc)
            app.update()
            if epn:
                ep = next((e for e in app.selected_arc["episodes"]
                           if e["num"] == int(epn)), None)
                if ep:
                    app._on_episode_clicked(ep)
            for t in ticks:
                if t in app.ep_check_vars:
                    app.ep_check_vars[t].set(True)
            app._refresh_selection_buttons()
        if extra == "dl":
            app.download_title = "Alabasta  —  3 episodes (Sub 1080p)"
            app.download_state = {"frac": 0.42, "speed": "8.4 MB/s",
                                  "bytes_now": 220_000_000, "idx": 2,
                                  "total": 3, "size_total": 520_000_000}
            app._set_cancel_visible(True)
            app._render_source_panel()
        elif extra == "batch":
            eps = [e for e in app.selected_arc["episodes"] if e["num"] in ticks]
            od.BatchDownloadDialog(app, source=src, episodes=eps,
                                   default_version="English Dub",
                                   default_quality="1080p", allow_subs=False,
                                   saved_nums={eps[0]["num"]})
        elif extra == "settings":
            app._open_settings_panel()
        elif extra == "update":
            app._show_app_update("2.2.0", "https://example.com")
            app._set_refresh_badge(True)
        app.lift()
        app.attributes("-topmost", True)
        app.after(2500, shoot)

    def shoot():
        app.update()
        hwnd = ctypes.windll.user32.GetParent(app.winfo_id())
        rect = wintypes.RECT()
        ctypes.windll.dwmapi.DwmGetWindowAttribute(
            hwnd, 9, ctypes.byref(rect), ctypes.sizeof(rect))
        ImageGrab.grab(bbox=(rect.left, rect.top, rect.right, rect.bottom),
                       all_screens=True).save(out)
        app.destroy()

    app.after(2500, step)
    app.mainloop()


def main():
    if sys.argv[1] == "--one":
        run_one(*sys.argv[2:9])
        return
    out_dir = Path(sys.argv[1])
    out_dir.mkdir(parents=True, exist_ok=True)
    for name, *rest in SHOTS:
        out = out_dir / f"{name}.png"
        args = [sys.executable, __file__, "--one", str(out)] + [str(x) for x in rest]
        r = subprocess.run(args, cwd=ROOT, timeout=120)
        print(name, "ok" if r.returncode == 0 and out.exists() else "FAILED")


if __name__ == "__main__":
    os.environ.setdefault("PYTHONUTF8", "1")
    main()
