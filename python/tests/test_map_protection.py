"""Behavioural tests for protected maps, mining-only mode, and library flags.

These Qt-facing tests keep protected-map workflows deterministic while verifying
that core state, disk contents, and preview widgets stay safe in headless runs.
"""
import copy
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtWidgets import QApplication
from layout_options import OP_MARK
from mapper_core import MapperState, SRV_FLAG
from radar import RadarPulse
from rhino_surface_mapper_qt import MapperWindow
from map_library import MapLibraryWindow
from i18n import translate


class ProtectionTests(unittest.TestCase):
    """Covers protected-map workflows using a saved PML map fixture from setUp."""

    @classmethod
    def setUpClass(cls):
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.path = self.root/'Kappa'/'Kappa 2 [6].json'
        self.path.parent.mkdir()
        self.status = dict(Flags=SRV_FLAG, Latitude=38, Longitude=-9, Heading=0,
                           StarSystem='Kappa', BodyName='Kappa 2')
        self.state = MapperState()
        self.state.process_status(self.status)
        self.state.pml_id = '6'
        self.state.start_search()
        self.state.deposits = [dict(name='Ouro', size='Grande', rigs=3,
                                   x=1000, y=0, lat=38, lon=-8.9886)]
        self.state.favorite = True
        self.state.protected = True
        self.state.save(self.path)
        self.original = self.path.read_bytes()
        self.window = None

    def tearDown(self):
        if self.window:
            with patch.object(self.window, 'confirm_pml_exit', return_value=True):
                self.window.close()
        self.tmp.cleanup()

    def make_window(self):
        self.window = MapperWindow(
            self.root/'absent-status.json', game_running_check=lambda: True)
        for timer in (self.window.timer, self.window.radar_timer, self.window.assist_timer):
            timer.stop()
        return self.window

    def test_flags_round_trip_and_disk_guard_even_with_stale_state(self):
        loaded = MapperState()
        loaded.load(self.path)
        self.assertTrue(loaded.favorite and loaded.protected)
        loaded.protected = False
        with self.assertRaises(PermissionError):
            loaded.save(self.path)
        self.assertEqual(self.original, self.path.read_bytes())

    def test_metadata_only_update_preserves_content_and_modification_date(self):
        before = json.loads(self.original)
        # Library flag toggles should restore mtime so cards do not look re-saved.
        stamp = self.path.stat().st_mtime_ns
        MapperState.set_file_flags(self.path, favorite=False, protected=False)
        after = json.loads(self.path.read_text())
        before.update(favorite=False, protected=False)
        self.assertEqual(before, after)
        self.assertEqual(stamp, self.path.stat().st_mtime_ns)
        state = MapperState()
        state.load(self.path)
        state.save(self.path)

    def test_mining_moves_rhino_without_recording_and_refuses_all_saves(self):
        state = MapperState()
        state.load(self.path)
        state.enter_mining_mode()
        records = copy.deepcopy((state.points, state.radar_coverage, state.route_history))
        self.assertTrue(state.process_status(dict(self.status, Longitude=-8.999)))
        self.assertEqual(state.rhino_lon, -8.999)
        self.assertFalse(state.start_search())
        self.assertFalse(state.skip_next())
        radar = RadarPulse()
        radar.tick(state, 0, True, False, 'test')
        radar.tick(state, 1, True, True, 'test')
        self.assertEqual(records, (state.points, state.radar_coverage, state.route_history))
        for path in (self.path, self.root/'copy.json'):
            with self.assertRaises(PermissionError):
                state.save(path)
        self.assertEqual(self.original, self.path.read_bytes())
        result = state.process_status(dict(self.status, BodyName='Outro'))
        self.assertTrue(result)
        self.assertTrue(result.location_changed)
        self.assertEqual((result.system, result.body), ('Kappa', 'Outro'))
        self.assertEqual(state.body, 'Kappa 2')
        self.assertEqual(records, (state.points, state.radar_coverage, state.route_history))
        self.assertTrue(state.protected and state.mining_only)

    def test_exploration_creates_unprotected_version_and_preserves_original(self):
        w = self.make_window()
        with patch.object(w, 'choose_protected_map_mode', return_value='explore'), patch('qt_map_operations.maps_directory', return_value=self.root):
            self.assertTrue(w.install_loaded_map(self.state, source_path=self.path))
        self.assertEqual(w.current_map_path.name, 'Kappa 2 [6] v2.json')
        self.assertFalse(w.state.read_only)
        self.assertTrue(w.state.favorite)
        self.assertEqual(self.original, self.path.read_bytes())
        self.assertEqual(w.state.deposits, self.state.deposits)

    def test_cancel_and_failed_copy_preserve_active_map(self):
        w = self.make_window()
        previous = w.state
        with patch.object(w, 'choose_protected_map_mode', return_value=None):
            self.assertFalse(w.install_loaded_map(self.state, source_path=self.path))
        self.assertIs(w.state, previous)
        with patch.object(w, 'choose_protected_map_mode', return_value='explore'), patch.object(w, 'write_new_version', side_effect=OSError('sem espaço')):
            with self.assertRaises(OSError):
                w.install_loaded_map(self.state, source_path=self.path)
        self.assertIs(w.state, previous)
        self.assertEqual(self.original, self.path.read_bytes())

    def test_mining_navigation_and_edit_guards(self):
        w = self.make_window()
        with patch.object(w, 'choose_protected_map_mode', return_value='mining'):
            self.assertTrue(w.install_loaded_map(self.state, source_path=self.path))
        w.state.process_status(self.status)
        w.start_navigation('deposits', w.state.deposits[0])
        self.assertIsNotNone(w.state.active_nav_target)
        self.assertFalse(w.state.search_paused)
        expected = copy.deepcopy(w.state.to_dict())
        with patch('qt_map_operations.QMessageBox.information'):
            w.mark()
            w.mark_deposit()
            w.mark_rig()
            w.edit_deposit(w.state.deposits[0])
            w.delete_marker('deposits', w.state.deposits[0])
            w.save_map()
        self.assertEqual(expected, w.state.to_dict())
        self.assertTrue(w.confirm_pml_exit())
        self.assertTrue(w.prepare_to_replace_current_map('Abrir', 'abrir'))
        w.refresh()
        self.assertFalse(w.search_button.isEnabled())
        self.assertFalse(w.op_buttons[OP_MARK].isEnabled())
        self.assertIn('Mining only', w.map_file_info.text())
        # Mining-only rendering must not even ask for exploration trail paths.
        with patch.object(w.view, 'trajectory_paths', side_effect=AssertionError('rasto em mineração')):
            w.view.grab()
        self.assertEqual(self.original, self.path.read_bytes())

    def test_library_flags_and_protected_preview_do_not_migrate_file(self):
        with patch('map_library.maps_directory', return_value=self.root):
            library = MapLibraryWindow()
            library.select_system('Kappa')
            item = library.tree.topLevelItem(0).child(0)
            self.assertEqual(item.text(0), self.path.name)
            library.select_map(item, 1)
            self.assertTrue(library.favorite_check.isChecked())
            self.assertTrue(library.protected_check.isChecked())
            self.assertEqual(self.original, self.path.read_bytes())
            library.favorite_check.setChecked(False)
            data = json.loads(self.path.read_text())
            self.assertFalse(data['favorite'])
            self.assertTrue(data['protected'])
            library.protected_check.setChecked(False)
            self.assertFalse(json.loads(self.path.read_text())['protected'])
            library.close()

    def test_map_library_ui_hosts_custom_widgets_without_transplanting_them(self):
        from PySide6.QtCore import Qt
        from PySide6.QtWidgets import QCheckBox, QLineEdit, QListWidget, QPushButton, QSplitter, QWidget
        library = MapLibraryWindow()
        try:
            self.assertEqual(library.objectName(), 'MapLibraryWindow')
            self.assertIsNotNone(library.findChild(QSplitter, 'librarySplitter'))
            horizontal_splitter = library.findChild(QSplitter, 'librarySplitter')
            vertical_splitter = library.findChild(QSplitter, 'previewDetailsSplitter')
            self.assertEqual(horizontal_splitter.orientation(), Qt.Orientation.Horizontal)
            self.assertIsNotNone(vertical_splitter)
            self.assertEqual(vertical_splitter.orientation(), Qt.Orientation.Vertical)
            for splitter in (horizontal_splitter, vertical_splitter):
                self.assertFalse(splitter.childrenCollapsible())
                self.assertTrue(all(not splitter.isCollapsible(index)
                                    for index in range(splitter.count())))
            self.assertIsInstance(library.findChild(QLineEdit, 'searchInput'), QLineEdit)
            self.assertIsInstance(library.findChild(QListWidget, 'suggestionsList'), QListWidget)
            self.assertIsInstance(library.findChild(QCheckBox, 'favoriteCheck'), QCheckBox)
            self.assertIsInstance(library.findChild(QPushButton, 'openButton'), QPushButton)
            preview_host = library.findChild(QWidget, 'previewHost')
            map_details = library.findChild(QWidget, 'mapDetails')
            tree_host = library.findChild(QWidget, 'treeHost')
            self.assertEqual(vertical_splitter.widget(0), preview_host)
            self.assertEqual(vertical_splitter.widget(1), map_details)
            self.assertIs(library.preview.parent(), preview_host)
            self.assertIs(library.tree.parent(), tree_host)
            self.assertIs(library.preview.window(), library)
            self.assertIs(library.tree.window(), library)
        finally:
            library.close()

    def test_map_library_theme_is_local_and_preserves_preview_semantics(self):
        from map_library import MapPreview

        library = MapLibraryWindow()
        try:
            self.assertTrue(library.dark_theme)
            self.assertIn('#252d34', library.info.styleSheet())
            self.assertIn('#2c3239', library.tree.styleSheet())
            library.set_theme(False)
            self.assertFalse(library.dark_theme)
            self.assertIn('#ffffff', library.info.styleSheet())
            self.assertIn('#ffffff', library.tree.styleSheet())
            self.assertNotIn('#252d34', library.info.styleSheet())
            self.assertNotIn('#2c3239', library.tree.styleSheet())
            library.set_theme(True)
            self.assertIn('#252d34', library.info.styleSheet())
        finally:
            library.close()

        preview = MapPreview()
        preview.resize(460, 300)
        state = MapperState()
        state.points = [dict(x=-500, y=0), dict(x=500, y=0)]
        preview.show_map(state)
        preview.show()
        self.app.processEvents()
        try:
            dark_route = preview.grab().toImage().pixelColor(230, 150)
            preview.set_theme(False)
            self.app.processEvents()
            light_route = preview.grab().toImage().pixelColor(230, 150)
            for color in (dark_route, light_route):
                self.assertGreater(color.green(), color.red())
                self.assertGreater(color.green(), color.blue())
        finally:
            preview.close()

    def test_map_library_follows_effective_theme_when_open(self):
        window = self.make_window()
        self.assertFalse(hasattr(window, 'map_library'))
        window.apply_theme('Dark')
        self.assertFalse(hasattr(window, 'map_library'))
        window.show_map_library()
        library = window.map_library
        self.assertTrue(library.dark_theme)
        window.apply_theme('Light')
        self.assertFalse(library.dark_theme)
        window.apply_theme('Dark')
        self.assertTrue(library.dark_theme)

        library.close()
        window.show_map_library()
        self.assertIs(window.map_library, library)
        self.assertTrue(window.map_library.dark_theme)

    def test_legacy_exploration_uses_next_free_version(self):
        w = self.make_window()
        self.state.pml_id = ''
        (self.path.parent/'map v7.json').write_text('{}')
        with patch.object(w, 'choose_protected_map_mode', return_value='explore'):
            w.install_loaded_map(self.state, source_path=self.path.parent/'map.json')
        self.assertEqual(w.current_map_path.name, 'map v8.json')
        self.assertFalse(w.state.read_only)

    def test_automatic_pml_open_also_asks_and_preserves_original(self):
        w = self.make_window()
        w.state.process_status(self.status)
        w.live_status = self.status
        with patch.object(w, 'nearby_pml_maps', return_value=[(0, self.path, self.state)]), \
                patch.object(w, 'choose_protected_map_mode', return_value='mining') as choose:
            w.open_or_create_pml_for_current_position()
        choose.assert_called_once()
        self.assertTrue(w.state.mining_only)
        self.assertEqual(self.original, self.path.read_bytes())

    def test_library_flags_synchronize_active_map_and_keep_unsaved_points(self):
        w = self.make_window()
        self.state.protected = False
        MapperState.set_file_flags(self.path, favorite=True, protected=False)
        w.state = w.view.state = self.state
        w.current_map_path = self.path
        w.state.process_status(dict(self.status, Longitude=-8.999))
        count = len(w.state.points)
        with patch('map_library.maps_directory', return_value=self.root):
            library = MapLibraryWindow(w)
            library.select_system('Kappa')
            library.select_map(library.tree.topLevelItem(0).child(0), 1)
            with patch.object(w, 'prepare_to_replace_current_map', return_value=True):
                library.protected_check.setChecked(True)
            self.assertTrue(w.state.protected and w.state.mining_only)
            self.assertEqual(count, len(w.state.points))
            library.favorite_check.setChecked(False)
            self.assertFalse(w.state.favorite)
            library.close()

    def test_real_choice_dialog_restores_timers_on_cancel(self):
        from PySide6.QtCore import QTimer
        from PySide6.QtWidgets import QMessageBox
        w = self.make_window()
        w.timer.start(10000)
        observed = []
        def cancel():
            observed.append(not w.timer.isActive())
            for widget in QApplication.topLevelWidgets():
                if isinstance(widget, QMessageBox):
                    widget.reject()
        QTimer.singleShot(0, cancel)
        self.assertIsNone(w.choose_protected_map_mode())
        self.assertEqual(observed, [True])
        self.assertTrue(w.timer.isActive())
        self.assertEqual(w.timer.interval(), 10000)

    def test_both_open_modes_detect_stationary_rhino_without_star_system(self):
        w = self.make_window()
        w.status_path.write_text(json.dumps(dict(self.status, StarSystem='', Heading=123)))
        stamp = w.status_path.stat().st_mtime_ns
        for mode in ('mining', 'explore'):
            with self.subTest(mode=mode):
                state = MapperState()
                state.load(self.path)
                # Force the cached mtime so the status reader treats the file as current.
                w.last_mtime = stamp
                with patch.object(w, 'choose_protected_map_mode', return_value=mode), \
                        patch('qt_map_operations.maps_directory', return_value=self.root):
                    self.assertTrue(w.install_loaded_map(state, source_path=self.path))
                self.assertTrue(w.status_valid)
                self.assertTrue(w.state.in_srv)
                self.assertEqual(w.state.rhino_heading, 123)
                self.assertEqual(w.state.system, 'Kappa')
                self.assertEqual(w.state.rhino_lat, 38)
                self.assertEqual(w.state.mining_only, mode == 'mining')
                self.assertEqual(self.original, self.path.read_bytes())

    def test_open_does_not_use_rhino_on_another_body(self):
        w = self.make_window()
        w.status_path.write_text(json.dumps(dict(self.status, StarSystem='', BodyName='Outro')))
        state = MapperState()
        state.load(self.path)
        with patch.object(w, 'choose_protected_map_mode', return_value='mining'):
            w.install_loaded_map(state, source_path=self.path)
        self.assertFalse(w.status_valid)
        self.assertFalse(w.state.in_srv)
        self.assertEqual(w.state.body, 'Kappa 2')

    def test_deposit_box_is_clickable_on_both_lines(self):
        from PySide6.QtCore import QPointF
        from deposit_marker import deposit_bounds
        w = self.make_window()
        w.state = w.view.state = self.state
        item = self.state.deposits[0]
        bounds = deposit_bounds(w.view.screen(item['x'], item['y']), w.view.map_font(), item)
        for fraction in (.25, .75):
            found = w.view.marker_at(QPointF(bounds.right()-5, bounds.top()+bounds.height()*fraction))
            self.assertEqual(found[0], 'deposits')
            self.assertIs(found[1], item)

    def test_library_uses_one_column_and_preserves_card_height(self):
        with patch('map_library.maps_directory', return_value=self.root):
            library = MapLibraryWindow()
            library.select_system('Kappa')
            planet = library.tree.topLevelItem(0)
            planet.setExpanded(True)
            library.show()
            self.app.processEvents()
            self.assertEqual(library.tree.columnCount(), 1)
            self.assertEqual(library.tree.visualItemRect(planet.child(0)).height(), 48)
            self.assertTrue(planet.child(0).icon(0).isNull())
            library.close()

    def test_library_open_selected_map_passes_two_arguments_to_prepare_to_replace_current_map(self):
        w = self.make_window()
        w.state.pml_id = '6'
        w.current_map_path = self.path
        with patch('map_library.maps_directory', return_value=self.root), \
                patch('map_library.infer_legacy_pml'), \
                patch.object(w, 'install_loaded_map'), \
                patch.object(w, 'prepare_to_replace_current_map', return_value=True) as mock_prepare:
            library = MapLibraryWindow(w)
            library.select_system('Kappa')
            library.select_map(library.tree.topLevelItem(0).child(0), 1)
            library.open_selected_map()
            mock_prepare.assert_called_once_with(
                translate('MapperWindow', 'Open map'),
                translate('MapperWindow', 'opening the map'))
            library.close()


if __name__ == '__main__':
    unittest.main()
