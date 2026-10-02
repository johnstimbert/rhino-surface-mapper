# GitHub Copilot — Rhino Surface Mapper

Rhino Surface Mapper is a portable Windows PySide6 desktop companion for *Elite Dangerous*
Surface Mining: it reads game telemetry, draws and persists planetary maps, and renders a
navigation overlay.

`AGENTS.md` (repository root) holds the binding architectural and engineering rules.
Read it first; on any conflict, `AGENTS.md` wins. This file adds repository-specific
build, test, and architectural context.

## Working rules (summary of `AGENTS.md`)

- Analyse the existing structure and reuse existing modules, functions, and patterns before adding new ones.
- Keep business logic, UI, and persistence/I/O separated; keep modules small and single-purpose.
- Preserve existing behaviour; make only the changes the task requires — no opportunistic renames or reorganisations.
- Catch specific exceptions; never `except: pass`.
- English for all source comments, docstrings, and new technical identifiers (much legacy code still has Portuguese docstrings — translate only what you touch, never repository-wide).
- Create or update the corresponding pytest tests whenever a module is added or relevant logic changes.
- PySide6 is the canonical binding. `MapPreview`, `MapTree`, and `MapRowDelegate` are intentionally programmatic; do not mass-convert Python UI into `.ui` files.

## Repository layout

- `python/` — the original PySide6 desktop application (source, `tests/`, `ui/`, `assets/`, `translations/`, `RhinoSurfaceMapper.spec`). It remains the reference implementation and the shipped Beta.
- `src/`, `tests/` — the in-progress .NET port (Clean Architecture + Blazor Hybrid on WPF). See `docs/design/`.
- `AGENTS.md` applies to both; the rules below describe the Python application unless stated otherwise.

## Build, test, and tooling (Python application)

There is no `pyproject.toml`, no configured linter, and no `conftest.py`. `python/` is the
import root — run everything from there (modules import each other flatly, e.g.
`from mapper_core import MapperState`).

```powershell
cd python
.venv\Scripts\python.exe -m pip install -r requirements-dev.txt   # PySide6, requests, pytest, pyinstaller
.venv\Scripts\python.exe -m pytest tests -ra                      # full suite
.venv\Scripts\python.exe -m pytest tests\test_mapper_core.py -ra  # single file
.venv\Scripts\python.exe -m pytest tests\test_mapper_core.py::MapperCoreTests::test_name -v   # single test
```

Tests are `unittest.TestCase` classes executed through pytest. Qt tests set
`os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')` **before** importing PySide6 and
reuse `QApplication.instance() or QApplication([])` in `setUpClass`; follow that pattern in
new Qt tests.

Packaging and translations:

```powershell
cd python
.venv\Scripts\python.exe -m PyInstaller RhinoSurfaceMapper.spec   # windowed, one-folder build
pyside6-lrelease translations\rsm_pt_PT.ts -qm translations\rsm_pt_PT.qm
```

`.qm` files are gitignored and must be compiled from `python/translations/rsm_pt_PT.ts`. Any new
bundled data file (asset, `.ui`, catalogue, JSON) must also be added to the `datas` list in
`python/RhinoSurfaceMapper.spec`, or it will be missing in the frozen build.

## Architecture (Python application)

Three independent layers, split by Qt dependency (all paths below are under `python/`):

- **Qt-free domain and I/O** — `mapper_core.py` (`MapperState`: telemetry acceptance, map
  points, radar coverage, deposits/rigs/marks, search routes, navigation), `map_persistence.py`
  (atomic JSON read/write, favourite/protected flags), `map_pml.py` (PML discovery and naming
  rules), `settings_persistence.py`, `app_paths.py`, `radar.py`, `steering.py`, `turn_trial.py`.
  These import no PySide6 and are the preferred place for new rules.
- **Elite Dangerous telemetry** — `elite_dangerous/status.py` (`Status.json` reading and change
  detection), `elite_dangerous/journal.py` (incremental Journal reader; authoritative system
  identity), and `elite_dangerous/market/` (a self-contained, Qt-free market subpackage with
  frozen dataclass models, Spansh/INARA acquisition, policy filtering, ranking, and JSON cache;
  it backs the planned Powerplay feature and is not yet wired into the UI).
- **Qt presentation** — `rhino_surface_mapper.py` (entry point only) → `rhino_surface_mapper_qt.py`
  (`MapperWindow`, map view, timers), `qt_map_operations.py` (map/deposit/rig dialogs and
  operations), `map_library.py` (`MapLibraryWindow` + preview/tree/delegate), `layout_options.py`
  (preferences panel), `pyqt_overlay.py` (transparent navigation overlay), `steering_ui.py`,
  plus small shared widgets/painters (`deposit_marker.py`, `map_badges.py`, `numeric_fields.py`).
  Windows input reading lives in `radar_input.py` and `steering_input.py` (passive observation;
  `steering_input.py` is the only module that injects keystrokes).

Data flow: `Status.json`/Journal → `elite_dangerous` readers → `MapperState` (decides whether a
status is accepted and whether location changed) → window refresh → painters and overlay.

## Repository conventions

- **Runtime paths are app-local (portable app).** `app_paths.maps_directory()` returns `MAPAS/`
  and `rhino_surface_mapper_qt.OPTIONS_PATH` resolves `options.json`, both beside
  `sys.executable` when `getattr(sys, 'frozen', False)` else beside the module. Bundled
  read-only resources resolve through `sys._MEIPASS` when frozen (see `i18n.catalogue_path`).
  Never hardcode user-profile or installation paths.
- **Map files are JSON** in `MAPAS/`, named `<Body> [<PML id>].json` via
  `qt_map_operations.pml_filename` / `safe_filename_component`. Writes go through
  `map_persistence` (temp file + `os.replace`); flag updates preserve the original mtime.
  Unknown/legacy maps missing optional metadata must remain loadable.
- **`.ui` loading uses a loader that returns the existing widget as the Designer root**
  (`_DepositDialogLoader`, `_MapLibraryLoader` override `createWidget` for the named root),
  then resolves children with `findChild` and raises `RuntimeError` when a contract widget is
  missing. Object names in `ui/*.ui` are runtime contracts — preserve them and keep the tests
  that assert them.
- **Translation contract**: Python UI strings go through `i18n.translate(context, text)` with the
  class name as context; `.ui` strings carry their own context. Source language is `en_GB`;
  `pt_PT` is the only catalogue. Every new user-visible string needs a matching
  `translations/rsm_pt_PT.ts` entry, and `tests/test_*_i18n.py` assert context/source-string
  coverage against both the `.ui` files and the `.ts` catalogue.
- **Domain models are immutable** where practical: `@dataclass(frozen=True)` for results such as
  `StatusUpdate` and everything in `elite_dangerous/market/models.py`.
- **Safety framing**: the app does not detect obstacles and steering assistance only observes
  input; keep that wording and the conservative behaviour in any related change.
- Commit messages are short imperative subjects, e.g. "Add Journal system identity support".
