"""Tests for selecting and incrementally reading Elite Dangerous journals."""

import json
import tempfile
import unittest
from pathlib import Path

from elite_dangerous.journal import JournalIdentityReader


class JournalIdentityReaderTests(unittest.TestCase):
    """Covers journal identity recovery across sessions and partial writes."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.path = self.root / 'Journal.2026-09-25T180148.01.log'
        self.reader = JournalIdentityReader(self.root)

    def tearDown(self):
        self.temp.cleanup()

    def write(self, records):
        """Write complete journal records to the active session file."""
        self.path.write_text(''.join(json.dumps(record) + '\n' for record in records), encoding='utf-8')

    def test_location_extracts_system_and_body(self):
        self.write([dict(event='Location', StarSystem='Kappa', Body='Kappa 2 a')])
        self.assertEqual(self.reader.current_identity().system, 'Kappa')
        self.assertEqual(self.reader.current_identity().body, 'Kappa 2 a')

    def test_fsdjump_updates_system(self):
        self.write([dict(event='Location', StarSystem='Kappa', Body='Kappa 2 a')])
        self.reader.current_identity()
        with self.path.open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(dict(event='FSDJump', StarSystem='Sol', Body='Sol')) + '\n')
        identity = self.reader.current_identity()
        self.assertEqual((identity.system, identity.body), ('Sol', 'Sol'))

    def test_appended_events_are_consumed_incrementally(self):
        self.write([dict(event='Location', StarSystem='Kappa', Body='Kappa 2 a')])
        self.reader.current_identity()
        offset = self.reader._offset
        with self.path.open('a', encoding='utf-8') as stream:
            stream.write(json.dumps(dict(event='Location', StarSystem='Sol', Body='Earth')) + '\n')
        identity = self.reader.current_identity()
        self.assertGreater(self.reader._offset, offset)
        self.assertEqual((identity.system, identity.body), ('Sol', 'Earth'))

    def test_new_journal_session_resets_identity(self):
        self.write([dict(event='Location', StarSystem='Kappa', Body='Kappa 2 a')])
        self.reader.current_identity()
        # Journal selection orders timestamp, sequence, then mtime; this file is newer.
        new_path = self.root / 'Journal.2026-09-25T180149.01.log'
        new_path.write_text('', encoding='utf-8')
        self.assertIsNone(self.reader.current_identity())

    def test_missing_directory_and_malformed_records_are_safe(self):
        reader = JournalIdentityReader(self.root / 'missing')
        self.assertIsNone(reader.current_identity())
        self.path.write_text('{broken\n' + json.dumps(dict(event='Other')) + '\n', encoding='utf-8')
        self.assertIsNone(self.reader.current_identity())

    def test_incomplete_final_record_is_retried(self):
        # The game can leave a final line unterminated until the next write flushes it.
        self.path.write_text('{"event":"Location","StarSystem":"Kappa"', encoding='utf-8')
        self.assertIsNone(self.reader.current_identity())
        with self.path.open('a', encoding='utf-8') as stream:
            stream.write(',"Body":"Kappa 2 a"}\n')
        identity = self.reader.current_identity()
        self.assertEqual((identity.system, identity.body), ('Kappa', 'Kappa 2 a'))
