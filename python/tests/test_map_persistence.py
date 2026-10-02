"""Behavioural tests for the low-level map persistence module.

These tests specify deterministic, headless file I/O guarantees that protect map
contents, metadata flags, and timestamps independently of the Qt interface.
"""

import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from map_persistence import (
    is_map_file_protected,
    read_file_timestamps,
    read_map_json,
    update_map_file_flags,
    write_map_json,
)


class TestMapPersistence(unittest.TestCase):
    """Covers atomic JSON writes, flag updates, and timestamp reads in a temp map."""

    def setUp(self):
        self.temp_dir = tempfile.TemporaryDirectory()
        self.path = Path(self.temp_dir.name) / "test_map.json"

    def tearDown(self):
        self.temp_dir.cleanup()

    def test_write_and_read_map_json(self):
        sample_data = {"system": "Sol", "body": "Earth", "favorite": True, "protected": False}
        write_map_json(self.path, sample_data)
        self.assertTrue(self.path.exists())

        loaded = read_map_json(self.path)
        self.assertEqual(loaded, sample_data)
        self.assertEqual(list(self.path.parent.glob('*.tmp')), [])

    def test_write_replaces_existing_map_completely(self):
        write_map_json(self.path, {"old": True})
        write_map_json(self.path, {"new": "á", "values": [1, 2, 3]})

        self.assertEqual(read_map_json(self.path), {"new": "á", "values": [1, 2, 3]})
        self.assertEqual(list(self.path.parent.glob('*.tmp')), [])

    def test_write_failure_before_replacement_preserves_existing_map(self):
        write_map_json(self.path, {"original": True})
        original = self.path.read_bytes()

        with patch("map_persistence.tempfile.NamedTemporaryFile", side_effect=OSError("write failed")):
            with self.assertRaises(OSError):
                write_map_json(self.path, {"replacement": True})

        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(list(self.path.parent.glob('*.tmp')), [])

    def test_serialization_failure_preserves_existing_map(self):
        write_map_json(self.path, {"original": True})
        original = self.path.read_bytes()

        with patch("map_persistence.json.dumps", side_effect=TypeError("serialize failed")):
            with self.assertRaises(TypeError):
                write_map_json(self.path, {"replacement": object()})

        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(list(self.path.parent.glob('*.tmp')), [])

    def test_replace_failure_preserves_existing_map_and_cleans_temporary(self):
        write_map_json(self.path, {"original": True})
        original = self.path.read_bytes()

        with patch("map_persistence.os.replace", side_effect=OSError("replace failed")):
            with self.assertRaises(OSError):
                write_map_json(self.path, {"replacement": True})

        self.assertEqual(self.path.read_bytes(), original)
        self.assertEqual(list(self.path.parent.glob('*.tmp')), [])

    def test_is_map_file_protected(self):
        self.assertFalse(is_map_file_protected(self.path))

        write_map_json(self.path, {"protected": False})
        self.assertFalse(is_map_file_protected(self.path))

        write_map_json(self.path, {"protected": True})
        self.assertTrue(is_map_file_protected(self.path))

    def test_update_map_file_flags_validates_booleans(self):
        with self.assertRaises(ValueError):
            update_map_file_flags(self.path, favorite="yes", protected=False)
        with self.assertRaises(ValueError):
            update_map_file_flags(self.path, favorite=True, protected=1)

    def test_update_map_file_flags_atomically_updates_and_preserves_mtime(self):
        data = {"system": "LHS 3447", "body": "Star", "favorite": False, "protected": False}
        write_map_json(self.path, data)

        initial_stats = self.path.stat()

        update_map_file_flags(self.path, favorite=True, protected=True)

        loaded = read_map_json(self.path)
        self.assertTrue(loaded["favorite"])
        self.assertTrue(loaded["protected"])

        updated_stats = self.path.stat()
        # Flag-only updates must not make library cards look like newly saved maps.
        self.assertEqual(initial_stats.st_mtime_ns, updated_stats.st_mtime_ns)

    def test_read_file_timestamps(self):
        data = {"system": "Sol"}
        write_map_json(self.path, data)

        created_at, last_saved_at = read_file_timestamps(self.path)
        self.assertIsInstance(created_at, str)
        self.assertIsInstance(last_saved_at, str)
        self.assertIn("T", created_at)
        self.assertIn("T", last_saved_at)


if __name__ == "__main__":
    unittest.main()
