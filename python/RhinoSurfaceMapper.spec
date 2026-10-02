# -*- mode: python ; coding: utf-8 -*-

import fnmatch


def is_top_level_ucrt_binary(entry):
    """Return whether a binary is an app-local UCRT file excluded by policy."""
    destination = str(entry[0]).replace("/", "\\")
    if "\\" in destination:
        return False
    patterns = ("ucrtbase.dll", "api-ms-win-core-*.dll", "api-ms-win-crt-*.dll")
    return any(fnmatch.fnmatchcase(destination.casefold(), pattern) for pattern in patterns)


a = Analysis(
    ['rhino_surface_mapper.py'],
    pathex=[],
    binaries=[],
    datas=[('assets', 'assets'), ('ui', 'ui'), ('translations/rsm_pt_PT.qm', 'translations'), ('elite_dangerous/market/SURFACE_MINING_COMMODITIES.json', 'elite_dangerous/market')],
    hiddenimports=[],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
# Beta 1 targets Windows 10/11 x64, where UCRT is an OS component; this prevents environment-dependent collection.
a.binaries = [entry for entry in a.binaries if not is_top_level_ucrt_binary(entry)]
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name='RhinoSurfaceMapper',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
)
coll = COLLECT(
    exe,
    a.binaries,
    a.datas,
    strip=False,
    upx=True,
    upx_exclude=[],
    name='RhinoSurfaceMapper',
)
