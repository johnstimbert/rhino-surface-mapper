"""Qt-free PML discovery, matching, and filename rules.

A PML is the named exploration area for a body: it combines system/body
identity, a PML identifier, and a geographic centre used to rediscover matching
maps. This module stays in the domain/persistence core and depends only on
plain paths, regexes, and numbers. It must not import PySide6.
"""

from __future__ import annotations

import math
import re
from pathlib import Path
from typing import Any, Callable, Iterable

PML_MATCH_DISTANCE_M = 13_000


def corresponds_to_map(state: Any, system: str, body: str, lat: float, lon: float) -> bool:
    """Return whether a position belongs to the supplied map/PML location.

    System and body identity must match case-insensitively and the stored PML
    centre must be available. The fixed 13 km correspondence radius is
    inclusive: a position exactly on the boundary still belongs to the map.
    Invalid or incomplete map coordinates are treated as non-corresponding.
    """
    if (not isinstance(getattr(state, 'system', None), str)
            or not isinstance(getattr(state, 'body', None), str)
            or state.system.casefold() != system.casefold()
            or state.body.casefold() != body.casefold()):
        return False
    center_lat = getattr(state, 'pml_center_lat', None)
    center_lon = getattr(state, 'pml_center_lon', None)
    if center_lat is None or center_lon is None:
        return False
    try:
        distance = surface_distance(state.radius, lat, lon, center_lat, center_lon)
    except (AttributeError, TypeError, ValueError, ZeroDivisionError):
        return False
    return distance <= PML_MATCH_DISTANCE_M

def safe_filename_component(value: object) -> str:
    """Return a readable filename component safe for Windows paths.

    Windows-invalid characters (``<>:"/\\|?*`` and control codes) are replaced
    with underscores, and trailing dots/spaces are removed because Windows
    normalizes them unpredictably.
    """
    cleaned = re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', str(value)).strip().rstrip('. ')
    return cleaned or 'Sem nome'

def pml_filename(state: Any) -> str:
    """Build the canonical ``Body [PML].json`` filename for a PML state."""
    return f'{safe_filename_component(state.body)} [{safe_filename_component(state.pml_id)}].json'

def pml_path(base_directory: Path, state: Any) -> Path | None:
    """Return the canonical PML path, or ``None`` until its identity is known."""
    if not (state.system.strip() and state.body.strip() and state.pml_id.strip()):
        return None
    return base_directory / safe_filename_component(state.system) / pml_filename(state)

def surface_distance(radius: float, lat_a: float, lon_a: float, lat_b: float, lon_b: float) -> float:
    """Return great-circle distance in metres with the haversine formula.

    The supplied planet radius keeps the calculation body-specific. The final
    ``min(1, ...)`` protects ``asin`` from tiny floating-point overshoots.
    """
    dlat, dlon = math.radians(lat_b - lat_a), math.radians(lon_b - lon_a)
    value = math.sin(dlat / 2) ** 2 + math.cos(math.radians(lat_a)) * math.cos(math.radians(lat_b)) * math.sin(dlon / 2) ** 2
    return 2 * radius * math.asin(min(1, math.sqrt(value)))

def infer_legacy_pml(state: Any, path: Path, system: str, body: str) -> None:
    """Recover PML metadata from legacy paths and ``Centro [id]`` markers.

    Older maps may encode the system in the parent directory, the body/PML in
    the filename, and the PML centre as a marker named ``Centro [id]``. This
    function restores those fields so old files participate in modern matching.
    """
    if not state.system and path.parent.name.casefold() == safe_filename_component(system).casefold():
        state.system = system
    legacy_name = re.fullmatch(r'(.+?)\s+\[[^\]]+\](?:\s+v\d+)?', path.stem, re.IGNORECASE)
    if not state.pml_id and state.pml_center_lat is None and legacy_name:
        state.body = legacy_name.group(1).strip()
    elif not state.body:
        state.body = body
    for mark in state.marks:
        # Legacy centre markers are the only persisted source of the old PML
        # identifier and geographic centre.
        match = re.fullmatch(r'Centro\s*\[([^\]]+)\]', str(mark.get('name', '')).strip(), re.IGNORECASE)
        if match and 'lat' in mark and 'lon' in mark:
            state.pml_id = state.pml_id or match.group(1).strip()
            state.pml_center_lat = state.pml_center_lat if state.pml_center_lat is not None else mark['lat']
            state.pml_center_lon = state.pml_center_lon if state.pml_center_lon is not None else mark['lon']
            break
    state.body_key = f'{state.system}|{state.body}'

def matching_candidates(paths: Iterable[Path], system: str, body: str, lat: float, lon: float, loader: Callable[[Path], Any]) -> list[tuple[float, Path, Any]]:
    """Load and return nearby compatible candidates, ignoring malformed files.

    Args:
        paths: Candidate map paths to inspect.
        system: Current star system.
        body: Current body name.
        lat: Current latitude.
        lon: Current longitude.
        loader: Function that loads a path into a state-like object.

    Returns:
        ``(distance, path, state)`` tuples sorted from nearest to farthest.
    """
    matches = []
    for path in paths:
        try:
            candidate = loader(path)
            infer_legacy_pml(candidate, path, system, body)
            if not corresponds_to_map(candidate, system, body, lat, lon):
                continue
            distance = surface_distance(candidate.radius, lat, lon,
                                        candidate.pml_center_lat, candidate.pml_center_lon)
            matches.append((distance, path, candidate))
        except (OSError, ValueError, TypeError, KeyError, AttributeError):
            continue
    return sorted(matches, key=lambda item: item[0])

def newest_by_pml(matches: Iterable[tuple[float, Path, Any]]) -> list[tuple[float, Path, Any]]:
    """Keep the newest file for each PML identifier."""
    selected = {}
    for item in matches:
        key = item[2].pml_id.casefold()
        if key not in selected or item[1].stat().st_mtime > selected[key][1].stat().st_mtime:
            selected[key] = item
    return list(selected.values())

def next_john_doe_id(paths: Iterable[Path], system: str, body: str, loader: Callable[[Path], Any]) -> str:
    """Return the next JD identifier for valid maps of the requested body."""
    highest = 0
    for path in paths:
        try:
            candidate = loader(path)
            infer_legacy_pml(candidate, path, system, body)
            if candidate.body.casefold() != body.casefold():
                continue
            match = re.fullmatch(r'JD(\d+)', candidate.pml_id.strip(), re.IGNORECASE)
            if match:
                highest = max(highest, int(match.group(1)))
        except (OSError, ValueError, TypeError, KeyError, AttributeError):
            continue
    return f'JD{highest + 1}'

def next_version_path(canonical: Path, paths: Iterable[Path]) -> Path:
    """Return the next version path without treating PML brackets as globs.

    Version detection uses a regex against supplied path names instead of
    ``Path.glob`` because square brackets are glob character-class syntax, while
    PML filenames deliberately contain literal ``[id]`` components.
    """
    expression = re.compile(rf'^{re.escape(canonical.stem)} v(\d+)\.json$', re.IGNORECASE)
    highest = max([1] + [int(match.group(1)) for path in paths if (match := expression.fullmatch(path.name))])
    return canonical.with_name(f'{canonical.stem} v{highest + 1}.json')
