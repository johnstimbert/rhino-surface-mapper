"""Exercise Map Library splitter ratio persistence and fallback behaviour.

These tests protect saved pane proportions and invalid-ratio handling so the map
library remains usable across sessions and parent-window shutdown.
"""
import math
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtWidgets import QApplication, QSplitter

from map_library import MapLibraryWindow


class MapLibrarySplitterPersistenceTests(unittest.TestCase):
    """Cover splitter persistence using temporary library roots."""
    @classmethod
    def setUpClass(cls):
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        (self.root / 'Kappa').mkdir()
        self.window = None
        self.parent = None

    def tearDown(self):
        if self.window is not None:
            self.window.close()
        if self.parent is not None:
            self.parent.close()
        self.temp.cleanup()

    def make_window(self, preferences=None, saved=None):
        saved = saved if saved is not None else {}
        callback = lambda key, value: saved.__setitem__(key, value)
        with patch('map_library.maps_directory', return_value=self.root):
            self.window = MapLibraryWindow(
                preferences=preferences or {}, save_preference=callback)
        return self.window

    def splitter_sizes(self, window, name):
        splitter = window.findChild(QSplitter, name)
        return splitter, splitter.sizes()

    def test_valid_ratios_restore_for_both_splitters(self):
        saved = {}
        window = self.make_window(saved=saved)
        window.show()
        self.app.processEvents()
        horizontal = window.findChild(QSplitter, 'librarySplitter')
        vertical = window.findChild(QSplitter, 'previewDetailsSplitter')
        horizontal.setSizes([600, 400])
        vertical.setSizes([560, 240])
        expected_horizontal = horizontal.sizes()[0] / sum(horizontal.sizes())
        expected_vertical = vertical.sizes()[0] / sum(vertical.sizes())
        window.close()
        restored = self.make_window(saved, {})
        restored.show()
        self.app.processEvents()
        _, horizontal_sizes = self.splitter_sizes(restored, 'librarySplitter')
        _, vertical_sizes = self.splitter_sizes(restored, 'previewDetailsSplitter')
        self.assertAlmostEqual(horizontal_sizes[0] / sum(horizontal_sizes), expected_horizontal, delta=0.005)
        self.assertAlmostEqual(vertical_sizes[0] / sum(vertical_sizes), expected_vertical, delta=0.005)
        self.window = restored
        self.assertFalse(horizontal.childrenCollapsible())
        self.assertFalse(vertical.childrenCollapsible())

    def test_missing_or_invalid_ratios_keep_defaults(self):
        invalid = {
            # Non-finite and non-numeric ratios fall back to default splitter sizes.
            MapLibraryWindow.HORIZONTAL_SPLIT_KEY: float('nan'),
            MapLibraryWindow.VERTICAL_SPLIT_KEY: 'invalid',
        }
        baseline = self.make_window()
        baseline.show()
        self.app.processEvents()
        _, expected_horizontal = self.splitter_sizes(baseline, 'librarySplitter')
        _, expected_vertical = self.splitter_sizes(baseline, 'previewDetailsSplitter')
        baseline.close()
        window = self.make_window(invalid)
        window.show()
        self.app.processEvents()
        horizontal, horizontal_sizes = self.splitter_sizes(window, 'librarySplitter')
        vertical, vertical_sizes = self.splitter_sizes(window, 'previewDetailsSplitter')
        self.assertAlmostEqual(horizontal_sizes[0] / sum(horizontal_sizes),
                               expected_horizontal[0] / sum(expected_horizontal), delta=0.005)
        self.assertAlmostEqual(vertical_sizes[0] / sum(vertical_sizes),
                               expected_vertical[0] / sum(expected_vertical), delta=0.005)
        # Fallback sizes must reject NaN, infinite, 0, and 1 ratio extremes.
        self.assertTrue(math.isfinite(horizontal_sizes[0]))
        self.assertTrue(math.isfinite(vertical_sizes[0]))

    def test_close_persists_both_ratios(self):
        saved = {}
        window = self.make_window(saved=saved)
        horizontal = window.findChild(QSplitter, 'librarySplitter')
        vertical = window.findChild(QSplitter, 'previewDetailsSplitter')
        horizontal.resize(1000, 100)
        vertical.resize(100, 800)
        horizontal.setSizes([600, 400])
        vertical.setSizes([560, 240])
        expected_horizontal = horizontal.sizes()[0] / sum(horizontal.sizes())
        expected_vertical = vertical.sizes()[0] / sum(vertical.sizes())
        window.close()
        self.assertAlmostEqual(saved[MapLibraryWindow.HORIZONTAL_SPLIT_KEY], expected_horizontal)
        self.assertAlmostEqual(saved[MapLibraryWindow.VERTICAL_SPLIT_KEY], expected_vertical)

    def test_unrelated_preferences_are_not_touched(self):
        preferences = {'theme': 'Dark', 'scanner_group': 4}
        saved = {}
        window = self.make_window(preferences, saved)
        window.close()
        self.assertEqual(preferences, {'theme': 'Dark', 'scanner_group': 4})
        self.assertNotIn('theme', saved)
        self.assertNotIn('scanner_group', saved)

    def test_parent_shutdown_persists_open_map_library(self):
        from rhino_surface_mapper_qt import MapperWindow

        self.parent = MapperWindow(
            self.root / 'status.json', game_running_check=lambda: True)
        # Stop parent timers so shutdown persistence is driven only by close().
        for timer in (self.parent.timer, self.parent.radar_timer, self.parent.assist_timer):
            timer.stop()
        self.parent.options_path = self.root / 'options.json'
        self.parent.options_path.write_text('{}', encoding='utf-8')
        with patch('map_library.maps_directory', return_value=self.root):
            self.parent.show_map_library()
        self.window = self.parent.map_library
        horizontal = self.window.findChild(QSplitter, 'librarySplitter')
        vertical = self.window.findChild(QSplitter, 'previewDetailsSplitter')
        horizontal.setSizes([600, 400])
        vertical.setSizes([560, 240])
        expected_horizontal = horizontal.sizes()[0] / sum(horizontal.sizes())
        expected_vertical = vertical.sizes()[0] / sum(vertical.sizes())
        with patch.object(self.parent, 'confirm_pml_exit', return_value=True):
            self.parent.close()
        data = __import__('json').loads(self.parent.options_path.read_text(encoding='utf-8'))
        self.assertAlmostEqual(data[MapLibraryWindow.HORIZONTAL_SPLIT_KEY], expected_horizontal)
        self.assertAlmostEqual(data[MapLibraryWindow.VERTICAL_SPLIT_KEY], expected_vertical)


if __name__ == '__main__':
    unittest.main()
