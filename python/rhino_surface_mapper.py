"""Entry point for the Rhino Surface Mapper PySide6 application.

Run from this folder with ``py -3.13 rhino_surface_mapper.py``. The entry point
stays deliberately thin so tests can import the Qt window module without
starting the event loop.

This module belongs to the presentation startup layer. Qt UI construction lives
in ``rhino_surface_mapper_qt``; mapping rules, PML identity, and persistence
remain in Qt-free core modules such as ``mapper_core``, ``map_pml``, and
``map_persistence``.
"""
from rhino_surface_mapper_qt import main

if __name__ == '__main__':
    raise SystemExit(main())
