"""Qt-free application-local filesystem paths.

Rhino Surface Mapper uses a portable app-local storage model. When packaged as
a frozen executable, data lives next to the executable; during source runs it
lives next to this module in the ``python`` folder. The path layer must not
depend on PySide6 or UI lifecycle.
"""

from __future__ import annotations

import sys
from pathlib import Path


def maps_directory() -> Path:
    """Return the application ``MAPAS`` directory, creating it when needed."""
    # ``sys.frozen`` distinguishes a packaged app from a source checkout while
    # preserving the same app-local directory convention in both modes.
    base = Path(sys.executable if getattr(sys, 'frozen', False) else __file__).resolve().parent
    directory = base / 'MAPAS'
    directory.mkdir(parents=True, exist_ok=True)
    return directory
