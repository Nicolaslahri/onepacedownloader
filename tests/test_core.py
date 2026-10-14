"""Checks for the pure helpers both apps rely on. Run with: pytest tests"""

import json
import sys
import zlib
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "docker"))
sys.path.insert(0, str(ROOT / "_source"))

from app.core import downloader as dk_dl  # noqa: E402
from app.core import episode_index as dk_idx  # noqa: E402
from app.core import seasons  # noqa: E402

INDEX = json.loads((ROOT / "_source" / "episode_index.json").read_text("utf-8"))


# ── episode_key ───────────────────────────────────────────────────────

def test_episode_key_ignores_crc_and_range_tags():
    old = "[One Pace][115-117] Little Garden 01 [1080p][En Sub][ABCD1234].mkv"
    new = "[One Pace][115-117] Little Garden 01 [1080p][En Sub][0F0F0F0F].mkv"
    assert dk_dl.episode_key(old) == dk_dl.episode_key(new)


def test_episode_key_keeps_episode_number_and_quality():
    a = "[One Pace] Little Garden 01 [1080p][En Sub][ABCD1234].mkv"
    assert dk_dl.episode_key(a) != dk_dl.episode_key(a.replace(" 01 ", " 02 "))
    assert dk_dl.episode_key(a) != dk_dl.episode_key(a.replace("1080p", "720p"))


# ── source selection ─────────────────────────────────────────────────

def _src(version, quality, fid):
    return {"kind": "onepace", "version": version, "quality": quality,
            "file_id": fid}


EP = {"sources": [
    _src("English Subtitles", "1080p", "s1080"),
    _src("English Subtitles", "720p", "s720"),
    _src("English Dub with Closed Captions", "720p", "cc720"),
]}


def test_preferred_version_and_quality():
    s = dk_idx.best_source_for(EP, "onepace", "English Subtitles", "720p")
    assert s["file_id"] == "s720"


def test_dub_falls_back_to_dub_cc_before_subtitles():
    s = dk_idx.best_source_for(EP, "onepace", "English Dub", "1080p")
    assert s["file_id"] == "cc720"


def test_dub_never_becomes_subtitles_without_permission():
    ep = {"sources": [_src("English Subtitles", "1080p", "s1080")]}
    assert dk_idx.best_source_for(ep, "onepace", "English Dub", "1080p",
                                  allow_subs=False) is None
    s = dk_idx.best_source_for(ep, "onepace", "English Dub", "1080p",
                               allow_subs=True)
    assert dk_idx.is_sub_fallback(s, "English Dub")


def test_dedupe_sources_keeps_first_copy_of_a_file():
    srcs = [_src("English Subtitles", "1080p", "x"),
            _src("English Subtitles", "720p", "x"),
            _src("English Subtitles", "480p", "y")]
    assert [s["quality"] for s in dk_idx.dedupe_sources(srcs)] == ["1080p", "480p"]


# ── seasons ──────────────────────────────────────────────────────────

def test_every_current_arc_has_its_pinned_season():
    for i, arc in enumerate(INDEX["arcs"], 1):
        assert seasons.season_for_arc(INDEX, arc["title"]) == i, arc["title"]


def test_new_arc_in_the_middle_does_not_renumber_others():
    idx = {"arcs": list(INDEX["arcs"])}
    idx["arcs"].insert(3, {"title": "Brand New Arc"})
    assert seasons.season_for_arc(idx, "Brand New Arc") == 37
    assert seasons.season_for_arc(idx, "Egghead") == 36
    assert seasons.season_for_arc(idx, "Not An Arc") is None


# ── downloader helpers ───────────────────────────────────────────────

def _downloader(filter_ids, names):
    d = dk_dl.Downloader.__new__(dk_dl.Downloader)
    d.file_filter = set(filter_ids)
    d.expected_names = names
    d.missing = []
    d.on_log = lambda _m: None
    return d


def test_select_files_rematches_a_re_released_file_by_name():
    d = _downloader({"old"}, {"old": "[One Pace] Jaya 01 [1080p][AAAA1111].mkv"})
    files = [{"id": "new", "name": "[One Pace] Jaya 01 [1080p][BBBB2222].mkv"},
             {"id": "other", "name": "[One Pace] Jaya 02 [1080p][CCCC3333].mkv"}]
    assert [f["id"] for f in d._select_files(files, "Jaya")] == ["new"]
    assert d.missing == []


def test_select_files_reports_files_that_are_gone():
    d = _downloader({"gone"}, {"gone": "[One Pace] Jaya 05 [1080p][AAAA1111].mkv"})
    assert d._select_files([], "Jaya") == []
    assert d.missing == ["[One Pace] Jaya 05 [1080p][AAAA1111].mkv"]


def test_verify_crc(tmp_path):
    data = b"one pace" * 1000
    crc = f"{zlib.crc32(data):08X}"
    f = tmp_path / "ep.mkv"
    f.write_bytes(data)
    dk_dl.Downloader._verify_crc(f, f"[One Pace] Jaya 01 [{crc}].mkv")
    with pytest.raises(Exception):
        dk_dl.Downloader._verify_crc(f, "[One Pace] Jaya 01 [00000000].mkv")


# ── desktop app helpers (skipped where tkinter isn't available) ──────

def test_desktop_helpers_match_docker():
    od = pytest.importorskip("onepace_downloader")
    assert od.version_tuple("v2.1.0") > od.version_tuple("2.0.5")
    assert od.version_tuple("2.0.10") > od.version_tuple("2.0.9")
    name = "[One Pace][1-3] Romance Dawn 01 [1080p][En Sub][ABCD1234].mkv"
    assert od.episode_key(name) == dk_dl.episode_key(name)
    for arc in INDEX["arcs"]:
        assert (od.season_for_arc(INDEX, arc["title"])
                == seasons.season_for_arc(INDEX, arc["title"]))


# ── qBittorrent login ─────────────────────────────────────────────────

def _fake_qbittorrent(login_status, login_body):
    import http.server
    import threading

    class Handler(http.server.BaseHTTPRequestHandler):
        def _send(self, status, body=b""):
            self.send_response(status)
            if status == 200 and self.path.endswith("/auth/login"):
                self.send_header("Set-Cookie", "SID=abc; path=/")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_POST(self):
            self.rfile.read(int(self.headers.get("Content-Length", 0)))
            self._send(login_status, login_body)

        def do_GET(self):
            self._send(200, b"v5.1.0")

        def log_message(self, *args):
            pass

    srv = http.server.HTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


@pytest.mark.parametrize("status,body", [(200, b"Ok."), (204, b"")])
def test_qbittorrent_login_accepts_old_and_new_success(status, body):
    from app.core.clients import QBittorrentClient
    srv = _fake_qbittorrent(status, body)
    try:
        c = QBittorrentClient(f"http://127.0.0.1:{srv.server_port}", "u", "p")
        assert c.test() == "v5.1.0"
    finally:
        srv.shutdown()


@pytest.mark.parametrize("status,body", [(200, b"Fails."), (401, b""), (403, b"")])
def test_qbittorrent_login_rejects_bad_credentials(status, body):
    from app.core.clients import ClientError, QBittorrentClient
    srv = _fake_qbittorrent(status, body)
    try:
        c = QBittorrentClient(f"http://127.0.0.1:{srv.server_port}", "u", "p")
        with pytest.raises(ClientError):
            c.test()
    finally:
        srv.shutdown()
