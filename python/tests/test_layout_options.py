"""Exercise fixed layout controls, options persistence, and theme behaviour.

These tests protect UI contracts and preference merging so future settings do not
break the map layout or discard unknown persisted keys.
"""
import json
import os
import tempfile
import unittest
from pathlib import Path
# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtWidgets import QApplication, QPushButton, QSpinBox
from PySide6.QtCore import Qt
from layout_options import (OP_MAPS, OP_MARK, SECTION_LAYOUT, THEME_DARK,
                            THEME_LABELS, THEME_LIGHT, THEME_SYSTEM,
                            _theme_is_dark)
from rhino_surface_mapper_qt import MapperWindow


class LayoutTests(unittest.TestCase):
    """Cover layout and option fixtures built from a temporary MapperWindow."""
    @classmethod
    def setUpClass(cls):
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.window = MapperWindow(
            Path(self.temp.name)/'Status.json', game_running_check=lambda: True)
        self.window.options_path = Path(self.temp.name)/'options.json'
        # Stop timers so option-panel assertions are not affected by polling.
        for timer in (self.window.timer,self.window.radar_timer,self.window.assist_timer):
            timer.stop()
        self.window.show()
        self.app.processEvents()

    def tearDown(self):
        self.window.close()
        self.temp.cleanup()

    def test_fixed_buttons_and_map_expand(self):
        w = self.window
        sizes = [b.size() for b,_ in w.fixed_buttons]
        w.resize(1600,950)
        self.app.processEvents()
        self.assertEqual(sizes,[b.size() for b,_ in w.fixed_buttons])
        width = w.view.width()
        w.options_button.setChecked(True)
        self.app.processEvents()
        self.assertLess(w.view.width(),width)
        self.assertTrue(all(not header.isChecked() for header,_ in w.option_sections.values()))
        w.options_button.setChecked(False)
        self.app.processEvents()
        self.assertEqual(w.view.width(),width)
        menu = w.findChild(QPushButton,'maps_menu').menu()
        self.assertEqual([a.text() for a in menu.actions()],['Open','Save','New','View and manage'])
        maps = w.findChildren(QPushButton, 'maps_menu')
        self.assertEqual(len(maps), 1)
        self.assertIs(w.op_buttons[OP_MAPS], maps[0])
        self.assertTrue(maps[0].isVisible())
        self.assertEqual(len([button for button in w.findChildren(QPushButton)
                              if button.text() == 'Options']), 1)
        self.assertEqual(len([button for button in w.findChildren(QPushButton)
                              if button.text() == 'Choose Status.json']), 1)

    def test_preferences_preserve_existing_keys(self):
        w = self.window
        w.preferences['future_setting'] = 'preservar'
        field = w.options_panel.findChild(QSpinBox,'rhino_size')
        field.setValue(73)
        w.theme_selector.setCurrentText('Dark')
        saved = json.loads(w.options_path.read_text(encoding='utf-8'))
        self.assertEqual(saved['rhino_size'],73)
        self.assertEqual(saved['future_setting'],'preservar')
        self.assertEqual(saved['theme'],THEME_DARK)
        self.assertEqual(w.view.rhino_height,73)
        self.assertTrue(w.view.dark_theme)

    def test_theme_resolution_and_widget_stylesheet(self):
        w = self.window
        self.assertFalse(_theme_is_dark(THEME_LIGHT, Qt.ColorScheme.Dark))
        self.assertTrue(_theme_is_dark(THEME_DARK, Qt.ColorScheme.Light))
        self.assertTrue(_theme_is_dark(THEME_SYSTEM, Qt.ColorScheme.Dark))
        self.assertFalse(_theme_is_dark(THEME_SYSTEM, Qt.ColorScheme.Light))

        w.apply_theme('Light')
        self.assertFalse(w.view.dark_theme)
        self.assertIn('background: #edf1f4; color: #233448;', w.styleSheet())
        self.assertIn('background: #ffffff; border: 1px solid #cbd5dd;', w.styleSheet())

        w.apply_theme('Dark')
        self.assertTrue(w.view.dark_theme)
        self.assertIn('background: #202429; color: #edf1f5;', w.styleSheet())
        self.assertIn('background: #2c3239; border: 1px solid #46515d;', w.styleSheet())

    def test_spin_arrows_with_mouse_in_both_themes(self):
        from PySide6.QtCore import Qt
        from PySide6.QtWidgets import QStyle, QStyleOptionSpinBox
        from PySide6.QtTest import QTest
        w = self.window
        w.options_button.setChecked(True)
        for theme in ('Light', 'Dark'):
            w.theme_selector.setCurrentText(theme)
            for title, (header, content) in w.option_sections.items():
                header.setChecked(True)
                for spin in content.findChildren(QSpinBox):
                    value = (spin.minimum()+spin.maximum())//2
                    spin.setValue(value)
                    w.options_panel.ensureWidgetVisible(spin)
                    self.app.processEvents()
                    option = QStyleOptionSpinBox()
                    spin.initStyleOption(option)
                    up = spin.style().subControlRect(QStyle.ComplexControl.CC_SpinBox, option, QStyle.SubControl.SC_SpinBoxUp, spin)
                    down = spin.style().subControlRect(QStyle.ComplexControl.CC_SpinBox, option, QStyle.SubControl.SC_SpinBoxDown, spin)
                    self.assertFalse(up.intersects(down), (title, up, down))
                    self.assertEqual(up.center().x(),down.center().x(),title)
                    self.assertLess(up.center().y(),down.center().y(),title)
                    QTest.mouseClick(spin,Qt.MouseButton.LeftButton,pos=up.center())
                    self.assertEqual(spin.value(),value+spin.singleStep(),(theme,title,spin.objectName()))
                    QTest.mouseClick(spin,Qt.MouseButton.LeftButton,pos=down.center())
                    self.assertEqual(spin.value(),value,(theme,title,spin.objectName()))
                header.setChecked(False)

    def test_dark_map_uses_selected_background(self):
        from unittest.mock import patch
        from PySide6.QtGui import QColor
        w = self.window
        w.preferences.pop('map_background',None)
        w.apply_theme('Dark')
        self.app.processEvents()
        self.assertEqual(w.view.grab().toImage().pixelColor(40,90).name(),'#606060')
        with patch('layout_options.QColorDialog.getColor',return_value=QColor('#8899aa')):
            w.color_buttons['map_background'].click()
        self.app.processEvents()
        self.assertEqual(w.view.grab().toImage().pixelColor(40,90).name(),'#8899aa')
        w.apply_theme('Light')
        w.apply_theme('Dark')
        self.assertEqual(w.view.colors['map_background'],'#8899aa')

        w.setting_fields['grid_color'][1]('#112233')
        w.apply_theme('Light')
        w.apply_theme('Dark')
        self.assertEqual(w.view.colors['grid_color'],'#112233')

    def test_scanned_area_uses_wave_color(self):
        w = self.window
        w.state.center_lat = 0
        w.state.center_lon = 0
        w.state.radar_coverage = [dict(x=0,y=0,radius=2000)]
        w.view.colors['wave_color'] = '#bb6633'
        self.app.processEvents()
        picture = w.view.grab().toImage()
        self.assertEqual(picture.pixelColor(picture.width()//2+40,picture.height()//2+40).name(),'#bb6633')

    def test_settings_export_import_and_reset(self):
        from unittest.mock import patch
        w = self.window
        target = Path(self.temp.name)/'preset.json'
        w.setting_fields['rhino_size'][1](81)
        w.setting_fields['theme'][1]('Dark')
        w.setting_fields['wave_color'][1]('#123456')
        with patch('layout_options.QFileDialog.getSaveFileName',return_value=(str(target),'')):
            w.export_settings()
        w.reset_settings()
        self.assertEqual(w.view.rhino_height,56)
        self.assertEqual(w.theme_selector.currentText(),THEME_LABELS[THEME_SYSTEM])
        with patch('layout_options.QFileDialog.getOpenFileName',return_value=(str(target),'')):
            w.import_settings()
        self.assertEqual(w.view.rhino_height,81)
        self.assertEqual(w.view.colors['wave_color'],'#123456')
        self.assertEqual(w.theme_selector.currentText(),'Dark')
        self.assertEqual(json.loads(w.options_path.read_text())['rhino_size'],81)
        target.write_text(json.dumps({'rhino_settings_version':1,'settings':{'rhino_size':75,'assist_speed':-1}}))
        with patch('layout_options.QFileDialog.getOpenFileName',return_value=(str(target),'')), patch('layout_options.QMessageBox.warning') as warning:
            w.import_settings()
        warning.assert_called_once()
        self.assertEqual(w.view.rhino_height,81)

    def test_operation_and_section_identity_is_not_display_text(self):
        """Operation and section lookup keys remain independent of labels."""
        w = self.window
        button = w.op_buttons[OP_MARK]
        w.op_buttons[OP_MARK].setText('Texto alterado')
        self.assertIs(w.op_buttons[OP_MARK], button)

        header, content = w.option_sections[SECTION_LAYOUT]
        header.setText('Título alterado')
        w.setting_fields['font_size'][1](11)
        for callback in w.option_resets[SECTION_LAYOUT]:
            callback()
        self.assertEqual(w.setting_fields['font_size'][0](),9)
        self.assertEqual(header.text(),'Título alterado')

    def test_theme_uses_stable_values_and_accepts_legacy_label(self):
        w = self.window
        w.theme_selector.setCurrentText('Dark')
        self.assertEqual(w.theme_selector.currentData(), THEME_DARK)
        self.assertEqual(json.loads(w.options_path.read_text(encoding='utf-8'))['theme'], THEME_DARK)

        w.setting_fields['theme'][1]('Como o Windows')
        self.assertEqual(w.theme_selector.currentData(), THEME_SYSTEM)
        self.assertTrue(w.setting_fields['theme'][2]('Como o Windows'))
        self.assertTrue(w.setting_fields['theme'][2](THEME_LIGHT))

    def test_language_selector_uses_stable_codes_and_persists_selection(self):
        from PySide6.QtWidgets import QComboBox
        w = self.window
        selector = w.options_panel.findChild(QComboBox, 'language_selector')
        self.assertIsNotNone(selector)
        self.assertEqual(selector.findData('en_GB') >= 0, True)
        self.assertEqual(selector.findData('pt_PT') >= 0, True)
        selector.setCurrentIndex(selector.findData('pt_PT'))
        self.assertEqual(selector.currentData(), 'pt_PT')
        self.assertEqual(json.loads(w.options_path.read_text(encoding='utf-8'))['language'], 'pt_PT')

    def test_legacy_persisted_theme_loads_as_system_theme(self):
        from unittest.mock import patch
        with patch('rhino_surface_mapper_qt.load_preferences', return_value={'theme': 'Como o Windows'}):
            window = MapperWindow(
                Path(self.temp.name)/'Status-legacy.json', game_running_check=lambda: True)
        try:
            for timer in (window.timer,window.radar_timer,window.assist_timer):
                timer.stop()
            self.assertEqual(window.preferences['theme'], THEME_SYSTEM)
            self.assertEqual(window.theme_selector.currentData(), THEME_SYSTEM)
        finally:
            window.close()

    def test_stable_persisted_theme_loads_directly(self):
        from unittest.mock import patch
        with patch('rhino_surface_mapper_qt.load_preferences', return_value={'theme': THEME_DARK}):
            window = MapperWindow(
                Path(self.temp.name)/'Status-stable.json', game_running_check=lambda: True)
        try:
            for timer in (window.timer,window.radar_timer,window.assist_timer):
                timer.stop()
            self.assertEqual(window.preferences['theme'], THEME_DARK)
            self.assertEqual(window.theme_selector.currentData(), THEME_DARK)
            self.assertTrue(window.view.dark_theme)
        finally:
            window.close()
