"""Verify MapLibraryWindow translation ownership and dynamic badge text.

These tests protect British English .ui source strings, class-owned contexts, and
placeholders such as {count} that must survive translation.
"""
import os
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
from xml.etree import ElementTree

# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtWidgets import QApplication, QTreeWidgetItem

from map_library import MapLibraryWindow


class MapLibraryI18nTests(unittest.TestCase):
    """Cover MapLibraryWindow translation context and badge fixtures."""
    @classmethod
    def setUpClass(cls):
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def test_ui_source_strings_and_catalogue_context_are_scoped(self):
        root = Path(__file__).resolve().parents[1]
        ui_text = (root / 'ui' / 'map_library_window.ui').read_text(encoding='utf-8')
        # .ui source strings remain British English for the source catalogue.
        for source in (
                'View and manage maps', 'Favourite', 'Protected', 'Open map',
                'System:', 'Type at least 2 characters', 'Favourites',
                'Search for a system'):
            self.assertIn(f'<string>{source}</string>', ui_text)

        ts_root = ElementTree.parse(root / 'translations' / 'rsm_pt_PT.ts').getroot()
        map_library_context = next(
            (context for context in ts_root.findall('context')
             if context.findtext('name') == 'MapLibraryWindow'),
            None,
        )
        self.assertIsNotNone(map_library_context)
        messages = {
            message.findtext('source'): message.findtext('translation')
            for message in map_library_context.findall('message')
        }
        self.assertEqual(messages['Select a map to preview.'],
                         'Selecione um mapa para pré-visualizar.')
        self.assertEqual(messages['This map has no records yet.'],
                         'Este mapa ainda não contém registos.')
        self.assertEqual(messages['View and manage maps'], 'Ver e gerir mapas')
        # Dynamic placeholders must survive translation unchanged.
        self.assertEqual(messages['{count} deposits'], '{count} depósitos')
        self.assertEqual(messages['{deposits} deposits · {rigs} rigs · {marks} marks'],
                         '{deposits} depósitos · {rigs} rigs · {marks} marcas')

    def test_dynamic_badge_text_uses_context_without_translating_domain_count(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        (root / 'Kappa').mkdir()
        with patch('map_library.maps_directory', return_value=root), \
                patch('map_library.translate', side_effect=lambda context, text: text) as translate:
            library = MapLibraryWindow()
            self.addCleanup(library.close)
            item = QTreeWidgetItem(['Kappa 1 [1].json'])
            library.tree.addTopLevelItem(item)
            library.update_badges(item, SimpleNamespace(
                deposits=[{}, {}], favorite=True, protected=True))

        self.assertEqual(item.toolTip(0), '2 deposits · Favourite · Protected')
        self.assertTrue(translate.call_count >= 3)
        # Every dynamic string belongs to the owning MapLibraryWindow context.
        self.assertTrue(all(call.args[0] == 'MapLibraryWindow'
                            for call in translate.call_args_list))


if __name__ == '__main__':
    unittest.main()
