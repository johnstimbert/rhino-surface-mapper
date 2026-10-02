"""Behavioural tests for map-directory discovery.

These tests specify deterministic headless path resolution for source and frozen
builds so the Qt application stores maps beside the right runtime location.
"""

import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
import app_paths as maps

class MapsDirectoryTests(unittest.TestCase):
    """Covers source-tree and PyInstaller-style MAPAS directory resolution."""

    def test_executable_directory_not_extraction_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            base = Path(folder)
            exe = base/'Portable'/'RhinoSurfaceMapper.exe'
            # sys.frozen simulates PyInstaller; __file__ points at its extraction dir.
            with patch.object(maps.sys,'frozen',True,create=True), patch.object(maps.sys,'executable',str(exe)), patch.object(maps,'__file__',str(base/'_MEI123'/'qt_map_operations.py')):
                result = maps.maps_directory()
                self.assertEqual(result,exe.resolve().parent/'MAPAS')
                self.assertTrue(result.is_dir())
                self.assertEqual(maps.maps_directory(),result)

    def test_source_directory(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder)/'source'/'qt_map_operations.py'
            with patch.object(maps.sys,'frozen',False,create=True), patch.object(maps,'__file__',str(source)):
                self.assertEqual(maps.maps_directory(),source.resolve().parent/'MAPAS')
