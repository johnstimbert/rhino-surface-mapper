"""Qt dialogs and user-facing map operations for Rhino Surface Mapper.

The mixin in this module coordinates modal dialogs, save/open confirmation
flows, marker editing, and active-map installation for ``MapperWindow``. It is
part of the Qt presentation layer; business rules for map identity, PML matching,
filename/version generation, and persistence are delegated to Qt-free core
modules such as ``mapper_core``, ``map_pml``, and ``map_persistence``.
"""
import math
import re
import time
from datetime import datetime
from pathlib import Path
from PySide6.QtCore import QFile, QPointF, Qt
from PySide6.QtUiTools import QUiLoader
from PySide6.QtWidgets import (QDialog, QDialogButtonBox, QFormLayout, QLineEdit,
    QComboBox, QSpinBox, QFileDialog, QMessageBox, QMenu, QApplication, QDoubleSpinBox,
    QInputDialog)
from app_paths import maps_directory
from mapper_core import MapperState
from map_pml import (PML_MATCH_DISTANCE_M, infer_legacy_pml, matching_candidates,
                     newest_by_pml, next_john_doe_id, next_version_path,
                     pml_filename, pml_path as canonical_pml_path,
                     safe_filename_component, surface_distance as pml_surface_distance,
                     corresponds_to_map)
from numeric_fields import MetresSpinBox, DegreesSpinBox, compact
from i18n import translate


class _DepositDialogLoader(QUiLoader):
    """Custom ``QUiLoader`` that binds Designer UI to an existing dialog.

    The loader returns the already constructed ``DepositDialog`` for the Designer
    root object, then lets Qt create child widgets normally. Object names in the
    ``.ui`` file are runtime contracts resolved later with ``findChild``.
    """

    def __init__(self, dialog):
        """Store the dialog instance that will act as the Designer root."""
        super().__init__(dialog)
        self.dialog = dialog

    def createWidget(self, class_name, parent=None, name=''):
        """Return the existing dialog for the root and create children normally.

        Missing or renamed child object names are detected by ``DepositDialog``
        after the load and reported as runtime contract errors.
        """
        if class_name == 'QDialog' and name == 'DepositDialog':
            return self.dialog
        return super().createWidget(class_name, parent, name)

# PML auto-recognition threshold used to decide whether New starts exploration
# in another area instead of the current PML.
# Within 13 km of a PML centre, the UI can auto-recognize the area.
PML_MATCH_DISTANCE_M = 13_000

def safe_filename_component(value):
    """Return a Windows-safe path component while preserving readability.

    Only characters invalid on Windows filesystems, control characters, trailing
    spaces, and trailing periods are replaced or trimmed. Empty results become a
    stable fallback name.
    """
    cleaned = re.sub(r'[<>:"/\\|?*\x00-\x1f]', '_', str(value)).strip().rstrip('. ')
    return cleaned or 'Sem nome'


def pml_filename(state):
    """Return the canonical filename for a state's PML map."""
    return f'{safe_filename_component(state.body)} [{safe_filename_component(state.pml_id)}].json'



class DepositDialog(QDialog):
    """Modal editor for deposit name, size, and rig count.

    The dialog owns only its Qt controls. It loads static layout from Designer,
    validates required object-name contracts, and returns values without mutating
    the map directly.
    """
    def __init__(self, parent, existing=None):
        """Load the deposit editor UI and populate controls.

        ``parent`` owns the modal dialog. ``existing`` optionally supplies current
        values for edit mode. Raises ``OSError`` when the UI file cannot be opened
        and ``RuntimeError`` when required Designer widgets are missing.
        """
        super().__init__(parent)
        ui_path = Path(__file__).resolve().parent / 'ui' / 'deposit_dialog.ui'
        ui_file = QFile(str(ui_path))
        if not ui_file.open(QFile.OpenModeFlag.ReadOnly):
            raise OSError(f'Unable to open UI resource: {ui_path}')
        # The custom loader returns this dialog as the Designer root; required
        # child object names below are runtime contracts.
        loaded = _DepositDialogLoader(self).load(ui_file, self)
        ui_file.close()
        if loaded is None or loaded.layout() is None:
            raise RuntimeError(f'Unable to load UI resource: {ui_path}')
        self.setWindowTitle(translate(
            'DepositDialog', 'Edit deposit' if existing else 'Mark deposit'))
        data = existing or {}
        self.name = self.findChild(QLineEdit, 'name')
        self.size = self.findChild(QComboBox, 'size')
        self.rigs = self.findChild(QSpinBox, 'rigs')
        buttons = self.findChild(QDialogButtonBox, 'buttonBox')
        if not all((self.name, self.size, self.rigs, buttons)):
            raise RuntimeError('DepositDialog.ui is missing a required widget')
        self.name.setText(data.get('name', ''))
        size_values = ('Pequeno', 'Médio', 'Grande', 'Enorme')
        size_labels = ('Small', 'Medium', 'Large', 'Huge')
        for value, label in zip(size_values, size_labels):
            self.size.addItem(translate('DepositDialog', label), userData=value)
        size = data.get('size', 'Pequeno')
        index = self.size.findData(size)
        self.size.setCurrentIndex(index if index >= 0 else 0)
        self.rigs.setSuffix(f" {translate('DepositDialog', 'rigs')}")
        self.rigs.setRange(1,6)
        self.rigs.setValue(int(data.get('rigs',1)))
        compact(self.rigs)
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)

    def values(self):
        """Return accepted deposit fields as a dictionary.

        The name falls back to the localized deposit label when blank. Size and
        rig count are constrained by the dialog controls.
        """
        return dict(name=self.name.text().strip() or 'Depósito', size=self.size.currentData(), rigs=self.rigs.value())


