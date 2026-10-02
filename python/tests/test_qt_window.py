"""Exercise the main Qt window, map rendering, overlay, and radar UI contracts.

These tests protect offscreen interaction details that regress easily when the
live telemetry loop, overlay visibility, or custom painting code changes.
"""
import importlib.util
import json
import os
import tempfile
import unittest
from pathlib import Path
# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM','offscreen')

@unittest.skipUnless(importlib.util.find_spec('PySide6'), 'PySide6 não instalado')
class QtWindowTests(unittest.TestCase):
    """Cover MapperWindow fixtures that create isolated status files per test."""
    def test_overlay_can_drag_and_resize_across_refresh_without_game_focus(self):
        from types import SimpleNamespace
        from PySide6.QtCore import QPoint, QPointF, Qt
        from rhino_surface_mapper_qt import MapperWindow
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'Status.json'
            path.write_text(json.dumps(dict(Flags=0x04000000, Latitude=0,
                Longitude=0, Heading=0, BodyName='Test')), encoding='utf-8')
            window = MapperWindow(path, game_running_check=lambda: True)
            try:
                # Stop timers so drag and refresh assertions are deterministic.
                window.timer.stop()
                window.radar_timer.stop()
                # Overlay visibility follows navigation state, not window focus.
                window.radar_input.game_focused = lambda: False
                window.start_search()
                overlay = window.overlay
                overlay.setGeometry(100, 100, 360, 150)
                def event(local, global_pos):
                    return SimpleNamespace(position=lambda: QPointF(*local),
                        globalPosition=lambda: QPointF(*global_pos),
                        button=lambda: Qt.MouseButton.LeftButton,
                        buttons=lambda: Qt.MouseButton.LeftButton)
                overlay.mousePressEvent(event((180,75), (280,175)))
                window.refresh()
                self.assertTrue(overlay.isVisible())
                overlay.mouseMoveEvent(event((180,75), (320,205)))
                self.assertEqual(overlay.pos(), QPoint(140,130))
                overlay.mouseReleaseEvent(event((180,75), (320,205)))
                self.assertFalse(overlay.dragging)
                overlay.mousePressEvent(event((359,149), (499,279)))
                window.refresh()
                self.assertTrue(overlay.isVisible())
                overlay.mouseMoveEvent(event((459,189), (599,319)))
                overlay.mouseReleaseEvent(event((459,189), (599,319)))
                self.assertGreater(overlay.width(), 360)
                # Resizing preserves the overlay 2.4:1 navigation aspect ratio.
                self.assertAlmostEqual(overlay.width()/overlay.height(), 2.4, delta=.03)
                self.assertFalse(overlay.resizing)
            finally:
                window.close()

    @classmethod
    def setUpClass(cls):
        from PySide6.QtWidgets import QApplication
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def test_travel_coverage_survives_save_without_bridging_breaks(self):
        from mapper_core import MapperState
        from rhino_surface_mapper_qt import MapView
        state = MapperState()
        state.center_lat = state.center_lon = 0
        state.coverage_width_m = 100
        state.points = [dict(x=-200,y=0),dict(x=-100,y=0),
                        dict(x=200,y=0,break_before=True)]
        # Persisted track coverage must not need a synthetic far-away point to render.
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'coverage.json'
            state.save(path)
            restored = MapperState()
            restored.load(path)
            for candidate in (state, restored):
                view = MapView(candidate)
                view.resize(800,600)
                view.scale = 1
                snapshot = view.grab().toImage()
                inside = view.screen(-150,25)
                gap = view.screen(50,25)
                self.assertEqual(snapshot.pixelColor(int(inside.x()),int(inside.y())).name(), '#8cbd8c')
                self.assertNotEqual(snapshot.pixelColor(int(gap.x()),int(gap.y())).name(), '#8cbd8c')
                view.close()

    def test_live_status_map_zoom_and_shutdown(self):
        from PySide6.QtCore import QPointF
        from rhino_surface_mapper_qt import MapperWindow
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'Status.json'
            path.write_text(json.dumps({'Flags':0x04000000,'Latitude':38,'Longitude':-9,'Heading':90,'BodyName':'Test','PlanetRadius':6371000}),encoding='utf-8')
            window = MapperWindow(path, game_running_check=lambda: True)
            try:
                window.radar_input.game_focused = lambda: True
                window.show()
                self.assertFalse(window.overlay_button.isEnabled())
                window.toggle_overlay()
                self.assertFalse(window.overlay.isVisible())
                self.assertFalse(window.overlay.isVisible())
                window.poll()
                self.assertFalse(window.overlay.isVisible())
                self.app.processEvents()
                self.assertEqual(len(window.state.points),1)
                self.assertTrue(window.view.rhino_renderer.isValid())
                window.start_search()
                self.assertTrue(window.overlay_button.isEnabled())
                self.assertEqual(window.overlay.heading_text,'<<<<< 000°')
                self.assertTrue(window.overlay.isVisible())
                window.state.in_srv = False
                window.refresh()
                self.assertFalse(window.overlay.isVisible())
                window.state.in_srv = True
                window.refresh()
                self.assertTrue(window.overlay.isVisible())
                window.radar_input.game_focused = lambda: False
                window.refresh()
                self.assertTrue(window.overlay.isVisible())
                window.radar_input.game_focused = lambda: True
                window.refresh()
                self.assertTrue(window.overlay.isVisible())
                point = QPointF(120,180)
                before = window.view.world(point)
                window.view.zoom_at(point,1.15)
                after = window.view.world(point)
                self.assertAlmostEqual(before.x(),after.x())
                self.assertAlmostEqual(before.y(),after.y())
                self.assertFalse(window.view.grab().isNull())
                window.toggle_overlay()
                self.assertFalse(window.overlay.isVisible())
                window.refresh()
                self.assertFalse(window.overlay.isVisible())
                window.toggle_overlay()
                self.assertTrue(window.overlay.isVisible())
                window.state.search_started = False
                window.refresh()
                self.assertFalse(window.overlay.isVisible())
                self.assertFalse(window.overlay_button.isEnabled())
                window.toggle_overlay()
                self.assertFalse(window.overlay.isVisible())
                window.state.search_started = True
                window.refresh()
                self.assertTrue(window.overlay.isVisible())
                self.assertTrue(window.overlay_button.isEnabled())
                path.write_text('{',encoding='utf-8')
                old = window.last_mtime
                window.poll()
                self.assertEqual(window.last_mtime,old)
            finally:
                window.close()
            # Closing the window must shut down timers to avoid leaked Qt work.
            self.assertFalse(window.timer.isActive())
            self.assertFalse(window.overlay.isVisible())
    def test_map_font_adapts_to_logical_monitor_size(self):
        """The map font scales with logical monitor size without physical screens."""
        from types import SimpleNamespace
        from unittest.mock import patch
        from PySide6.QtCore import QRect
        from PySide6.QtGui import QFont
        from rhino_surface_mapper_qt import MapView
        source = SimpleNamespace(font=lambda: QFont('Arial',10))
        for width,height,expected in [(1920,1080,10),(3840,2160,20),(2560,1440,10*4/3),(1080,1920,10)]:
            with self.subTest(width=width,height=height):
                screen = SimpleNamespace(geometry=lambda: QRect(0,0,width,height))
                with patch('rhino_surface_mapper_qt.QWidget',SimpleNamespace(screen=lambda widget: screen)):
                    self.assertAlmostEqual(MapView.map_font(source).pointSizeF(),expected,places=2)

    def test_track_connects_without_search_but_respects_breaks(self):
        from mapper_core import MapperState
        from rhino_surface_mapper_qt import MapView
        state = MapperState()
        state.center_lat = state.center_lon = 0
        state.points = [dict(x=-100, y=0), dict(x=100, y=0)]
        view = MapView(state)
        view.resize(400, 300)
        view.scale = 1
        try:
            view.show()
            self.app.processEvents()
            connected = view.grab().toImage().pixelColor(210, 150)
            state.points[1]['break_before'] = True
            view.update()
            self.app.processEvents()
            broken = view.grab().toImage().pixelColor(210, 150)
            self.assertNotEqual(connected, broken)
            self.assertLess(connected.red(), broken.red())
        finally:
            view.close()

    def test_angle_inputs_are_integer_and_polling_is_50_ms(self):
        from PySide6.QtGui import QValidator
        from PySide6.QtWidgets import QSpinBox
        from qt_map_operations import MarkDialog
        from rhino_surface_mapper_qt import MapperWindow
        test_dir = tempfile.TemporaryDirectory()
        self.addCleanup(test_dir.cleanup)
        window = MapperWindow(
            Path(test_dir.name)/'Status.json', game_running_check=lambda: True)
        dialog = MarkDialog(window)
        try:
            self.assertEqual(window.timer.interval(), 50)
            self.assertEqual(window.parameter_spins['coverage_width_m'].suffix(), ' m')
            self.assertEqual(window.parameter_spins['scanner_range_m'].suffix(), ' m')
            self.assertEqual(window.search_azimuth_spin.suffix(), 'º')
            for spin in (dialog.azimuth, window.search_azimuth_spin):
                self.assertIsInstance(spin, QSpinBox)
                self.assertEqual((spin.minimum(), spin.maximum()), (0, 359))
                self.assertTrue(spin.wrapping())
                spin.setValue(0)
                spin.stepBy(-1)
                self.assertEqual(spin.value(), 359)
                spin.setValue(359)
                spin.stepBy(1)
                self.assertEqual(spin.value(), 0)
                for value in ('1.5', '1,5', '360', '-1'):
                    self.assertNotEqual(spin.validate(value, len(value))[0], QValidator.State.Acceptable)
        finally:
            dialog.close()
            window.close()

    def test_unchanged_status_does_not_request_map_redraw(self):
        from unittest.mock import patch
        from rhino_surface_mapper_qt import MapperWindow
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'Status.json'
            data = dict(Flags=0x04000000, Latitude=38, Longitude=-9, Heading=90, BodyName='Test')
            path.write_text(json.dumps(data), encoding='utf-8')
            window = MapperWindow(path, game_running_check=lambda: True)
            try:
                window.poll()
                with patch.object(window.view, 'update') as redraw:
                    window.poll()
                    redraw.assert_not_called()
                    window.last_mtime = None
                    window.poll()
                    redraw.assert_not_called()
                    data['Heading'] = 95
                    path.write_text(json.dumps(data), encoding='utf-8')
                    window.last_mtime = None
                    window.poll()
                    redraw.assert_called_once()
                with patch.object(window.overlay, 'update') as redraw:
                    window.refresh(redraw_map=False)
                    redraw.assert_not_called()
            finally:
                window.close()

    def test_radar_requires_analysis_group_focus_and_live_status(self):
        import time
        from unittest.mock import patch
        from rhino_surface_mapper_qt import MapperWindow
        test_dir = tempfile.TemporaryDirectory()
        self.addCleanup(test_dir.cleanup)
        window = MapperWindow(
            Path(test_dir.name)/'Status.json', game_running_check=lambda: True)
        try:
            data = dict(Flags=0x04000000|0x08000000, Latitude=0, Longitude=0,
                        BodyName='Test', FireGroup=0, GuiFocus=0)
            window.state.process_status(data)
            window.live_status = data
            window.status_valid = True
            window.scanner_group = 0
            window.radar_input.bindings = [[('Mouse','Mouse_1')]]
            with patch.object(window.radar_input, 'game_focused', return_value=True) as focus, \
                    patch.object(window.radar_input, 'down', return_value=False) as down:
                window.update_radar()
                down.return_value = True
                window.update_radar()
                self.assertEqual(len(window.state.radar_coverage), 1)
                window.view.radar.waves.clear()
                for changes in ({'Flags':0x04000000}, {'FireGroup':1}, {'GuiFocus':1}):
                    original = dict(data)
                    data.update(changes)
                    window.update_radar()
                    self.assertEqual(len(window.state.radar_coverage), 1)
                    data.clear()
                    data.update(original)
                focus.return_value = False
                window.update_radar()
                self.assertEqual(len(window.state.radar_coverage), 1)
                focus.return_value = True
                window.status_valid = False
                window.update_radar()
                self.assertEqual(len(window.state.radar_coverage), 1)
        finally:
            window.close()

    def test_start_new_and_loaded_map_accept_first_click_without_file_change(self):
        from unittest.mock import patch
        from PySide6.QtWidgets import QMessageBox
        from rhino_surface_mapper_qt import MapperWindow
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'Status.json'
            saved = Path(directory)/'map.json'
            data = dict(Flags=0x04000000|0x08000000, Latitude=0, Longitude=0,
                        BodyName='Test', Heading=0, FireGroup=0, GuiFocus=0)
            path.write_text(json.dumps(data), encoding='utf-8')
            window = MapperWindow(path, game_running_check=lambda: True)
            window.timer.stop()
            window.radar_timer.stop()
            try:
                self.assertTrue(window.status_valid)  # The constructor reads initial status.
                window.state.save(saved)
                window.view.colors.update(trail_color='#2f7d32', coverage_color='#8cbd8c')
                window.scanner_group = 0
                window.radar_input.bindings = [[('Mouse','Mouse_1')]]
                with patch.object(window.radar_input, 'game_focused', return_value=True), \
                        patch.object(window.radar_input, 'down', return_value=False) as down, \
                        patch('rhino_surface_mapper_qt.time.monotonic', return_value=1000000), \
                        patch('qt_map_operations.QMessageBox.question', return_value=QMessageBox.StandardButton.Yes), \
                        patch('qt_map_operations.QFileDialog.getOpenFileName', return_value=(str(saved), '')), \
                        patch.object(window, 'save_new_pml_version', return_value=saved):
                    for operation in (lambda: None, window.new_map, window.load_map):
                        operation()
                        self.assertIn('coverage_width_m', window.parameter_spins)
                        self.assertIsNotNone(window.state.rhino_lat)
                        window.view.resize(800, 600)
                        snapshot = window.view.grab().toImage()
                        center = window.view.screen(*window.state.llxy(window.state.rhino_lat, window.state.rhino_lon))
                        radius = window.state.coverage_width_m*window.view.scale/2
                        pixel = snapshot.pixelColor(int(center.x()+radius), int(center.y()))
                        self.assertGreater(pixel.green(), pixel.red())
                        self.assertLess(pixel.red(), 100)
                        # Check the filled interior, not just the coverage outline.
                        interior = snapshot.pixelColor(int(center.x()+radius*.6), int(center.y()+radius*.2))
                        self.assertGreater(interior.green(), interior.red())
                        self.assertLess(interior.red(), 200)
                        down.return_value = False
                        window.update_radar()
                        before = len(window.state.radar_coverage)
                        down.return_value = True
                        window.update_radar()
                        self.assertEqual(len(window.state.radar_coverage), before+1)
            finally:
                window.close()


