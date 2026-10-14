"""Checks for the Docker app's pure helpers against the real catalog.

Run with: pytest tests
"""

import json
import sys
import zlib
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "docker"))

from app.core import downloader as dk_dl  # noqa: E402
from app.core import episode_index as dk_idx  # noqa: E402
from app.core import seasons  # noqa: E402

INDEX = json.loads((ROOT / "catalog" / "episode_index.json").read_text("utf-8"))


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