class MarkDialog(QDialog):
    """Modal editor for a named bearing-and-distance marker.

    The dialog captures user input relative to the Rhino position chosen by the
    caller. It preserves original values when rounded controls are left unchanged.
    """
    def __init__(self, parent, existing=None):
        """Create marker editing controls and populate optional existing values.

        ``existing`` enables edit mode and records original azimuth/distance so
        unchanged rounded fields do not move a marker accidentally.
        """
        super().__init__(parent)
        self.setWindowTitle(translate(
            'MarkDialog', 'Edit marker' if existing else 'Marker'))
        form = QFormLayout(self)
        form.setLabelAlignment(Qt.AlignmentFlag.AlignRight | Qt.AlignmentFlag.AlignVCenter)
        form.setHorizontalSpacing(4)
        self.name = QLineEdit()
        self.name.setMaxLength(120)
        self.azimuth = DegreesSpinBox()
        self.azimuth.setToolTip(translate(
            'MarkDialog', '0° North · 90° East · 180° South · 270° West'))
        self.distance = MetresSpinBox()
        form.addRow(translate('MarkDialog', 'Marker name:'), self.name)
        form.addRow(translate('MarkDialog', 'Bearing from north:'), self.azimuth)
        form.addRow(translate('MarkDialog', 'Distance from Rhino:'), self.distance)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Ok | QDialogButtonBox.StandardButton.Cancel)
        buttons.button(QDialogButtonBox.StandardButton.Ok).setEnabled(False)
        self.name.textChanged.connect(lambda text: buttons.button(QDialogButtonBox.StandardButton.Ok).setEnabled(bool(text.strip())))
        buttons.accepted.connect(self.accept)
        buttons.rejected.connect(self.reject)
        form.addRow(buttons)
        if existing:
            self.name.setText(existing['name'])
            self.azimuth.setValue(round(existing['azimuth']) % 360)
            self.distance.setValue(round(existing['distance']))
        # Integer formatting is presentation only; preserve old values when
        # unchanged fields would otherwise move markers through rounding.
        self.original_values = dict(existing) if existing else None
        self.initial_azimuth = self.azimuth.value()
        self.initial_distance = self.distance.value()
        if existing and existing['distance'] > self.distance.maximum():
            self.distance.setToolTip(translate(
                'MarkDialog',
                'Original distance exceeds 99,999 m. It will be preserved unless this field is changed.'))

    def values(self):
        """Return the accepted marker name, azimuth, and distance."""
        azimuth, distance = self.azimuth.value(), self.distance.value()
        if self.original_values:
            if azimuth == self.initial_azimuth:
                azimuth = self.original_values['azimuth']
            if distance == self.initial_distance:
                distance = self.original_values['distance']
        return dict(name=self.name.text().strip(), azimuth=azimuth, distance=distance)


