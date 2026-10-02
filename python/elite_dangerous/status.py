"""Read Elite Dangerous ``Status.json`` and detect live game processes.

This Qt-free telemetry module belongs to the Elite Dangerous domain layer. It
isolates low-level operating-system process enumeration and file change detection
from UI code and higher-level services.

The process check observes the Windows process table rather than installation
files, because the caller needs to know whether the game executable is currently
running. Status reads use the file system's ``st_mtime_ns`` value as the sole
change token; partial writes are intentionally not retried here so callers can
handle ``json.JSONDecodeError`` according to their polling policy.
"""

from __future__ import annotations

import ctypes
import json
import os
from ctypes import wintypes
from os import PathLike
from pathlib import Path
from typing import Any


ELITE_DANGEROUS_PROCESS = "EliteDangerous64.exe"
TH32CS_SNAPPROCESS = 0x00000002


class _ProcessEntry32W(ctypes.Structure):
    """Win32 ``PROCESSENTRY32W`` layout used by Tool Help enumeration.

The structure stores one process snapshot record. ``dwSize`` must be populated
before ``Process32FirstW`` so Windows knows which layout version is being read,
and ``szExeFile`` contains the executable basename compared by this module.
"""

    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", wintypes.LONG),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", wintypes.WCHAR * 260),
    ]


def _windows_process_names() -> tuple[str, ...]:
    """Return executable basenames from the Windows process snapshot API.

Returns:
    A tuple of process executable names, or an empty tuple on non-Windows systems
    or when a snapshot cannot be opened.

The function raises no process-enumeration errors. It uses
``CreateToolhelp32Snapshot`` with ``Process32FirstW``/``Process32NextW`` to read
the live process table; this is more accurate for game-running detection than
checking whether game files exist on disk.
"""
    if os.name != "nt":
        return ()
    # Tool Help is the stable Win32 API for enumerating live process names.
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
    kernel32.Process32FirstW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_ProcessEntry32W)]
    kernel32.Process32FirstW.restype = wintypes.BOOL
    kernel32.Process32NextW.argtypes = [wintypes.HANDLE, ctypes.POINTER(_ProcessEntry32W)]
    kernel32.Process32NextW.restype = wintypes.BOOL
    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
    kernel32.CloseHandle.restype = wintypes.BOOL

    # The snapshot is point-in-time; callers poll again to observe later changes.
    snapshot = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snapshot == wintypes.HANDLE(-1).value:
        return ()
    names = []
    try:
        entry = _ProcessEntry32W()
        entry.dwSize = ctypes.sizeof(entry)
        if kernel32.Process32FirstW(snapshot, ctypes.byref(entry)):
            while True:
                names.append(entry.szExeFile)
                if not kernel32.Process32NextW(snapshot, ctypes.byref(entry)):
                    break
    finally:
        kernel32.CloseHandle(snapshot)
    return tuple(names)


def elite_dangerous_is_running() -> bool:
    """Return whether the exact Elite Dangerous game process is running.

Returns:
    ``True`` only when a live process basename matches
    ``EliteDangerous64.exe`` case-insensitively; otherwise ``False``.

The comparison intentionally requires the exact executable name after
``casefold`` normalization so similarly named launchers, installers, or helper
processes do not count as the running game.
"""
    # Casefold both sides for exact-name matching independent of Windows casing.
    expected = ELITE_DANGEROUS_PROCESS.casefold()
    return any(name.casefold() == expected for name in _windows_process_names())


def read_status_if_changed(
    path: str | PathLike[str],
    previous_mtime_ns: int | None,
    *,
    force: bool = False,
) -> tuple[int, Any] | None:
    """Read ``Status.json`` when its modification timestamp changed.

Args:
    path: File-system path to the current ``Status.json`` document.
    previous_mtime_ns: Previously observed ``st_mtime_ns`` token, or ``None``
        when no version has been read yet.
    force: When ``True``, read even if the timestamp token did not change.

Returns:
    ``None`` when the timestamp is unchanged and ``force`` is false; otherwise a
    ``(mtime_ns, payload)`` pair where ``payload`` is the decoded JSON document.

Raises:
    OSError: If the file metadata or contents cannot be read.
    json.JSONDecodeError: If the producer is mid-write or the file is invalid
        JSON. This module deliberately does not retry partial writes.
"""
    status_path = Path(path)
    # ``st_mtime_ns`` is the complete change-detection contract for callers.
    mtime_ns = status_path.stat().st_mtime_ns
    if not force and mtime_ns == previous_mtime_ns:
        return None
    # Do not mask partial-write JSON failures; polling callers decide retry timing.
    payload = json.loads(status_path.read_text(encoding="utf-8"))
    return mtime_ns, payload
