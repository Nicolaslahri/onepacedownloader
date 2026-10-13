"""Pinned Plex/Jellyfin season numbers for each arc."""

from __future__ import annotations

import re

# Plex/Jellyfin season numbers, pinned per arc. Deriving them from the arc's
# position in the index would renumber every later season (and orphan the
# user's existing library folders) the day One Pace adds an arc in the middle.
# Arcs not listed here are numbered after the highest pinned season, in index
# order.
PINNED_SEASONS = {
    "romancedawn": 1, "orangetown": 2, "syrupvillage": 3, "gaimon": 4,
    "baratie": 5, "arlongpark": 6, "theadventuresofbuggyscrew": 7,
    "loguetown": 8, "reversemountain": 9, "whiskypeak": 10,
    "thetrialsofkobymeppo": 11, "littlegarden": 12, "drumisland": 13,
    "alabasta": 14, "jaya": 15, "skypiea": 16, "longringlongland": 17,
    "waterseven": 18, "enieslobby": 19, "postenieslobby": 20,
    "thrillerbark": 21, "sabaodyarchipelago": 22, "amazonlily": 23,
    "impeldown": 24,
    "ifyoucouldgoanywheretheadventuresofthestrawhats": 25,
    "marineford": 26, "postwar": 27, "returntosabaody": 28,
    "fishmanisland": 29, "punkhazard": 30, "dressrosa": 31, "zou": 32,
    "wholecakeisland": 33, "reverie": 34, "wano": 35, "egghead": 36,
}


def _arc_slug(title: str) -> str:
    return re.sub(r"[^a-z0-9]", "", (title or "").lower())


def season_for_arc(index: dict, title: str) -> int | None:
    """Plex season number for an arc title, or None if it isn't in the index."""
    arcs = index.get("arcs", [])
    if not any(a.get("title") == title for a in arcs):
        return None
    pinned = PINNED_SEASONS.get(_arc_slug(title))
    if pinned:
        return pinned
    nxt = max(PINNED_SEASONS.values())
    for a in arcs:
        if _arc_slug(a.get("title", "")) in PINNED_SEASONS:
            continue
        nxt += 1
        if a.get("title") == title:
            return nxt
    return None
