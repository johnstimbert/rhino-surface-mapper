"""Qt-free filesystem and JSON persistence for Rhino Surface Mapper maps.

This module is the persistence boundary for map documents. It knows about
UTF-8 JSON encoding and filesystem metadata, but not UI widgets or domain
navigation rules. Writes use same-directory temporary files followed by
``os.replace`` so callers never leave a half-written map at the target path.
"""

from __future__ import annotations

import json
import os
import tempfile
from datetime import datetime
from pathlib import Path
from typing import Any


def read_map_json(path: Path | str) -> dict[str, Any]:
    """Read and parse a UTF-8 JSON map file.

    Args:
        path: Map file path.

    Returns:
        Parsed JSON object.

    Raises:
        OSError: if the file cannot be read.
        json.JSONDecodeError: if the content is not valid JSON.
    """
    target = Path(path)
    return json.loads(target.read_text(encoding="utf-8"))


def write_map_json(path: Path | str, data: dict[str, Any]) -> None:
    """Atomically write a dictionary as a UTF-8 encoded map JSON file.

    The temporary file is created in the destination directory so replacement
    stays on the same filesystem. ``os.replace`` then swaps it into place
    atomically, avoiding corrupt target files if the process is interrupted.
    """
    target = Path(path)
    content = json.dumps(data, ensure_ascii=False, indent=2)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            dir=target.parent,
            suffix=".tmp",
            delete=False,
        ) as stream:
            temporary = Path(stream.name)
            stream.write(content)
        os.replace(temporary, target)
    finally:
        if temporary is not None and temporary.exists():
            try:
                temporary.unlink()
            except OSError:
                pass


def is_map_file_protected(path: Path | str) -> bool:
    """Return whether an existing map file on disk has the protected flag set."""
    target = Path(path)
    if not target.exists():
        return False
    data = read_map_json(target)
    return bool(data.get("protected", False))


def update_map_file_flags(path: Path | str, *, favorite: bool, protected: bool) -> None:
    """Atomically update favorite/protected flags while preserving file mtime.

    The Map Library sorts and displays entries by modification time. Flag-only
    edits are management changes, not exploration updates, so the original mtime
    is restored after the atomic replacement.

    Raises:
        ValueError: if either flag is not a real ``bool``.
        OSError: if the map cannot be read, replaced, or timestamped.
        json.JSONDecodeError: if the existing file is not valid JSON.
    """
    if type(favorite) is not bool or type(protected) is not bool:
        raise ValueError("Favorito e proteção devem ser valores booleanos.")

    target = Path(path)
    stats = target.stat()
    data = read_map_json(target)
    data.update(favorite=favorite, protected=protected)

    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            dir=target.parent,
            suffix=".tmp",
            delete=False,
        ) as stream:
            temporary = Path(stream.name)
            json.dump(data, stream, ensure_ascii=False, indent=2)
        os.replace(temporary, target)
        os.utime(target, ns=(stats.st_atime_ns, stats.st_mtime_ns))
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def read_file_timestamps(path: Path | str) -> tuple[str, str]:
    """Return file creation and modification times as ISO 8601 strings."""
    target = Path(path)
    stats = target.stat()
    created_at = datetime.fromtimestamp(stats.st_ctime).astimezone().isoformat(timespec="seconds")
    last_saved_at = datetime.fromtimestamp(stats.st_mtime).astimezone().isoformat(timespec="seconds")
    return created_at, last_saved_at
