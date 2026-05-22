"""Plex / Jellyfin media-server organization logic (standalone functions)."""

from __future__ import annotations

import os
import re
import time
import xml.sax.saxutils as su
from pathlib import Path

from .downloader import sanitize_filename


# ── Helpers ───────────────────────────────────────────────────────────

def arc_index_for_title(index: dict, title: str) -> int | None:
    """0-based canonical arc index for a given arc title."""
    for i, a in enumerate(index.get("arcs", [])):
        if a.get("title") == title:
            return i
    return None


def build_nfo_xml(season: int, ep_num: int, title: str, plot: str) -> str:
    """Render a Plex/Jellyfin episodedetails.nfo for a single episode."""
    t = su.escape(title or f"Episode {ep_num:02d}")
    p = su.escape(plot or "")
    return (
        '<?xml version="1.0" encoding="UTF-8"?>\n'
        "<episodedetails>\n"
        f"  <title>{t}</title>\n"
        "  <showtitle>One Pace</showtitle>\n"
        f"  <season>{season}</season>\n"
        f"  <episode>{ep_num}</episode>\n"
        f"  <plot>{p}</plot>\n"
        "</episodedetails>\n"
    )


# ── Post-download organize ───────────────────────────────────────────

def organize_for_plex(
    downloaded_subfolder: Path,
    file_ids: set[str],
    arc: dict,
    index: dict,
    media_dir: Path,
    log=None,
) -> int:
    """Move downloaded files into Plex/Jellyfin Season layout.

    Returns the number of files successfully moved.
    """
    _log = log or (lambda m: None)
    arc_title = arc.get("title", "")
    arc_idx = arc_index_for_title(index, arc_title)
    if arc_idx is None:
        _log(f"  [warn] Plex organize: arc {arc_title!r} not found in index")
        return 0

    season = arc_idx + 1
    season_dir = media_dir / "One Pace" / f"Season {season}"
    try:
        season_dir.mkdir(parents=True, exist_ok=True)
    except OSError as e:
        _log(f"  [warn] Couldn't create {season_dir}: {e}")
        return 0

    # file_id -> (episode dict, source dict)
    ep_for_file: dict[str, tuple[dict, dict]] = {}
    for ep in arc.get("episodes", []):
        for src in ep.get("sources", []):
            if src.get("file_id") in file_ids:
                ep_for_file[src["file_id"]] = (ep, src)

    if not ep_for_file:
        _log(f"  [warn] Plex organize: no matching episodes for {arc_title}")
        return 0

    moved = 0
    for fid, (ep, src) in ep_for_file.items():
        raw_name = src.get("filename", "")
        if not raw_name:
            continue
        src_path = downloaded_subfolder / sanitize_filename(raw_name)
        if not src_path.exists():
            # CRC hash fallback
            hash_match = re.search(r"\[([0-9A-Fa-f]{6,8})\]", raw_name)
            found = False
            if hash_match:
                crc = hash_match.group(1).upper()
                for p in downloaded_subfolder.glob("*"):
                    if (p.is_file()
                            and not p.name.endswith(".part")
                            and crc in p.name.upper()):
                        src_path = p
                        found = True
                        break
            if not found:
                candidates = [
                    p for p in downloaded_subfolder.glob("*")
                    if p.is_file() and not p.name.endswith(".part")
                ]
                if len(candidates) == 1:
                    src_path = candidates[0]
                else:
                    _log(f"  [warn] Plex organize: file not found -- "
                         f"{sanitize_filename(raw_name)}")
                    continue

        ext = src_path.suffix
        ep_num = ep.get("num", 0)
        canonical = ep.get("canonical_title", "")
        title_part = sanitize_filename(canonical) if canonical else f"Episode {ep_num:02d}"
        new_name = f"One Pace - s{season:02d}e{ep_num:02d} - {title_part}{ext}"
        target = season_dir / new_name

        rename_ok = False
        for _attempt in range(6):
            try:
                if target.exists():
                    target.unlink()
                src_path.rename(target)
                rename_ok = True
                break
            except PermissionError:
                time.sleep(0.5)
            except Exception as e:
                _log(f"  [warn] Couldn't move {src_path.name} -> {target.name}: {e}")
                break
        if not rename_ok:
            continue

        moved += 1
        if canonical or ep.get("plot"):
            try:
                target.with_suffix(".nfo").write_text(
                    build_nfo_xml(season, ep_num, canonical, ep.get("plot", "")),
                    encoding="utf-8",
                )
            except Exception as e:
                _log(f"  [warn] Couldn't write .nfo for {target.name}: {e}")

    if moved:
        _log(f"  Organized {moved} file(s) into One Pace/Season {season}/")
        try:
            if downloaded_subfolder.exists() and not any(downloaded_subfolder.iterdir()):
                downloaded_subfolder.rmdir()
        except OSError:
            pass

    return moved


# ── Retroactive organize ─────────────────────────────────────────────

def retroactive_organize(media_dir: Path, index: dict, log=None) -> int:
    """Scan media_dir and organize previously downloaded files into Plex layout.
    Returns total files moved."""
    _log = log or (lambda m: None)
    if not media_dir.exists() or not media_dir.is_dir():
        return 0

    # Build lookup: sanitized_filename -> (arc_idx, ep_num, canonical, plot)
    file_map: dict[str, tuple[int, int, str, str]] = {}
    for arc_idx, arc in enumerate(index.get("arcs", [])):
        for ep in arc.get("episodes", []):
            for src in ep.get("sources", []):
                fn = src.get("filename")
                if not fn:
                    continue
                key = sanitize_filename(fn)
                file_map[key] = (
                    arc_idx,
                    ep.get("num", 0),
                    ep.get("canonical_title", ""),
                    ep.get("plot", ""),
                )

    plex_dir = media_dir / "One Pace"
    moved = 0
    prunable: set[Path] = set()

    for dirpath, _, filenames in os.walk(media_dir):
        dp = Path(dirpath)
        try:
            dp.relative_to(plex_dir)
            continue
        except ValueError:
            pass
        for fn in filenames:
            if fn.endswith(".part") or fn.endswith(".nfo"):
                continue
            info = file_map.get(fn)
            if info is None:
                continue
            arc_idx, ep_num, canonical, plot = info
            season = arc_idx + 1
            season_dir = media_dir / "One Pace" / f"Season {season}"
            try:
                season_dir.mkdir(parents=True, exist_ok=True)
            except OSError:
                continue
            src_path = dp / fn
            ext = src_path.suffix
            title_part = (
                sanitize_filename(canonical) if canonical else f"Episode {ep_num:02d}"
            )
            new_name = (
                f"One Pace - s{season:02d}e{ep_num:02d} - {title_part}{ext}"
            )
            target = season_dir / new_name
            rename_ok = False
            for _attempt in range(6):
                try:
                    if target.exists():
                        target.unlink()
                    src_path.rename(target)
                    rename_ok = True
                    break
                except PermissionError:
                    time.sleep(0.5)
                except Exception:
                    break
            if not rename_ok:
                continue
            moved += 1
            prunable.add(dp)
            if canonical or plot:
                try:
                    target.with_suffix(".nfo").write_text(
                        build_nfo_xml(season, ep_num, canonical, plot),
                        encoding="utf-8",
                    )
                except Exception:
                    pass

    for d in prunable:
        try:
            if d != media_dir and d.exists() and not any(d.iterdir()):
                d.rmdir()
        except OSError:
            pass

    if moved:
        _log(f"Organized {moved} existing file(s) into Plex/Jellyfin layout.")
    return moved
