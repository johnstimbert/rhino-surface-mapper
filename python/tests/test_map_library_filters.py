"""Exercise Map Library filtering, grouping, and selection refresh behaviour.

These tests protect session-only filter state and tree restoration rules so saved
map files are displayed consistently while users refine the library view.
"""
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtWidgets import QApplication, QHBoxLayout

from map_library import MapLibraryWindow
from mapper_core import MapperState, SRV_FLAG


class MapLibraryFilterTests(unittest.TestCase):
    """Cover filter state using temporary map folders and library windows."""
    @classmethod
    def setUpClass(cls):
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        system = self.root / 'Kappa'
        system.mkdir()
        combinations = ((False, False), (True, False), (False, True), (True, True))
        for index, (favorite, protected) in enumerate(combinations):
            # Library grouping parses the PML version from the square brackets.
            path = system / f'Kappa {index} [{index}].json'
            state = MapperState()
            state.process_status(dict(Flags=SRV_FLAG, Latitude=38, Longitude=-9,
                                      Heading=0, StarSystem='Kappa', BodyName=f'Kappa {index}'))
            state.pml_id = str(index)
            state.favorite = favorite
            state.protected = protected
            state.save(path)
        self.library = None

    def tearDown(self):
        if self.library is not None:
            self.library.close()
        if hasattr(self, 'maps_directory_patch'):
            self.maps_directory_patch.stop()
        self.tmp.cleanup()

    def make_library(self):
        self.maps_directory_patch = patch('map_library.maps_directory', return_value=self.root)
        self.maps_directory_patch.start()
        self.library = MapLibraryWindow()
        self.library.select_system('Kappa')
        return self.library

    def visible_map_names(self, library):
        return sorted(library.tree.topLevelItem(i).child(j).text(0)
                      for i in range(library.tree.topLevelItemCount())
                      for j in range(library.tree.topLevelItem(i).childCount()))

    def test_filters_use_and_semantics(self):
        library = self.make_library()
        self.assertEqual(len(self.visible_map_names(library)), 4)

        library.favorite_filter_check.setChecked(True)
        self.assertEqual(self.visible_map_names(library), ['Kappa 1 [1].json', 'Kappa 3 [3].json'])

        library.favorite_filter_check.setChecked(False)
        library.protected_filter_check.setChecked(True)
        self.assertEqual(self.visible_map_names(library), ['Kappa 2 [2].json', 'Kappa 3 [3].json'])

        library.favorite_filter_check.setChecked(True)
        self.assertEqual(self.visible_map_names(library), ['Kappa 3 [3].json'])

    def test_filter_refresh_preserves_system_and_search_and_clears_removed_selection(self):
        library = self.make_library()
        library.search.setText('ka')
        item = library.tree.topLevelItem(0).child(0)
        library.select_map(item, 0)
        library.favorite_filter_check.setChecked(True)
        self.assertEqual(library.current_system, 'Kappa')
        self.assertEqual(library.search.text(), 'ka')
        self.assertIsNone(library.selected_path)
        self.assertFalse(library.open_button.isEnabled())
        self.assertFalse(library.favorite_check.isEnabled())

    def test_editing_flag_refreshes_tree_while_filter_is_active(self):
        library = self.make_library()
        library.favorite_filter_check.setChecked(True)
        item = library.tree.topLevelItem(0).child(0)
        library.select_map(item, 0)
        library.favorite_check.setChecked(False)
        self.assertNotIn('Kappa 1 [1].json', self.visible_map_names(library))

    def test_filter_refresh_restores_surviving_expanded_group(self):
        library = self.make_library()
        surviving = next(library.tree.topLevelItem(i)
                         for i in range(library.tree.topLevelItemCount())
                         if library.tree.topLevelItem(i).text(0) == '1')
        surviving.setExpanded(True)
        library.favorite_filter_check.setChecked(True)
        restored = next(library.tree.topLevelItem(i)
                        for i in range(library.tree.topLevelItemCount())
                        if library.tree.topLevelItem(i).text(0) == '1')
        self.assertTrue(restored.isExpanded())
        self.assertEqual(sum(library.tree.topLevelItem(i).isExpanded()
                             for i in range(library.tree.topLevelItemCount())), 1)

    def test_filter_refresh_does_not_expand_replacement_group(self):
        library = self.make_library()
        removed = next(library.tree.topLevelItem(i)
                       for i in range(library.tree.topLevelItemCount())
                       if library.tree.topLevelItem(i).text(0) == '0')
        removed.setExpanded(True)
        library.favorite_filter_check.setChecked(True)
        self.assertEqual(sum(library.tree.topLevelItem(i).isExpanded()
                             for i in range(library.tree.topLevelItemCount())), 0)

    def test_expanding_a_group_keeps_only_one_group_expanded(self):
        library = self.make_library()
        first = library.tree.topLevelItem(0)
        second = library.tree.topLevelItem(1)
        first.setExpanded(True)
        second.setExpanded(True)
        self.assertFalse(first.isExpanded())
        self.assertTrue(second.isExpanded())

    def test_ui_declares_filter_controls_and_layout(self):
        library = self.make_library()
        self.assertIsNotNone(library.findChild(type(library.favorite_filter_check), 'favoriteFilterCheck'))
        self.assertIsNotNone(library.findChild(type(library.protected_filter_check), 'protectedFilterCheck'))
        self.assertIsNotNone(library.findChild(QHBoxLayout, 'mapFilterLayout'))


if __name__ == '__main__':
    unittest.main()
