"""Behavioural tests for options-file location selection.

These tests keep configuration path resolution deterministic for both source and
frozen Qt application starts without importing UI behaviour into the assertions.
"""

import importlib
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

import rhino_surface_mapper_qt


class OptionsPathTests(unittest.TestCase):
    """Covers module-level OPTIONS_PATH calculation across reload scenarios."""

    def test_source_options_path_stays_next_to_source_module(self):
        expected = Path(rhino_surface_mapper_qt.__file__).resolve().parent / 'options.json'
        self.assertEqual(rhino_surface_mapper_qt.OPTIONS_PATH, expected)

    def test_frozen_options_path_stays_next_to_executable(self):
        executable = Path('portable') / 'RhinoSurfaceMapper.exe'
        try:
            # sys.frozen/create=True simulates a PyInstaller build on plain Python.
            with patch.object(sys, 'frozen', True, create=True), \
                    patch.object(sys, 'executable', str(executable)):
                reloaded = importlib.reload(rhino_surface_mapper_qt)
                self.assertEqual(reloaded.OPTIONS_PATH, executable.resolve().parent / 'options.json')
        finally:
            importlib.reload(rhino_surface_mapper_qt)


if __name__ == '__main__':
    unittest.main()
