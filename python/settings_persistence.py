"""Qt-free persistence helpers for Rhino Surface Mapper settings documents.

The settings layer handles plain JSON documents for app-local options and
exported/imported settings. It is intentionally tolerant when loading internal
``options.json`` preferences so malformed or missing files do not prevent the
application from starting. It must remain independent of PySide6.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any


SUPPORTED_SETTINGS_VERSION = 1


def load_preferences(path: Path | str) -> dict[str, Any]:
    """Load flat internal preferences, falling back to an empty object.

    Missing files, invalid JSON, non-dictionary documents, and incompatible
    paths all resolve to ``{}``. This tolerant contract lets the application
    recreate app-local ``options.json`` with defaults.
    """
    try:
        document = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, ValueError, TypeError):
        return {}
    return document if isinstance(document, dict) else {}


def save_preferences(path: Path | str, preferences: dict[str, Any]) -> None:
    """Save flat internal preferences without changing their file shape."""
    Path(path).write_text(
        json.dumps(preferences, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def read_exported_settings(path: Path | str) -> dict[str, Any]:
    """Read and structurally validate a versioned exported settings file.

    Exported settings are stricter than internal preferences: callers should see
    an error when the selected file is not a supported settings export.

    Raises:
        OSError: if the file cannot be read.
        json.JSONDecodeError: if the content is not valid JSON.
        ValueError: if the version marker or settings payload is invalid.
    """
    document = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if (not isinstance(document, dict)
            or document.get("rhino_settings_version") != SUPPORTED_SETTINGS_VERSION):
        raise ValueError("Formato de configurações inválido.")
    settings = document.get("settings")
    if not isinstance(settings, dict) or not settings:
        raise ValueError("O ficheiro não contém configurações.")
    return settings


def write_exported_settings(path: Path | str, settings: dict[str, Any]) -> None:
    """Write a version-1 exported settings document."""
    document = {
        "rhino_settings_version": SUPPORTED_SETTINGS_VERSION,
        "settings": settings,
    }
    Path(path).write_text(
        json.dumps(document, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
