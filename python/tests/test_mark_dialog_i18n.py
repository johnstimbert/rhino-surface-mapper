"""Verify MarkDialog translation ownership and preserved marker values.

These tests protect British English .ui source strings where present, class-owned
contexts, and marker values that must survive translation.
"""
import os
import unittest
from pathlib import Path
from unittest.mock import patch
from xml.etree import ElementTree

# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtWidgets import QApplication, QLabel

from qt_map_operations import MarkDialog


class MarkDialogI18nTests(unittest.TestCase):
    """Cover MarkDialog strings with a shared offscreen QApplication."""
    @classmethod
    def setUpClass(cls):
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def test_mark_dialog_uses_british_english_source_strings_and_context(self):
        with patch('qt_map_operations.translate', side_effect=lambda context, text: text) as translate:
            dialog = MarkDialog(None)
            self.addCleanup(dialog.close)

        # Runtime-created source strings remain British English.
        self.assertEqual(dialog.windowTitle(), 'Marker')
        self.assertEqual(
            {label.text() for label in dialog.findChildren(QLabel)},
            {'Marker name:', 'Bearing from north:', 'Distance from Rhino:'},
        )
        self.assertEqual(dialog.azimuth.toolTip(), '0° North · 90° East · 180° South · 270° West')
        self.assertTrue(all(call.args[0] == 'MarkDialog' for call in translate.call_args_list))

    def test_catalogue_contains_complete_mark_dialog_translations(self):
        path = Path(__file__).resolve().parents[1] / 'translations' / 'rsm_pt_PT.ts'
        root = ElementTree.parse(path).getroot()
        context = next(
            context for context in root.findall('context')
            if context.findtext('name') == 'MarkDialog'
        )
        messages = {
            message.findtext('source'): message.findtext('translation')
            for message in context.findall('message')
        }
        # Sources and translations stay owned by the MarkDialog context.
        self.assertEqual(messages, {
            'Edit marker': 'Alterar marca',
            'Marker': 'Marca',
            'Marker name:': 'Nome da marca:',
            'Bearing from north:': 'Azimute desde o norte:',
            'Distance from Rhino:': 'Distância ao Rhino:',
            '0° North · 90° East · 180° South · 270° West':
                '0° Norte · 90° Este · 180° Sul · 270° Oeste',
            'Original distance exceeds 99,999 m. It will be preserved unless this field is changed.':
                'A distância original excede 99 999 m. Será conservada se não alterar este campo.',
        })

    def test_mark_dialog_preserves_existing_marker_values(self):
        existing = {'name': 'Surface marker', 'azimuth': 37.5, 'distance': 123456.78}
        dialog = MarkDialog(None, existing)
        self.addCleanup(dialog.close)
        self.assertEqual(dialog.values(), existing)
        self.assertIn('Original distance exceeds 99,999 m.', dialog.distance.toolTip())


if __name__ == '__main__':
    unittest.main()
