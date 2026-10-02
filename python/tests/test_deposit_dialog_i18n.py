"""Verify DepositDialog translation ownership and canonical value separation.

These tests protect the dialog context, British English .ui source strings where
present, and placeholders or domain values that must survive translation.
"""
import os
import unittest
from pathlib import Path
from unittest.mock import patch
from xml.etree import ElementTree

# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')

from PySide6.QtWidgets import QApplication

from qt_map_operations import DepositDialog


class DepositDialogI18nTests(unittest.TestCase):
    """Cover DepositDialog strings with a shared offscreen QApplication."""
    @classmethod
    def setUpClass(cls):
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def test_catalogue_preserves_deposit_dialog_translations(self):
        path = Path(__file__).resolve().parents[1] / 'translations' / 'rsm_pt_PT.ts'
        root = ElementTree.parse(path).getroot()
        deposit_context = next(
            (context for context in root.findall('context')
             if context.findtext('name') == 'DepositDialog'),
            None,
        )
        self.assertIsNotNone(deposit_context)
        messages = {
            message.findtext('source'): message.findtext('translation')
            for message in deposit_context.findall('message')
        }
        # Sources and translations stay owned by the DepositDialog context.
        self.assertEqual(messages, {
            'Mark deposit': 'Marcar depósito',
            'Edit deposit': 'Editar depósito',
            'Name:': 'Nome:',
            'Size:': 'Tamanho:',
            'Number of rigs:': 'Nº rigs:',
            'Small': 'Pequeno',
            'Medium': 'Médio',
            'Large': 'Grande',
            'Huge': 'Enorme',
            'rigs': 'rigs',
        })

    def test_python_created_strings_use_deposit_dialog_context(self):
        with patch('qt_map_operations.translate', side_effect=lambda context, text: f'{context}:{text}') as translate:
            dialog = DepositDialog(None)
            self.addCleanup(dialog.close)

        self.assertEqual(dialog.windowTitle(), 'DepositDialog:Mark deposit')
        self.assertEqual(dialog.size.itemText(0), 'DepositDialog:Small')
        self.assertEqual(dialog.rigs.suffix(), ' DepositDialog:rigs')
        self.assertTrue(translate.call_count >= 6)
        # Python-created strings must use the same owning class context.
        self.assertTrue(all(call.args[0] == 'DepositDialog' for call in translate.call_args_list))


if __name__ == '__main__':
    unittest.main()
