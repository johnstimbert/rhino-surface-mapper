"""Minimal Qt Linguist contract for Rhino Surface Mapper.

This module is the narrow localization boundary used by Python UI-facing code:
it normalizes supported language identifiers, locates compiled catalogues, and
installs a ``QTranslator`` when available. Domain and persistence modules should
remain Qt-free and call no functions here except for final user-facing text.
"""

from __future__ import annotations

import sys
from pathlib import Path
from types import MappingProxyType
from typing import Mapping

from PySide6.QtCore import QCoreApplication, QTranslator


DEFAULT_LANGUAGE = "en_GB"
SUPPORTED_LANGUAGES: Mapping[str, str] = MappingProxyType({
    "en_GB": "English (United Kingdom)",
    "pt_PT": "Português (Portugal)",
})


def normalize_language(value: object) -> str:
    """Return a supported language identifier, defaulting to British English."""
    if isinstance(value, str) and value in SUPPORTED_LANGUAGES:
        return value
    return DEFAULT_LANGUAGE


def catalogue_path(language: object) -> Path | None:
    """Return the runtime catalogue path for a supported non-source language.

    The source language does not need a compiled catalogue, so ``None`` is the
    expected path for English. Frozen builds load packaged resources from
    ``sys._MEIPASS``; source runs load from the local ``translations`` folder.
    """
    normalized = normalize_language(language)
    if normalized == DEFAULT_LANGUAGE:
        return None

    if getattr(sys, "frozen", False):
        application_root = Path(sys._MEIPASS)
    else:
        application_root = Path(__file__).resolve().parent
    return application_root / "translations" / f"rsm_{normalized}.qm"


def install_translator(
    app: QCoreApplication,
    language: object,
) -> QTranslator | None:
    """Install a supported catalogue when it can be loaded.

    Missing or invalid catalogues fall back to the source language without
    preventing application startup. The returned translator must be retained
    by the caller while the application uses the translation.
    """
    path = catalogue_path(language)
    if path is None or not path.is_file():
        return None

    translator = QTranslator(app)
    if not translator.load(str(path)):
        return None
    if not app.installTranslator(translator):
        return None
    return translator


def translate(context: str, source_text: str) -> str:
    """Translate a Python UI string through Qt's active translation system."""
    return QCoreApplication.translate(context, source_text)
