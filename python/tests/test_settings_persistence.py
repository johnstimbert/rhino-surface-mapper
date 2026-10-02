"""Behavioural tests for settings persistence helpers.

These tests specify deterministic headless JSON preference and preset handling
used by the Qt application without coupling the helpers to UI state.
"""

import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from settings_persistence import (load_preferences, read_exported_settings,
                                   save_preferences, write_exported_settings)


class SettingsPersistenceTests(unittest.TestCase):
    """Covers preference and exported-settings files under a temporary root."""

    def setUp(self):
        self.temp = TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def test_flat_preferences_round_trip(self):
        path = self.root / 'options.json'
        preferences = {'scanner_group': 4, 'theme': 'Dark', 'future': 'keep'}

        save_preferences(path, preferences)

        self.assertEqual(load_preferences(path), preferences)

    def test_missing_and_malformed_preferences_fall_back_to_empty(self):
        path = self.root / 'options.json'
        self.assertEqual(load_preferences(path), {})
        path.write_text('{broken', encoding='utf-8')
        self.assertEqual(load_preferences(path), {})

    def test_exported_settings_round_trip(self):
        path = self.root / 'preset.json'
        settings = {'theme': 'Dark', 'rhino_size': 81}

        write_exported_settings(path, settings)

        self.assertEqual(read_exported_settings(path), settings)
        self.assertEqual(json.loads(path.read_text(encoding='utf-8'))[
            'rhino_settings_version'], 1)

    def test_exported_settings_accepts_utf8_bom(self):
        path = self.root / 'preset.json'
        path.write_text(json.dumps({
            'rhino_settings_version': 1,
            'settings': {'theme': 'Light'},
        }, ensure_ascii=False), encoding='utf-8-sig')

        self.assertEqual(read_exported_settings(path), {'theme': 'Light'})

    def test_exported_settings_rejects_invalid_structure(self):
        """Settings imports must reject unsupported or empty preset payloads."""
        path = self.root / 'preset.json'
        for document in (
                {'rhino_settings_version': 2, 'settings': {'theme': 'Dark'}},
                {'rhino_settings_version': 1},
                {'rhino_settings_version': 1, 'settings': []},
                {'rhino_settings_version': 1, 'settings': {}},
        ):
            path.write_text(json.dumps(document), encoding='utf-8')
            with self.assertRaises(ValueError):
                read_exported_settings(path)

    def test_save_and_export_propagate_io_or_serialization_errors(self):
        with self.assertRaises(OSError):
            save_preferences(self.root / 'missing' / 'options.json', {})
        with self.assertRaises(OSError):
            write_exported_settings(self.root / 'missing' / 'preset.json', {})

        with self.assertRaises(TypeError):
            save_preferences(self.root / 'options.json', {'bad': object()})
        with self.assertRaises(TypeError):
            write_exported_settings(self.root / 'preset.json', {'bad': object()})
