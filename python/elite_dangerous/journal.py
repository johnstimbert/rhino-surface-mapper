"""Incrementally read Elite Dangerous Journal identity records.

This Qt-free telemetry module is part of the Elite Dangerous domain layer. It
tracks the active Journal file and extracts only the current system/body identity
needed by the rest of the application, leaving UI code independent from journal
file naming, offsets, and JSON-line recovery details.

Journal files are selected from names of the form
``Journal.YYYY-MM-DDTHHMMSS.sequence.log``. The newest candidate is chosen by
parsed timestamp, then numeric sequence, then file ``st_mtime_ns`` so renamed or
late-written files have deterministic precedence.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path


_JOURNAL_NAME = re.compile(
    r"Journal\.(?P<timestamp>\d{4}-\d{2}-\d{2}T\d{6})\.(?P<sequence>\d+)\.log$",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class JournalIdentity:
    """Immutable value object for the latest known Journal location identity.

``system`` is always a non-empty stripped star-system name. ``body`` is either a
non-empty body name or ``None`` when the Journal has not supplied one. The frozen
dataclass makes snapshots safe to share with UI and service code.
"""

    system: str
    body: str | None = None


class JournalIdentityReader:
    """Stateful incremental reader for the active Elite Dangerous Journal.

The reader owns the active path, byte offset, buffered trailing fragment, and
last identity seen in the current session. It reads only newly appended bytes on
each poll and updates identity exclusively from ``Location`` and ``FSDJump``
events, because those events authoritatively establish the current system.
"""

    def __init__(self, journal_directory: str | Path | None = None) -> None:
        # Default to Frontier's standard per-user Journal directory when omitted.
        self.journal_directory = Path(journal_directory) if journal_directory else (
            Path.home() / 'Saved Games' / 'Frontier Developments' / 'Elite Dangerous')
        self._active_path: Path | None = None
        # Byte offset enables cheap incremental reads of append-only Journal logs.
        self._offset = 0
        # Incomplete trailing JSON lines are retained until the writer finishes them.
        self._pending = b''
        self._identity: JournalIdentity | None = None

    def reset(self) -> None:
        """Discard the active path, byte offset, pending bytes, and identity.

Call this when the surrounding application intentionally starts a fresh Journal
session or wants to forget all previously observed location state. The method
raises no exceptions.
"""
        self._active_path = None
        self._offset = 0
        self._pending = b''
        self._identity = None

    def current_identity(self) -> JournalIdentity | None:
        """Consume newly appended Journal records and return the latest identity.

Returns:
    The most recent ``JournalIdentity`` observed in the active file, or ``None``
    when no authoritative identity has been read yet.

Behaviour:
    A newly selected file resets session state. A file that shrinks is treated as
    replacement/truncation and also resets state before reading from byte zero.
    Incomplete trailing JSON lines are buffered in ``_pending`` and retried with
    the next appended chunk instead of being parsed prematurely.
"""
        path = self._latest_journal()
        if path is None:
            return self._identity
        # A different latest Journal represents a new game/logging session.
        if path != self._active_path:
            self._active_path = path
            self._offset = 0
            self._pending = b''
            self._identity = None
        try:
            size = path.stat().st_size
            # Shrinkage means rotation, replacement, or truncation invalidated the offset.
            if size < self._offset:
                self._offset = 0
                self._pending = b''
                self._identity = None
            with path.open('rb') as journal:
                # Resume exactly at the last byte position consumed from this file.
                journal.seek(self._offset)
                chunk = journal.read()
        except OSError:
            return self._identity
        if not chunk:
            return self._identity

        data = self._pending + chunk
        lines = data.splitlines(keepends=True)
        complete = lines
        self._pending = b''
        # Journal records are newline-delimited JSON; a trailing fragment may be mid-write.
        if lines and not lines[-1].endswith((b'\n', b'\r')):
            complete = lines[:-1]
            self._pending = lines[-1]
        # The file position advances over both complete lines and the retained
        # incomplete fragment; the fragment is reattached to the next read.
        self._offset += len(chunk)
        for line in complete:
            self._consume_line(line)
        return self._identity

    def _latest_journal(self) -> Path | None:
        """Return the newest Journal file by timestamp, sequence, then mtime.

        Only filenames matching ``Journal.YYYY-MM-DDTHHMMSS.sequence.log`` are
        eligible. Malformed names, unreadable directories, invalid parsed
        timestamps, and stat failures are treated as absence of a usable Journal
        and return ``None``.
        """
        try:
            candidates = []
            # The filename timestamp is the primary game-session ordering signal.
            for path in self.journal_directory.glob('Journal.*.log'):
                match = _JOURNAL_NAME.fullmatch(path.name)
                if match:
                    candidates.append((
                        datetime.strptime(match['timestamp'], '%Y-%m-%dT%H%M%S'),
                        int(match['sequence']), path.stat().st_mtime_ns, path))
            # Tie-break by sequence, then mtime, matching Frontier's file naming contract.
            return max(candidates, key=lambda item: item[:3])[3] if candidates else None
        except (OSError, ValueError):
            return None

    def _consume_line(self, raw_line: bytes) -> None:
        """Parse one complete Journal JSON line and update identity if relevant.

        Invalid UTF-8, malformed JSON, non-object records, unrelated events, and
        records without a usable ``StarSystem`` are ignored. Only ``Location``
        and ``FSDJump`` change identity; other events may mention bodies or
        systems without proving the commander's current location.
        """
        try:
            record = json.loads(raw_line.decode('utf-8'))
        except (UnicodeDecodeError, json.JSONDecodeError, TypeError):
            return
        if not isinstance(record, dict):
            return
        event = record.get('event')
        # These are the authoritative events for the commander's current system.
        if event not in ('Location', 'FSDJump'):
            return
        system = record.get('StarSystem')
        if not isinstance(system, str) or not system.strip():
            return
        body = record.get('Body')
        if not isinstance(body, str) or not body.strip():
            # Preserve the previous body when the authoritative event omits one.
            body = self._identity.body if self._identity else None
        self._identity = JournalIdentity(system=system.strip(), body=body)