class MapOperations:
    """Mixin that adds map, PML, marker, and navigation operations to the window.

    The mixin expects ``MapperWindow`` attributes such as ``state``, ``view``,
    ``overlay``, timers, and status labels. It coordinates Qt dialogs and
    delegates map identity, distance matching, persistence, and version naming to
    core modules.
    """

    @staticmethod
    def surface_distance(radius, lat_a, lon_a, lat_b, lon_b):
        """Return great-circle surface distance in metres."""
        return pml_surface_distance(radius, lat_a, lon_a, lat_b, lon_b)
        # Unreachable: legacy inline implementation retained after the calculation
        # moved to map_pml.surface_distance. Kept verbatim for reference only.
        """Great-circle distance between two positions, in metres."""
        dlat = math.radians(lat_b-lat_a)
        dlon = math.radians(lon_b-lon_a)
        a = (math.sin(dlat/2)**2 + math.cos(math.radians(lat_a))
             * math.cos(math.radians(lat_b)) * math.sin(dlon/2)**2)
        return 2*radius*math.asin(min(1, math.sqrt(a)))

    def pml_path(self, state=None):
        """Return the canonical PML path for ``state`` or the active state."""
        state = state or self.state
        return canonical_pml_path(maps_directory(), state)

    def choose_list_item(self, title, label, items):
        """Display a modal list selector and return the selected index.

        Returns ``None`` when the user cancels. The selection style is set
        explicitly so highlighting stays readable in light and dark themes.
        """
        dialog = QInputDialog(self)
        dialog.setWindowTitle(title)
        dialog.setLabelText(label)
        dialog.setComboBoxItems(items)
        dialog.setComboBoxEditable(False)
        combo = dialog.findChild(QComboBox)
        if combo is not None:
            combo.view().setStyleSheet(
                'QListView::item:selected { background: #2478c4; color: white; '
                'border: 1px solid #9fd4ff; }')
        if dialog.exec() != QDialog.DialogCode.Accepted:
            return None
        return combo.currentIndex() if combo is not None else None

    @staticmethod
    def infer_legacy_pml(state, path, system, body):
        """Fill missing PML metadata on legacy loaded maps.

        The implementation delegates to the Qt-free PML helper. Legacy centre
        markers and file names identify older maps without writing changes until
        the user saves.
        """
        infer_legacy_pml(state, path, system, body)
        return
        # Unreachable: legacy inline implementation retained after the recovery rules
        # moved to map_pml.infer_legacy_pml. Kept verbatim for reference only.
        """Fill metadata that older map files did not save.

        The ``Center [n]`` marker created by the previous version is a reliable
        source for recovering the identifier and centre without touching the file
        until the user chooses to save it again.
        """
        if not state.system and path.parent.name.casefold() == safe_filename_component(system).casefold():
            state.system = system
        # The two oldest maps lack PML metadata. In them, the filename is the
        # most specific source: "Kappa 2 [20]" and "Kappa 2 a [6]" are different
        # bodies, even if an old JSON accidentally retained the previous body
        # name.
        legacy_name = re.fullmatch(r'(.+?)\s+\[[^\]]+\](?:\s+v\d+)?', path.stem,
                                   re.IGNORECASE)
        if (not state.pml_id and state.pml_center_lat is None and legacy_name):
            state.body = legacy_name.group(1).strip()
        elif not state.body:
            state.body = body
        for mark in state.marks:
            match = re.fullmatch(r'Centro\s*\[([^\]]+)\]', str(mark.get('name', '')).strip(), re.IGNORECASE)
            if match and 'lat' in mark and 'lon' in mark:
                state.pml_id = state.pml_id or match.group(1).strip()
                state.pml_center_lat = state.pml_center_lat if state.pml_center_lat is not None else mark['lat']
                state.pml_center_lon = state.pml_center_lon if state.pml_center_lon is not None else mark['lon']
                break
        state.body_key = f'{state.system}|{state.body}'

    def nearby_pml_maps(self, system, body, lat, lon):
        """Return nearby matching PML maps for a known system and body.

        PML auto-recognition uses the 13,000 m threshold from
        ``PML_MATCH_DISTANCE_M``. Invalid or incomplete files are ignored so
        entering the SRV remains robust.
        """
        directory = maps_directory() / safe_filename_component(system)
        return matching_candidates(directory.glob('*.json') if directory.is_dir() else (),
                                   system, body, lat, lon, self._load_candidate)
        if not directory.is_dir():
            return []
        matches = []
        for path in directory.glob('*.json'):
            try:
                candidate = MapperState()
                candidate.load(path)
                self.infer_legacy_pml(candidate, path, system, body)
                if (candidate.system.casefold() != system.casefold()
                        or candidate.body.casefold() != body.casefold()
                        or candidate.pml_center_lat is None):
                    continue
                distance = self.surface_distance(candidate.radius, lat, lon,
                                                 candidate.pml_center_lat,
                                                 candidate.pml_center_lon)
                if distance < PML_MATCH_DISTANCE_M:
                    matches.append((distance, path, candidate))
            except (OSError, ValueError, TypeError, KeyError, AttributeError):
                continue
        return sorted(matches, key=lambda item: item[0])

    def nearby_pml_maps_without_system(self, body, lat, lon):
        """Search existing system folders when telemetry omits ``StarSystem``.

        No new PML is created in this mode. A candidate must still match body and
        PML centre before its saved system is trusted.
        """
        matches = []
        for directory in maps_directory().iterdir():
            if directory.is_dir():
                matches.extend(self.nearby_pml_maps(directory.name, body, lat, lon))
        return sorted(matches, key=lambda item: item[0])

    def next_john_doe_id(self, system, body):
        """Return the next temporary JD identifier for the current body."""
        directory = maps_directory() / safe_filename_component(system)
        return next_john_doe_id(directory.glob('*.json') if directory.is_dir() else (),
                                 system, body, self._load_candidate)

    @staticmethod
    def _load_candidate(path):
        """Load and return a detached ``MapperState`` candidate from ``path``."""
        candidate = MapperState()
        candidate.load(path)
        return candidate

    def _legacy_next_john_doe_id(self, system, body):
        """Compute the next legacy JD id by scanning existing map files."""
        highest = 0
        directory = maps_directory() / safe_filename_component(system)
        if directory.is_dir():
            for path in directory.glob('*.json'):
                try:
                    candidate = MapperState()
                    candidate.load(path)
                    self.infer_legacy_pml(candidate, path, system, body)
                    if candidate.body.casefold() != body.casefold():
                        continue
                    match = re.fullmatch(r'JD(\d+)', candidate.pml_id.strip(), re.IGNORECASE)
                    if match:
                        highest = max(highest, int(match.group(1)))
                except (OSError, ValueError, TypeError, KeyError, AttributeError):
                    continue
        return f'JD{highest+1}'

    def prepare_loaded_map(self, candidate, source_path=None):
        """Prepare a loaded candidate without changing the active window state.

        Protected maps either open as mining-only read-only maps or become
        editable copies saved as new versions. Legacy timestamp migration is
        written back only when safe.
        """
        if candidate.protected:
            choice = self.choose_protected_map_mode()
            if choice is None:
                return None
            if choice == 'mining':
                candidate.enter_mining_mode()
            else:
                import copy
                candidate = copy.deepcopy(candidate)
                candidate.protected = False
                candidate.mining_only = False
                candidate.created_at = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
                candidate.last_saved_at = None
                source_path = self.write_new_version(candidate, source_path)
        if source_path:
            source_path = Path(source_path)
            if not candidate.read_only and candidate.populate_missing_timestamps(source_path):
                # One-time legacy migration keeps timestamps that came from file
                # properties before this write.
                candidate.save(source_path, update_saved_at=False)
        return candidate, Path(source_path) if source_path else None

    def install_prepared_map(self, candidate, source_text='Loaded map',
                             source_path=None, poll_after_install=True):
        """Atomically install a prepared map candidate with rollback.

        The method snapshots all active window and view state, installs the
        candidate, updates controls and optional telemetry, and restores the
        snapshot if any step raises. This prevents partial map switches from
        corrupting the UI.
        """
        self.cancel_placement()
        self.view.radar.waves.clear()
        self.stop_assistance()
        previous = {
            'state': self.state,
            'view_state': self.view.state,
            'current_map_path': self.current_map_path,
            'view_center': QPointF(self.view.center),
            'view_scale': self.view.scale,
            'overlay_mode_active': self.overlay_mode_active,
            'overlay_navigation_active': self.overlay_navigation_active,
            'info_left': self.info_left.text(),
            'last_mtime': self.last_mtime,
            'status_valid': self.status_valid,
            'live_status': dict(self.live_status),
            'parameters': {field: spin.value()
                           for field, spin in self.parameter_spins.items()},
        }

        try:
            self.state = candidate
            self.view.state = candidate
            # Store the file that was actually opened. Replace must write to this
            # path, including when it is an older version.
            self.current_map_path = Path(source_path) if source_path else None
            for field, spin in self.parameter_spins.items():
                spin.blockSignals(True)
                spin.setValue(int(getattr(candidate, field)))
                spin.blockSignals(False)
            points = ([] if candidate.mining_only else candidate.points) + candidate.deposits + candidate.rigs + candidate.marks
            if points:
                xs, ys = [p['x'] for p in points], [p['y'] for p in points]
                self.view.center = QPointF((min(xs)+max(xs))/2, (min(ys)+max(ys))/2)
                self.view.scale = max(0.001, min(10, min(
                    self.view.width()/max(4000, max(xs)-min(xs)+2000),
                    self.view.height()/max(4000, max(ys)-min(ys)+2000))))
            else:
                self.view.center = QPointF()
                self.view.scale = 0.08
            self.overlay_mode_active = False
            self.overlay_navigation_active = False
            self.info_left.setText(f'{candidate.system} — {candidate.body} | {source_text}')
            if poll_after_install:
                # Map JSON does not contain the current Rhino presence or
                # position. Re-read immediately even if Status.json has not
                # changed.
                self.last_mtime = None
                self.poll(reloading_map=True)
        except Exception:
            self.state = previous['state']
            self.view.state = previous['view_state']
            self.current_map_path = previous['current_map_path']
            self.view.center = previous['view_center']
            self.view.scale = previous['view_scale']
            self.overlay_mode_active = previous['overlay_mode_active']
            self.overlay_navigation_active = previous['overlay_navigation_active']
            self.last_mtime = previous['last_mtime']
            self.status_valid = previous['status_valid']
            self.live_status = previous['live_status']
            for field, value in previous['parameters'].items():
                spin = self.parameter_spins[field]
                spin.blockSignals(True)
                spin.setValue(value)
                spin.blockSignals(False)
            self.info_left.setText(previous['info_left'])
            raise
        return True

    def install_loaded_map(self, candidate, source_text='Loaded map', source_path=None):
        """Prepare and activate a loaded map candidate."""
        prepared = self.prepare_loaded_map(candidate, source_path)
        if prepared is None:
            return False
        candidate, source_path = prepared
        return self.install_prepared_map(candidate, source_text, source_path)

    def choose_protected_map_mode(self):
        """Ask how to open a protected map while pausing active timers.

        Timers are stopped during the modal dialog so background polling cannot
        record or mutate the map while the user decides between mining-only and
        editable copy.
        """
        timers = [getattr(self, name, None) for name in ('timer', 'radar_timer', 'assist_timer')]
        running = [(timer, timer.interval()) for timer in timers if timer is not None and timer.isActive()]
        for timer, _ in running:
            timer.stop()
        try:
            box = QMessageBox(self)
            box.setWindowTitle(translate('MapperWindow', 'Protected map'))
            box.setText(translate('MapperWindow', 'How would you like to use this protected map?'))
            box.setInformativeText(translate('MapperWindow', 'Continuing exploration creates a new editable version. Mining only lets you view points and navigate without recording changes.'))
            explore = box.addButton(translate('MapperWindow', 'Continue exploration'), QMessageBox.ButtonRole.AcceptRole)
            mining = box.addButton(translate('MapperWindow', 'Mining only'), QMessageBox.ButtonRole.ActionRole)
            box.addButton(translate('MapperWindow', 'Cancel'), QMessageBox.ButtonRole.RejectRole)
            box.setDefaultButton(mining)
            box.exec()
            return 'explore' if box.clickedButton() is explore else 'mining' if box.clickedButton() is mining else None
        finally:
            for timer, interval in running:
                timer.start(interval)

    def map_is_read_only(self):
        """Report whether editing is blocked and notify the user when it is."""
        if not self.state.read_only:
            return False
        QMessageBox.information(self, translate('MapperWindow', 'Read-only map'),
                                translate('MapperWindow', 'Open the map and choose Continue exploration to create a new editable version.'))
        return True

    def write_new_version(self, state, source_path=None):
        """Save ``state`` to the next free numbered version path.

        The method supports both canonical PML maps and older files that only have
        a source path. It raises ``ValueError`` when no destination can be derived.
        """
        canonical = self.pml_path(state)
        if canonical is None and source_path:
            canonical = Path(source_path)
            canonical = canonical.with_name(re.sub(r' v\d+$', '', canonical.stem) + '.json')
        if canonical is None:
            raise ValueError(translate('MapperWindow', 'The map has no file or identified PML yet.'))
        canonical.parent.mkdir(parents=True, exist_ok=True)
        destination = next_version_path(canonical, canonical.parent.iterdir())
        state.save(destination)
        return destination

    def setup_new_pml(self, state=None):
        """Prompt for PML identity and centre, then update the chosen state.

        The centre is calculated from the current Rhino position, bearing, and
        distance. The method adds the centre mark and returns ``False`` when the
        user cancels.
        """
        s = state or self.state
        prompt = translate('MapperWindow', 'PML number for this area. Leave blank if you do not know it yet.')
        pml_id, accepted = QInputDialog.getText(self, translate('MapperWindow', 'New PML'), prompt)
        if not accepted:
            return False
        pml_id = pml_id.strip()
        if not pml_id:
            if QMessageBox.question(
                    self, translate('MapperWindow', 'Unknown PML'),
                    translate('MapperWindow', 'No PML was provided. Create the following temporary identifier?')
            ) != QMessageBox.StandardButton.Yes:
                return False
            pml_id = self.next_john_doe_id(s.system, s.body)
        azimuth_dialog = QInputDialog(self)
        azimuth_dialog.setWindowTitle(
            translate('MapperWindow', 'PML centre [{pml_id}]').format(pml_id=pml_id))
        azimuth_dialog.setLabelText(
            translate('MapperWindow', 'Bearing from Rhino to the PML centre (0° = north):'))
        azimuth_dialog.setInputMode(QInputDialog.InputMode.IntInput)
        azimuth_dialog.setIntRange(0, 359)
        azimuth_dialog.setIntStep(1)
        azimuth_dialog.setIntValue(0)
        azimuth_spin = azimuth_dialog.findChild(QSpinBox)
        if azimuth_spin is not None:
            azimuth_spin.setWrapping(True)
        if azimuth_dialog.exec() != QDialog.DialogCode.Accepted:
            return False
        azimuth = azimuth_dialog.intValue()
        distance, accepted = QInputDialog.getDouble(
            self, translate('MapperWindow', 'PML centre [{pml_id}]').format(pml_id=pml_id),
            translate('MapperWindow', 'Distance from Rhino to the PML centre (m):'), 0, 0, 100000, 0)
        if not accepted:
            return False
        coordinates = self.mark_coordinates(s, s.rhino_lat, s.rhino_lon,
                                            {'azimuth': azimuth, 'distance': distance})
        s.pml_id = pml_id
        s.pml_center_lat, s.pml_center_lon = coordinates['lat'], coordinates['lon']
        s.created_at = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        s.last_saved_at = None
        s.marks.append(dict(name=f'Centro [{pml_id}]', **coordinates))
        self.statusBar().showMessage(
            translate('MapperWindow', 'PML [{pml_id}] created. Save the map to keep it.').format(pml_id=pml_id))
        return True

    def open_or_create_pml_for_current_position(self):
        """Open the matching nearby PML or guide creation of a new one.

        The method runs only with live body and SRV position data. It deduplicates
        versions by PML id, uses newest saves automatically when unambiguous, and
        avoids creating maps when the game omitted the system name.
        """
        s = self.state
        if not (s.body.strip() and s.rhino_lat is not None):
            return
        system_is_known = bool(s.system.strip())
        matches = (self.nearby_pml_maps(s.system, s.body, s.rhino_lat, s.rhino_lon)
                   if system_is_known else
                   self.nearby_pml_maps_without_system(s.body, s.rhino_lat, s.rhino_lon))
        # Multiple versions of one PML are not multiple PMLs. Open the newest
        # copy automatically while older versions remain available through Open
        # without interrupting normal SRV entry.
        newest_by_pml = {}
        for item in matches:
            pml_id = item[2].pml_id.casefold()
            if (pml_id not in newest_by_pml
                    or item[1].stat().st_mtime > newest_by_pml[pml_id][1].stat().st_mtime):
                newest_by_pml[pml_id] = item
        matches = list(newest_by_pml.values())
        if len(matches) == 1:
            _, path, candidate = matches[0]
            # Status.json sometimes omits StarSystem. For a map confirmed by
            # folder, body, and PML centre, use the saved system without inventing
            # or writing it back into telemetry.
            status = dict(self.live_status)
            status['StarSystem'] = candidate.system
            status['BodyName'] = candidate.body
            candidate.process_status(status)
            if not self.install_loaded_map(
                    candidate, translate('MapperWindow', 'PML [{pml_id}] opened').format(pml_id=candidate.pml_id), path):
                return
            self.statusBar().showMessage(
                translate('MapperWindow', 'Map opened: {filename}').format(filename=path.name))
            return
        if len(matches) > 1:
            # Here each entry is a distinct PML; save time helps pick the newest
            # copy when distances are close.
            matches.sort(key=lambda item: item[1].stat().st_mtime, reverse=True)
            labels = [translate(
                'MapperWindow', '[{pml_id}] — saved {timestamp} — {distance:.1f} km').format(
                    pml_id=candidate.pml_id,
                    timestamp=datetime.fromtimestamp(path.stat().st_mtime).strftime('%Y-%m-%d %H:%M'),
                    distance=distance / 1000)
                      for distance, path, candidate in matches]
            chosen = self.choose_list_item(
                translate('MapperWindow', 'Several nearby PMLs'),
                translate('MapperWindow', 'Choose the PML:'), labels)
            if chosen is None:
                return
            index = chosen
            _, path, candidate = matches[index]
            status = dict(self.live_status)
            status['StarSystem'] = candidate.system
            status['BodyName'] = candidate.body
            candidate.process_status(status)
            if not self.install_loaded_map(
                    candidate, translate('MapperWindow', 'PML [{pml_id}] opened').format(pml_id=candidate.pml_id), path):
                return
            self.statusBar().showMessage(
                translate('MapperWindow', 'Map opened: {filename}').format(filename=path.name))
            return
        if system_is_known:
            if self.setup_new_pml():
                # The first map for a PML always uses the primary name without a
                # version suffix and immediately becomes the current map.
                path = self.pml_path()
                try:
                    path.parent.mkdir(parents=True, exist_ok=True)
                    self.state.save(path)
                    self.current_map_path = path
                    self.statusBar().showMessage(translate(
                        'MapperWindow', 'PML [{pml_id}] created: {filename}').format(
                            pml_id=self.state.pml_id, filename=path.name))
                except OSError as exc:
                    QMessageBox.critical(self, translate('MapperWindow', 'Error creating PML'), str(exc))
        else:
            self.statusBar().showMessage(translate(
                'MapperWindow', 'The game did not provide a system; a new PML cannot be created.'))

    def save_new_pml_version(self):
        """Save the active PML as an additional numbered version.

        The save timestamp is shown in lists and used for ordering. The name keeps
        only the version suffix so Replace is not confused with renaming.
        """
        if self.state.read_only:
            raise PermissionError(translate(
                'MapperWindow', 'Open the map and choose Continue exploration.'))
        canonical = self.pml_path()
        if canonical is None:
            raise ValueError(translate(
                'MapperWindow', 'The map has no system, body, or identified PML yet.'))
        canonical.parent.mkdir(parents=True, exist_ok=True)
        highest = 1
        expression = re.compile(rf'^{re.escape(canonical.stem)} v(\d+)\.json$', re.IGNORECASE)
        # Do not use Path.glob with canonical.stem: PML square brackets would be
        # interpreted as a pattern and would keep recreating v2.
        for path in canonical.parent.iterdir():
            if not path.is_file():
                continue
            match = expression.fullmatch(path.name)
            if match:
                highest = max(highest, int(match.group(1)))
        candidate = canonical.with_name(f'{canonical.stem} v{highest+1}.json')
        self.state.save(candidate)
        self.current_map_path = candidate
        return candidate

    def confirm_pml_exit(self):
        """Run the exit save/discard/cancel confirmation flow.

        Protected or unidentified maps keep legacy behaviour. Editable PML maps
        can be replaced, saved as a new version, discarded, or left open by
        cancelling close.
        """
        if self.state.read_only or self.pml_path() is None:
            return True
        message = QMessageBox(self)
        message.setWindowTitle(translate('MapperWindow', 'Save PML map'))
        message.setText(translate(
            'MapperWindow', 'PML [{pml_id}]: how would you like to save before exiting?').format(
                pml_id=self.state.pml_id))
        new_version = message.addButton(translate('MapperWindow', 'Save new version'), QMessageBox.ButtonRole.ActionRole)
        replace = message.addButton(translate('MapperWindow', 'Replace'), QMessageBox.ButtonRole.AcceptRole)
        discard = message.addButton(translate('MapperWindow', 'Exit without saving'), QMessageBox.ButtonRole.DestructiveRole)
        cancel = message.addButton(translate('MapperWindow', 'Cancel'), QMessageBox.ButtonRole.RejectRole)
        message.setDefaultButton(replace)
        message.exec()
        try:
            if message.clickedButton() is cancel:
                return False
            if message.clickedButton() is discard:
                return True
            if message.clickedButton() is new_version:
                path = self.save_new_pml_version()
            else:
                path = self.current_map_path or self.pml_path()
                path.parent.mkdir(parents=True, exist_ok=True)
                self.state.save(path)
            self.statusBar().showMessage(translate(
                'MapperWindow', 'Map saved: {filename}').format(filename=path.name))
            return True
        except (OSError, ValueError) as exc:
            QMessageBox.critical(self, translate('MapperWindow', 'Error saving'), str(exc))
            return False

    def prepare_to_replace_current_map(self, next_action, action_prompt, allow_cancel=True):
        """Resolve unsaved-map handling before opening, creating, or changing maps.

        Discarding affects only in-memory changes. Saving replaces the active
        file, while New Version writes a numbered copy before the next action
        proceeds.
        """
        s = self.state
        has_current_map = bool(self.current_map_path or s.pml_id.strip())
        if s.read_only:
            return True
        if not has_current_map:
            return True
        # Compatibility for older maps that still lack a PML and an active file
        # where a version can be saved.
        if self.pml_path() is None:
            return (QMessageBox.question(
                        self, next_action, translate('MapperWindow', 'Discard the current map?'))
                    == QMessageBox.StandardButton.Yes)
        message = QMessageBox(self)
        message.setWindowTitle(next_action)
        message.setText(translate(
                'MapperWindow', 'What would you like to do with the current map before {action_prompt}?').format(
                action_prompt=action_prompt))
        discard = message.addButton(translate('MapperWindow', 'Do not save'), QMessageBox.ButtonRole.DestructiveRole)
        new_version = message.addButton(translate('MapperWindow', 'New version'), QMessageBox.ButtonRole.ActionRole)
        save = message.addButton(translate('MapperWindow', 'Save'), QMessageBox.ButtonRole.AcceptRole)
        cancel = (message.addButton(translate('MapperWindow', 'Cancel'), QMessageBox.ButtonRole.RejectRole)
                  if allow_cancel else None)
        message.setDefaultButton(save)
        message.exec()
        if cancel is not None and message.clickedButton() is cancel:
            return False
        if (not allow_cancel
                and message.clickedButton() not in (discard, new_version, save)):
            return False
        try:
            if message.clickedButton() is new_version:
                path = self.save_new_pml_version()
                self.statusBar().showMessage(translate(
                    'MapperWindow', 'New version saved: {filename}').format(filename=path.name))
            elif message.clickedButton() is save:
                path = self.current_map_path or self.pml_path()
                if path is None:
                    raise ValueError(translate(
                        'MapperWindow', 'The current map has no associated file yet.'))
                path.parent.mkdir(parents=True, exist_ok=True)
                self.state.save(path)
                self.current_map_path = path
            # Do not save: continue without writing or deleting any file.
            self.refresh(redraw_map=False)
            return True
        except (OSError, ValueError) as exc:
            QMessageBox.critical(self, translate('MapperWindow', 'Error saving'), str(exc))
            return False

    def new_map(self):
        """Create a clean map for the current PML after confirmation.

        If the Rhino is more than the PML-match threshold from the current centre,
        the user can start a different PML. Otherwise metadata is kept and a new
        version is saved immediately.
        """
        if not self.prepare_to_replace_current_map(
                translate('MapperWindow', 'New map'), translate('MapperWindow', 'creating a new map')):
            return
        s = self.state
        if (s.pml_center_lat is not None and s.rhino_lat is not None
                and not corresponds_to_map(s, s.system, s.body,
                                           s.rhino_lat, s.rhino_lon)):
            answer = QMessageBox.question(
                self, translate('MapperWindow', 'New map in another PML'),
                translate('MapperWindow', 'You are more than 13 km from this PML centre. Is this a different PML?'))
            if answer == QMessageBox.StandardButton.Yes:
                # From here the previous map is no longer the active context:
                # Center [6] cannot follow Center [JD1].
                self.cancel_placement()
                self.state.new_map()
                self.current_map_path = None
                self.open_or_create_pml_for_current_position()
                return
        self.cancel_placement()
        # New starts a clean record while staying in the current PML, so Open,
        # Save, and Exit do not fall back to generic file behaviour.
        self.state.new_map(keep_pml=True)
        self.state.created_at = time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime())
        self.state.last_saved_at = None
        self.current_map_path = None
        if self.state.pml_center_lat is not None:
            x, y = self.state.llxy(self.state.pml_center_lat, self.state.pml_center_lon)
            self.state.marks.append(dict(name=f'Centro [{self.state.pml_id}]', x=x, y=y,
                                         lat=self.state.pml_center_lat,
                                         lon=self.state.pml_center_lon))
        try:
            path = self.save_new_pml_version()
            self.statusBar().showMessage(translate(
                'MapperWindow', 'New map created: {filename}').format(filename=path.name))
        except (OSError, ValueError) as exc:
            QMessageBox.critical(self, translate('MapperWindow', 'Error creating new map'), str(exc))
        self.view.scale = 0.08
        self.view.recenter()
        self.refresh()

    def save_map(self):
        """Save the active map through replace or new-version confirmation."""
        if self.map_is_read_only():
            return
        if self.state.center_lat is None:
            QMessageBox.information(self, translate('MapperWindow', 'Save'),
                                    translate('MapperWindow', 'There is no map to save yet.'))
            return
        try:
            directory = maps_directory()
        except OSError as exc:
            QMessageBox.critical(self, translate('MapperWindow', 'Error preparing MAPS'), str(exc))
            return
        canonical_path = self.current_map_path or self.pml_path()
        if canonical_path is not None:
            question = QMessageBox(self)
            question.setWindowTitle(translate('MapperWindow', 'Save map'))
            question.setText(translate('MapperWindow', 'How would you like to save this map?'))
            new_version = question.addButton(translate('MapperWindow', 'Save new version'), QMessageBox.ButtonRole.ActionRole)
            replace = question.addButton(translate('MapperWindow', 'Replace'), QMessageBox.ButtonRole.AcceptRole)
            cancel = question.addButton(translate('MapperWindow', 'Cancel'), QMessageBox.ButtonRole.RejectRole)
            question.setDefaultButton(replace)
            question.exec()
            if question.clickedButton() is cancel:
                return
            if question.clickedButton() is new_version:
                try:
                    path = self.save_new_pml_version()
                    self.statusBar().showMessage(translate(
                        'MapperWindow', 'Map saved: {filename}').format(filename=path.name))
                    self.refresh(redraw_map=False)
                except (OSError, ValueError) as exc:
                    QMessageBox.critical(self, translate('MapperWindow', 'Error saving'), str(exc))
                return
            path = canonical_path
            path.parent.mkdir(parents=True, exist_ok=True)
        else:
            path, _ = QFileDialog.getSaveFileName(
                self, translate('MapperWindow', 'Save map'), str(directory / 'mapa.json'), 'Rhino Map (*.json)')
            if not path:
                return
            if not Path(path).suffix:
                path += '.json'
        try:
            self.state.save(Path(path))
            self.current_map_path = Path(path)
            self.statusBar().showMessage(translate(
                'MapperWindow', 'Map saved: {filename}').format(filename=Path(path).name))
            self.refresh(redraw_map=False)
        except OSError as exc:
            QMessageBox.critical(self, translate('MapperWindow', 'Error saving'), str(exc))

    def load_map(self):
        """Open a saved map version after resolving current unsaved work.

        Current PML metadata narrows the picker to saved versions of that PML;
        legacy maps without PML identity still use the generic file selector.
        """
        s = self.state
        if not (s.system.strip() and s.body.strip() and s.pml_id.strip()):
            # Keep the generic picker only for legacy maps without PML identity.
            # It is also useful to recover a file before migrating it.
            try:
                directory = maps_directory()
            except OSError as exc:
                QMessageBox.critical(self, translate('MapperWindow', 'Error preparing MAPS'), str(exc))
                return
            path, _ = QFileDialog.getOpenFileName(
                self, translate('MapperWindow', 'Open map'), str(directory), 'Rhino Map (*.json)')
            if not path:
                return
            try:
                candidate = MapperState()
                candidate.load(Path(path))
            except (OSError, ValueError, TypeError, KeyError, AttributeError, OverflowError, ZeroDivisionError) as exc:
                QMessageBox.critical(self, translate('MapperWindow', 'Error opening'), str(exc))
                return
        else:
            try:
                directory = self.pml_path().parent
            except OSError as exc:
                QMessageBox.critical(self, translate('MapperWindow', 'Error preparing MAPS'), str(exc))
                return
            versions = []
            for path in directory.glob('*.json'):
                try:
                    candidate = MapperState()
                    candidate.load(path)
                    self.infer_legacy_pml(candidate, path, s.system, s.body)
                    if candidate.body.casefold() == s.body.casefold() and candidate.pml_id.casefold() == s.pml_id.casefold():
                        versions.append((path, candidate))
                except (OSError, ValueError, TypeError, KeyError, AttributeError):
                    continue
            if not versions:
                QMessageBox.information(
                    self, translate('MapperWindow', 'Open'),
                    translate('MapperWindow', 'There is no saved version of this PML yet.'))
                return
            versions.sort(key=lambda item: item[0].stat().st_mtime, reverse=True)
            labels = [translate('MapperWindow', '{timestamp} — {filename}').format(
                          timestamp=datetime.fromtimestamp(path.stat().st_mtime).strftime('%Y-%m-%d %H:%M'),
                          filename=path.name)
                      for path, _ in versions]
            selected_index = self.choose_list_item(
                translate('MapperWindow', 'Open PML [{pml_id}]').format(pml_id=s.pml_id),
                translate('MapperWindow', 'Version:'), labels)
            if selected_index is None:
                return
            path, candidate = versions[selected_index]
        try:
            if not self.prepare_to_replace_current_map(
                    translate('MapperWindow', 'Open map'), translate('MapperWindow', 'opening the map')):
                return
            if not self.install_loaded_map(
                    candidate, source_text=translate('MapperWindow', 'Loaded map'), source_path=path):
                return
        except (OSError, ValueError, TypeError, KeyError, AttributeError, OverflowError, ZeroDivisionError) as exc:
            QMessageBox.critical(self, translate('MapperWindow', 'Error opening'), str(exc))

    def edit_deposit_values(self, existing=None):
        """Run the deposit editor and return accepted values, or ``None``."""
        dialog = DepositDialog(self, existing)
        return dialog.values() if dialog.exec() == QDialog.DialogCode.Accepted else None

    def mark_deposit(self):
        """Create a deposit at the Rhino position captured before the dialog.

        Telemetry can continue arriving while the modal dialog is open, so the
        captured body and state are revalidated afterwards. Deposits closer than
        80 m are rejected to prevent duplicates.
        """
        if self.map_is_read_only():
            return
        s = self.state
        if s.rhino_lat is None or s.center_lat is None:
            QMessageBox.information(
                self, translate('MapperWindow', 'Deposit'),
                translate('MapperWindow', 'Enter the Rhino first.'))
            return
        # Capture the Rhino position before opening the modal editor; live
        # telemetry continues to arrive while the dialog is open.
        lat, lon = s.rhino_lat, s.rhino_lon
        body_key = s.body_key
        for deposit in s.deposits:
            dlat, dlon = math.radians(lat-deposit['lat']), math.radians(lon-deposit['lon'])
            # Haversine computes distance over the spherical surface instead of
            # comparing degrees directly or using the visual map scale.
            a = math.sin(dlat/2)**2 + math.cos(math.radians(lat))*math.cos(math.radians(deposit['lat']))*math.sin(dlon/2)**2
            if 2*s.radius*math.asin(min(1,math.sqrt(a))) < 80:
                QMessageBox.information(
                    self, translate('MapperWindow', 'Deposit already marked'),
                    translate('MapperWindow', 'A deposit already exists within 80 m.'))
                return
        values = self.edit_deposit_values()
        # Telemetry may change while the modal dialog is open; revalidate below.
        if values is not None and self.state is s and not s.read_only and s.body_key == body_key:
            x,y = s.llxy(lat,lon)
            s.deposits.append(dict(x=x,y=y,lat=lat,lon=lon,**values))
            self.refresh()

    def edit_mark_values(self, existing=None):
        """Run the marker editor and return accepted values, or ``None``."""
        dialog = MarkDialog(self, existing)
        return dialog.values() if dialog.exec() == QDialog.DialogCode.Accepted else None

    def mark(self):
        """Create a named marker from Rhino-relative bearing and distance.

        The Rhino position and body are captured before the modal dialog and
        rechecked afterward because telemetry may change during user input.
        """
        if self.map_is_read_only():
            return
        s = self.state
        if s.rhino_lat is None or s.center_lat is None:
            QMessageBox.information(
                self, translate('MapperWindow', 'Marker'),
                translate('MapperWindow', 'Enter the Rhino first and wait for its position.'))
            return
        self.cancel_placement()
        lat, lon, body = s.rhino_lat, s.rhino_lon, s.body_key
        # Capture happened before the modal marker dialog; validate the same map
        # is still active before committing the result.
        values = self.edit_mark_values()
        if values is None or self.state is not s or s.read_only or s.body_key != body:
            return
        s.marks.append(dict(name=values['name'], **self.mark_coordinates(s, lat, lon, values)))
        self.refresh()

    def alter_mark(self, item):
        """Edit an existing marker relative to the Rhino position at dialog open.

        The marker is updated only if it still belongs to the active map after the
        modal editor closes. Unchanged rounded values keep the original
        coordinates.
        """
        if self.map_is_read_only():
            return
        s = self.state
        if s.rhino_lat is None or s.center_lat is None:
            QMessageBox.information(
                self, translate('MapperWindow', 'Edit marker'),
                translate('MapperWindow', 'Enter the Rhino first and wait for its position.'))
            return
        lat, lon, body = s.rhino_lat, s.rhino_lon, s.body_key
        phi, target_phi = math.radians(lat), math.radians(item['lat'])
        delta = math.radians(item['lon']-lon)
        east = math.sin(delta)*math.cos(target_phi)
        north = math.cos(phi)*math.sin(target_phi)-math.sin(phi)*math.cos(target_phi)*math.cos(delta)
        a = math.sin((target_phi-phi)/2)**2 + math.cos(phi)*math.cos(target_phi)*math.sin(delta/2)**2
        existing = dict(name=item['name'],
                        azimuth=round(math.degrees(math.atan2(east, north)) % 360) % 360,
                        distance=round(2*s.radius*math.asin(math.sqrt(max(0, min(1, a)))), 2))
        values = self.edit_mark_values(existing)
        if (values is None or self.state is not s or s.read_only or s.body_key != body
                or not any(mark is item for mark in s.marks)):
            return
        # Accepting only a new name must not move the marker through rounded fields.
        if values['azimuth'] != existing['azimuth'] or values['distance'] != existing['distance']:
            item.update(self.mark_coordinates(s, lat, lon, values))
        item['name'] = values['name']
        self.refresh()

    @staticmethod
    def mark_coordinates(s, lat, lon, values):
        """Convert bearing and distance from a source lat/lon to marker data.

        The calculation follows the planet sphere and returns map metres plus
        geographic latitude/longitude without mutating state.
        """
        bearing = math.radians(values['azimuth'])
        arc = values['distance'] / s.radius
        phi, lam = math.radians(lat), math.radians(lon)
        dest_phi = math.asin(max(-1, min(1, math.sin(phi)*math.cos(arc) + math.cos(phi)*math.sin(arc)*math.cos(bearing))))
        dest_lam = lam + math.atan2(math.sin(bearing)*math.sin(arc)*math.cos(phi), math.cos(arc)-math.sin(phi)*math.sin(dest_phi))
        lat, lon = math.degrees(dest_phi), (math.degrees(dest_lam)+180)%360-180
        x, y = s.llxy(lat, lon)
        return dict(x=x, y=y, lat=lat, lon=lon)

    def mark_rig(self):
        """Enter one-click rig placement mode for the map canvas."""
        if self.map_is_read_only():
            return
        if self.state.center_lat is None:
            QMessageBox.information(
                self, translate('MapperWindow', 'Rig'),
                translate('MapperWindow', 'Enter the Rhino first.'))
            return
        self.placing_rig = True
        self.statusBar().showMessage(translate(
            'MapperWindow', 'Click the map to place the rig. Escape cancels.'))

    def cancel_placement(self):
        """Cancel pending rig placement and clear placement guidance."""
        self.placing_rig = False
        self.statusBar().clearMessage()

    def place_rig(self, point):
        """Place a rig at the clicked map point when placement mode is active.

        The screen point is converted to world metres and then to
        latitude/longitude. Placement mode is cleared immediately after a
        successful insert.
        """
        if self.state.read_only or not self.placing_rig or self.state.center_lat is None:
            return
        q = self.view.world(point)
        lat, lon = self.state.xyll(q.x(), q.y())
        self.state.rigs.append(dict(x=q.x(), y=q.y(), lat=lat, lon=lon))
        self.cancel_placement()
        self.refresh()

    def edit_deposit(self, item):
        """Apply accepted deposit edits if the item still belongs to the map."""
        if self.map_is_read_only():
            return
        values = self.edit_deposit_values(item)
        if values is not None and not self.state.read_only and any(d is item for d in self.state.deposits):
            item.update(values)
            self.refresh()

    def delete_marker(self, kind, item):
        """Delete a marker-like item by identity after confirmation.

        Identity comparison prevents deleting another dictionary with equal
        values. Active navigation to the deleted item is stopped before refreshing.
        """
        if self.map_is_read_only():
            return
        if QMessageBox.question(
                self, translate('MapperWindow', 'Delete marker'),
                translate('MapperWindow', 'Delete this marker?')) == QMessageBox.StandardButton.Yes:
            items = getattr(self.state,kind)
            for index, candidate in enumerate(items):
                if candidate is item:
                    del items[index]
                    break
            if self.state.active_nav_target and self.state.active_nav_target.get('item') is item:
                self.stop_navigation()
            self.refresh()

    def is_navigating_to(self, kind, item):
        """Return whether the given item is the current navigation target."""
        target = self.state.active_nav_target
        if target is None:
            return False
        return target.get('item') is item

    def start_navigation(self, kind, item):
        """Start direct navigation to a mark, deposit, rig, or route point.

        If a search is active, it is paused and the pause point is stored so the
        route can resume after direct navigation ends.
        """
        s = self.state
        if s.read_only and kind == 'route':
            return
        if s.rhino_lat is None or s.center_lat is None:
            QMessageBox.information(
                self, translate('MapperWindow', 'Navigate'),
                translate('MapperWindow', 'Enter the Rhino first and wait for its position.'))
            return

        if kind == 'marks':
            x, y = s.llxy(item['lat'], item['lon'])
            name = translate('MapperWindow', '[Marker] {name}').format(name=item['name'])
            target_data = {'type': kind, 'item': item, 'name': name, 'x': x, 'y': y, 'lat': item['lat'], 'lon': item['lon']}
        elif kind == 'deposits':
            x, y = item['x'], item['y']
            name = translate('MapperWindow', '[Deposit] {name}').format(
                name=item.get('name', translate('MapperWindow', 'Deposit')))
            target_data = {'type': kind, 'item': item, 'name': name, 'x': x, 'y': y, 'lat': item.get('lat'), 'lon': item.get('lon')}
        elif kind == 'rigs':
            x, y = item['x'], item['y']
            name = "[Rig]"
            target_data = {'type': kind, 'item': item, 'name': name, 'x': x, 'y': y, 'lat': item.get('lat'), 'lon': item.get('lon')}
        elif kind == 'route':
            x, y = item['x'], item['y']
            num = item.get('number', s.route_index + 1)
            name = f"[Busca] Ponto {num}"
            target_data = {'type': kind, 'item': item, 'name': name, 'x': x, 'y': y}
        else:
            return

        if s.search_started and not s.search_paused:
            s.search_paused = True
            s.search_pause_point = s.llxy(s.rhino_lat, s.rhino_lon)

        s.return_to_pause = False
        s.active_nav_target = target_data
        self.statusBar().showMessage(translate(
            'MapperWindow', 'Navigating to: {name}').format(name=name))
        self.refresh()

    def stop_navigation(self):
        """Stop direct navigation and optionally return to the search pause point."""
        s = self.state
        s.active_nav_target = None
        if s.search_paused and s.search_pause_point is not None:
            s.return_to_pause = True
            self.statusBar().showMessage(translate(
                'MapperWindow', 'Navigation stopped. Returning to Pause Point ⏸.'))
        else:
            s.search_paused = False
            s.search_pause_point = None
            s.return_to_pause = False
            self.statusBar().showMessage(translate('MapperWindow', 'Navigation stopped.'))
        self.refresh()

    def marker_menu(self, position):
        """Show the context menu for the marker under a screen position.

        Read-only maps expose information and coordinate copying only. Editable
        maps add contextual edit/delete actions while lambdas defer work until the
        user chooses.
        """
        found = self.view.marker_at(position)
        if found is None:
            return
        kind, item = found
        menu = QMenu(self)

        # Navigate/stop-navigation action is available for every marker type.
        if self.is_navigating_to(kind, item):
            menu.addAction(translate('MapperWindow', 'Stop navigation'), self.stop_navigation)
        else:
            menu.addAction(translate('MapperWindow', 'Navigate'),
                           lambda: self.start_navigation(kind, item))
        menu.addSeparator()

        if self.state.read_only:
            fallback_name = (translate('MapperWindow', 'Rig')
                             if kind == 'rigs'
                             else translate('MapperWindow', 'Marker'))
            details = translate('MapperWindow', 'Name: {name}').format(
                name=item.get('name', fallback_name))
            if kind == 'deposits':
                details += '\n' + translate('MapperWindow', 'Size: {size}').format(
                    size=item.get('size', '—'))
                details += '\n' + translate('MapperWindow', 'Rigs: {count}').format(
                    count=item.get('rigs', 0))
            details += '\n' + translate('MapperWindow', 'Latitude: {latitude}°').format(
                latitude=f"{item.get('lat', 0):.5f}")
            details += '\n' + translate('MapperWindow', 'Longitude: {longitude}°').format(
                longitude=f"{item.get('lon', 0):.5f}")
            menu.addAction(
                translate('MapperWindow', 'Information'),
                lambda: QMessageBox.information(
                    self, translate('MapperWindow', 'Marked point'), details))
            menu.addAction(translate('MapperWindow', 'Copy coordinates'), lambda: QApplication.clipboard().setText(
                f"{item.get('lat', 0):.5f} {item.get('lon', 0):.5f}"))
            menu.exec(self.view.mapToGlobal(position.toPoint()))
            return

        if kind == 'marks':
            menu.addAction(translate('MapperWindow', 'Edit'), lambda: self.alter_mark(item))
            menu.addAction(translate('MapperWindow', 'Delete'), lambda: self.delete_marker(kind, item))
        elif kind == 'deposits':
            menu.addAction(translate('MapperWindow', 'Copy coordinates'), lambda: QApplication.clipboard().setText(f"{item['lat']:.5f} {item['lon']:.5f}"))
            menu.addAction(translate('MapperWindow', 'Edit deposit'), lambda: self.edit_deposit(item))
            menu.addAction(translate('MapperWindow', 'Delete deposit'), lambda: self.delete_marker(kind, item))
        elif kind == 'rigs':
            menu.addAction(translate('MapperWindow', 'Delete rig'), lambda: self.delete_marker(kind, item))
        elif kind == 'route':
            pass

        menu.addSeparator()
        menu.addAction(translate('MapperWindow', 'Cancel'))
        menu.exec(self.view.mapToGlobal(position.toPoint()))
