"""Exercise map operation dialogs, marker workflows, and PML lifecycle handling.

The suite protects persistence, telemetry gating, and user-decision contracts that
must remain stable while map replacement and editing flows evolve.
"""
import importlib.util
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch
# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM','offscreen')

@unittest.skipUnless(importlib.util.find_spec('PySide6'), 'PySide6 não instalado')
class MapOperationsTests(unittest.TestCase):
    """Cover operation flows sharing one offscreen MapperWindow per test."""
    @classmethod
    def setUpClass(cls):
        from PySide6.QtWidgets import QApplication
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        from rhino_surface_mapper_qt import MapperWindow
        self.temp = tempfile.TemporaryDirectory()
        self.window = MapperWindow(
            Path(self.temp.name)/'Status.json', game_running_check=lambda: True)
        # Stop polling timers so test assertions depend only on explicit calls.
        self.window.timer.stop()
        self.window.state.process_status(dict(Flags=0x04000000,Latitude=38,Longitude=-9,Heading=0,BodyName='Test'))

    def tearDown(self):
        self.window.transition_required = False
        with patch.object(self.window, 'confirm_pml_exit', return_value=True):
            self.window.close()
        self.temp.cleanup()

    def test_deposit_dialog_ui_preserves_widget_contract_and_defaults(self):
        from qt_map_operations import DepositDialog
        from PySide6.QtWidgets import QDialogButtonBox, QLabel
        dialog = DepositDialog(self.window)
        self.addCleanup(dialog.close)
        self.assertEqual(dialog.name.objectName(), 'name')
        self.assertEqual(dialog.size.objectName(), 'size')
        self.assertEqual(dialog.rigs.objectName(), 'rigs')
        self.assertEqual(dialog.size.count(), 4)
        self.assertEqual((dialog.rigs.minimum(), dialog.rigs.maximum()), (1, 6))
        self.assertEqual(dialog.windowTitle(), 'Mark deposit')
        self.assertEqual(dialog.findChild(QLabel, 'nameLabel').text(), 'Name:')
        self.assertEqual(dialog.findChild(QLabel, 'sizeLabel').text(), 'Size:')
        self.assertEqual(dialog.findChild(QLabel, 'rigsLabel').text(), 'Number of rigs:')
        self.assertEqual(dialog.values(), {'name': 'Depósito', 'size': 'Pequeno', 'rigs': 1})
        self.assertEqual(dialog.size.itemText(0), 'Small')
        self.assertEqual(dialog.size.itemData(0), 'Pequeno')
        self.assertIsNotNone(dialog.findChild(QDialogButtonBox, 'buttonBox'))

    def test_deposit_dialog_ui_populates_existing_values_and_rejects(self):
        from qt_map_operations import DepositDialog
        from PySide6.QtWidgets import QDialog
        dialog = DepositDialog(self.window, {'name': 'A', 'size': 'Grande', 'rigs': 3})
        self.addCleanup(dialog.close)
        self.assertEqual(dialog.values(), {'name': 'A', 'size': 'Grande', 'rigs': 3})
        dialog.reject()
        self.assertEqual(dialog.result(), QDialog.DialogCode.Rejected)

    def test_deposit_dialog_keeps_portuguese_size_values_separate_from_labels(self):
        from qt_map_operations import DepositDialog

        dialog = DepositDialog(self.window, {'name': 'A', 'size': 'Médio', 'rigs': 3})
        self.addCleanup(dialog.close)
        self.assertEqual(dialog.size.currentText(), 'Medium')
        self.assertEqual(dialog.size.currentData(), 'Médio')
        self.assertEqual(dialog.values()['size'], 'Médio')

    def test_deposit_duplicate_edit_rig_cancel_and_delete(self):
        from PySide6.QtCore import QPointF
        from PySide6.QtWidgets import QMessageBox
        w = self.window
        with patch.object(w,'edit_deposit_values',return_value=dict(name='A',size='Grande',rigs=3)):
            w.mark_deposit()
        self.assertEqual(len(w.state.deposits),1)
        # A second deposit at the same position exercises the 80 m separation rule.
        with patch('qt_map_operations.QMessageBox.information') as info:
            w.mark_deposit()
            info.assert_called_once()
        item = w.state.deposits[0]
        with patch.object(w,'edit_deposit_values',return_value=None):
            w.edit_deposit(item)
        self.assertEqual(item['name'],'A')
        with patch.object(w,'edit_deposit_values',return_value=dict(name='B',size='Médio',rigs=2)):
            w.edit_deposit(item)
        self.assertEqual(item['name'],'B')
        self.assertIs(w.view.marker_at(w.view.screen(item['x'],item['y']))[1],item)
        w.mark_rig()
        w.cancel_placement()
        w.place_rig(QPointF(100,100))
        self.assertEqual(w.state.rigs,[])
        w.mark_rig()
        w.place_rig(QPointF(100,100))
        w.place_rig(QPointF(200,200))
        self.assertEqual(len(w.state.rigs),1)
        with patch('qt_map_operations.QMessageBox.question',return_value=QMessageBox.StandardButton.Yes):
            w.delete_marker('deposits',item)
        self.assertEqual(w.state.deposits,[])

    def test_save_load_round_trip_and_invalid_file_preserves_map(self):
        from PySide6.QtWidgets import QMessageBox
        w = self.window
        with tempfile.TemporaryDirectory() as directory:
            path = str(Path(directory)/'map.json')
            w.state.start_search()
            expected = w.state.to_dict()
            with patch('qt_map_operations.QFileDialog.getSaveFileName',return_value=(path,'')):
                w.save_map()
            with patch('qt_map_operations.QFileDialog.getOpenFileName',return_value=(path,'')), patch('qt_map_operations.QMessageBox.question',return_value=QMessageBox.StandardButton.Yes):
                w.load_map()
            # Loading migrates timestamps missing from older map files.
            self.assertIsNotNone(w.state.created_at)
            self.assertIsNotNone(w.state.last_saved_at)
            expected.update(created_at=w.state.created_at, last_saved_at=w.state.last_saved_at)
            self.assertEqual(w.state.to_dict(),expected)
            old = w.state
            Path(path).write_text('{',encoding='utf-8')
            with patch('qt_map_operations.QFileDialog.getOpenFileName',return_value=(path,'')), patch('qt_map_operations.QMessageBox.critical') as error:
                w.load_map()
                error.assert_called_once()
            self.assertIs(w.state,old)
            with patch('qt_map_operations.QMessageBox.question',return_value=QMessageBox.StandardButton.Yes), \
                    patch.object(w, 'save_new_pml_version', return_value=Path(path)):
                w.new_map()
            self.assertFalse(w.state.search_started)
            self.assertFalse(w.overlay.isVisible())
            self.assertFalse(w.overlay_button.isEnabled())

    def test_pml_center_azimuth_dialog_wraps_without_changing_contract(self):
        from PySide6.QtWidgets import QDialog, QInputDialog, QSpinBox
        w = self.window

        def inspect_spin(dialog):
            spin = dialog.findChild(QSpinBox)
            self.assertIsNotNone(spin)
            self.assertTrue(spin.wrapping())
            self.assertEqual((spin.minimum(), spin.maximum()), (0, 359))
            self.assertEqual(spin.singleStep(), 1)
            spin.setValue(0)
            spin.stepBy(-1)
            self.assertEqual(spin.value(), 359)
            spin.setValue(359)
            spin.stepBy(1)
            self.assertEqual(spin.value(), 0)

        with patch('qt_map_operations.QInputDialog.getText', return_value=('PML', True)), \
                patch.object(QInputDialog, 'exec', return_value=QDialog.DialogCode.Accepted), \
                patch('qt_map_operations.QInputDialog.getDouble', return_value=(0.0, True)):
            result = w.setup_new_pml()

        dialog = w.findChildren(QInputDialog)[-1]
        inspect_spin(dialog)
        self.assertTrue(result)

        self.assertEqual(dialog.windowTitle(), 'PML centre [PML]')
        self.assertEqual(dialog.labelText(), 'Bearing from Rhino to the PML centre (0° = north):')
        self.assertEqual(dialog.intValue(), 0)

    def test_choose_list_item_returns_selected_index_and_preserves_cancellation(self):
        from PySide6.QtWidgets import QDialog, QInputDialog

        w = self.window
        dialog = Mock()
        combo = Mock()
        combo.currentIndex.return_value = 1
        dialog.findChild.return_value = combo
        dialog.exec.return_value = QDialog.DialogCode.Accepted
        with patch('qt_map_operations.QInputDialog', return_value=dialog):
            self.assertEqual(w.choose_list_item('Title', 'Choose:', ['same', 'same']), 1)

        dialog.exec.return_value = QDialog.DialogCode.Rejected
        with patch('qt_map_operations.QInputDialog', return_value=dialog):
            self.assertIsNone(w.choose_list_item('Title', 'Choose:', ['same', 'same']))

    def test_replace_confirmation_uses_explicit_prompt_text(self):
        from PySide6.QtWidgets import QMessageBox

        w = self.window
        w.state.pml_id = 'PML'
        discard, new_version, save, cancel = object(), object(), object(), object()
        box = Mock()
        box.addButton.side_effect = [discard, new_version, save, cancel]
        box.clickedButton.return_value = discard
        message_box = Mock(return_value=box)
        message_box.ButtonRole = QMessageBox.ButtonRole
        with patch('qt_map_operations.QMessageBox', message_box), \
                patch.object(w, 'pml_path', return_value=Path('map.json')), \
                patch.object(w, 'refresh'):
            self.assertTrue(w.prepare_to_replace_current_map(
                'Display title', 'open the map'))

        box.setText.assert_called_once_with(
            'What would you like to do with the current map before open the map?')
    def test_load_rereads_unchanged_status_immediately(self):
        import json
        from PySide6.QtWidgets import QMessageBox
        w = self.window
        with tempfile.TemporaryDirectory() as directory:
            status_path = Path(directory)/'Status.json'
            map_path = Path(directory)/'map.json'
            status_path.write_text(json.dumps(dict(Flags=0x04000000,Latitude=38,Longitude=-9,Heading=90,BodyName='Test',Fuel=dict(FuelReservoir=0.4))),encoding='utf-8')
            w.status_path = status_path
            w.poll()
            w.state.start_search()
            w.state.save(map_path)
            stamp = w.last_mtime
            with patch('qt_map_operations.QFileDialog.getOpenFileName',return_value=(str(map_path),'')), patch('qt_map_operations.QMessageBox.question',return_value=QMessageBox.StandardButton.Yes):
                w.load_map()
            self.assertEqual(w.last_mtime,stamp)
            self.assertEqual(w.state.rhino_lat,38)
            self.assertEqual(w.state.rhino_heading,90)
            self.assertEqual(w.state.fuel_percent,50)
            self.assertIn('50%',w.info_right.text())
            self.assertTrue(w.state.search_started)
            self.assertIsNotNone(w.state.next_target_xy)

    def test_poll_opens_pml_flow_when_late_system_completes_body_identity(self):
        import json

        w = self.window
        w.status_path.write_text(json.dumps(dict(
            Flags=0x04000000, Latitude=38, Longitude=-9, Heading=0,
            StarSystem='Teste', BodyName='Test')),
            encoding='utf-8')

        with patch.object(w, 'open_or_create_pml_for_current_position') as open_pml:
            w.poll()

        self.assertEqual((w.state.system, w.state.body, w.state.body_key),
                         ('Teste', 'Test', 'Teste|Test'))
        self.assertFalse(w.transition_required)
        open_pml.assert_called_once_with()

    def test_first_map_bootstrap_uses_journal_system_when_status_omits_it(self):
        import json
        from elite_dangerous.journal import JournalIdentityReader

        journal_root = Path(self.temp.name) / 'journal'
        journal_root.mkdir()
        (journal_root / 'Journal.2026-09-25T180148.01.log').write_text(
            json.dumps(dict(event='Location', StarSystem='Teste', Body='Test')) + '\n',
            encoding='utf-8')
        w = self.window
        w.journal_identity = JournalIdentityReader(journal_root)
        w.status_path.write_text(json.dumps(dict(
            Flags=0x04000000, Latitude=38, Longitude=-9, Heading=0,
            BodyName='Test')), encoding='utf-8')

        with patch.object(w, 'setup_new_pml', return_value=False) as setup_pml:
            w.poll()

        self.assertEqual((w.state.system, w.state.body, w.state.body_key),
                         ('Teste', 'Test', 'Teste|Test'))
        setup_pml.assert_called_once_with()

    def test_stale_srv_status_stays_offline_when_game_is_not_running(self):
        import json
        from rhino_surface_mapper_qt import MapperWindow

        status_path = Path(self.temp.name)/'stale-status.json'
        status_path.write_text(json.dumps(dict(
            Flags=0x04000000, Fuel=dict(FuelReservoir=0.4),
            Latitude=38, Longitude=-9, Heading=90,
            StarSystem='Kappa', BodyName='Kappa 2 a')), encoding='utf-8')
        with patch.object(
                MapperWindow, 'open_or_create_pml_for_current_position') as open_pml:
            window = MapperWindow(status_path, game_running_check=lambda: False)
        self.addCleanup(window.close)
        window.timer.stop()
        window.radar_timer.stop()

        self.assertFalse(window.status_valid)
        self.assertFalse(window.state.in_srv)
        self.assertEqual(window.live_status, {})
        self.assertIsNone(window.state.fuel_percent)
        self.assertEqual(
            (window.state.rhino_lat, window.state.rhino_lon, window.state.rhino_heading),
            (None, None, None))
        self.assertFalse(window.search_button.isEnabled())
        open_pml.assert_not_called()

    def test_game_closing_clears_only_live_session_state(self):
        import json

        w = self.window
        w.status_path.write_text(json.dumps(dict(
            Flags=0x04000000, Fuel=dict(FuelReservoir=0.4),
            Latitude=38, Longitude=-9, Heading=90,
            StarSystem='Teste', BodyName='Test')), encoding='utf-8')
        with patch.object(w, 'open_or_create_pml_for_current_position'):
            w.poll()
        w.state.pml_id = 'PML-1'
        w.state.deposits.append({'name': 'Deposit'})
        persistent = (w.state.system, w.state.body, w.state.body_key,
                      w.state.pml_id, list(w.state.deposits))

        w._game_running_check = lambda: False
        w._next_game_running_check = 0.0
        w.poll()

        self.assertFalse(w.status_valid)
        self.assertFalse(w.state.in_srv)
        self.assertEqual(w.live_status, {})
        self.assertIsNone(w.state.fuel_percent)
        self.assertEqual(
            (w.state.rhino_lat, w.state.rhino_lon, w.state.rhino_heading),
            (None, None, None))
        self.assertEqual(
            (w.state.system, w.state.body, w.state.body_key,
             w.state.pml_id, w.state.deposits), persistent)

    def test_mark_position_persistence_rename_and_delete(self):
        from mapper_core import MapperState
        from PySide6.QtCore import QPointF
        from PySide6.QtWidgets import QMessageBox
        w = self.window
        for azimuth, expected_x, expected_y in [(0, 0, 1000), (90, 1000, 0), (180, 0, -1000), (270, -1000, 0)]:
            with patch.object(w, 'edit_mark_values', return_value=dict(name='Local', azimuth=azimuth, distance=1000)):
                w.mark()
            item = w.state.marks[-1]
            self.assertAlmostEqual(item['x'], expected_x, delta=1)
            self.assertAlmostEqual(item['y'], expected_y, delta=1)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)/'marks.json'
            w.state.save(path)
            restored = MapperState()
            restored.load(path)
            self.assertEqual(restored.marks, w.state.marks)
        with patch.object(w, 'edit_mark_values', return_value=dict(name='Novo nome', azimuth=90, distance=2000)):
            w.alter_mark(item)
        self.assertAlmostEqual(item['x'], 2000, delta=1)
        self.assertAlmostEqual(item['y'], 0, delta=1)
        self.assertEqual(item['name'], 'Novo nome')
        bounds = w.view.mark_bounds(item)
        self.assertIs(w.view.marker_at(QPointF(bounds.right()-3, bounds.center().y()))[1], item)
        w.view.grab()  # Run offscreen vector and label painting before hit testing.
        with patch('qt_map_operations.QMessageBox.question', return_value=QMessageBox.StandardButton.Yes):
            w.delete_marker('marks', item)
        self.assertEqual(len(w.state.marks), 3)
        w.state.new_map()
        self.assertEqual(w.state.marks, [])

    def test_mark_cancel_and_body_change(self):
        w = self.window
        with patch.object(w, 'edit_mark_values', return_value=None):
            w.mark()
        self.assertEqual(w.state.marks, [])
        with patch.object(w, 'edit_mark_values', return_value=dict(name='Local', azimuth=0, distance=100)):
            w.mark()
        before = list(w.state.marks)
        result = w.state.process_status(dict(Flags=0x04000000, Latitude=38,
                                             Longitude=-9, Heading=0, BodyName='Other'))
        self.assertTrue(result.location_changed)
        self.assertEqual(w.state.marks, before)

    def test_active_map_lifecycle_evaluation_preserves_pending_mismatch(self):
        from mapper_core import MapperState
        from map_pml import PML_MATCH_DISTANCE_M

        w = self.window
        state = MapperState()
        state.process_status(dict(Flags=0x04000000, Latitude=38, Longitude=-9,
                                  Heading=0, StarSystem='Sol', BodyName='Earth'))
        state.pml_id = '6'
        state.pml_center_lat, state.pml_center_lon = 38, -9
        w.state = w.view.state = state
        w.current_map_path = Path(self.temp.name) / 'active.json'
        # Convert the 13 km PML match boundary to degrees on this body.
        degrees = PML_MATCH_DISTANCE_M / state.radius * 180 / 3.141592653589793

        nearby = w.active_map_corresponds('Sol', 'Earth', 38, -9 + degrees / 2)
        self.assertTrue(nearby)
        nearby_update = state.process_status(dict(Flags=0x04000000, Latitude=38,
                                                   Longitude=-9 + degrees / 2,
                                                   Heading=0, StarSystem='Sol',
                                                   BodyName='Earth'))
        w.evaluate_status_update(nearby_update, nearby)
        self.assertFalse(w.transition_required)
        self.assertIsNone(w.pending_status_update)
        original = (state.system, state.body, state.pml_id,
                    state.pml_center_lat, state.pml_center_lon,
                    state.rhino_lat, state.rhino_lon, list(state.points),
                    w.current_map_path)

        mismatch_lat = 38 + degrees + .001
        mismatch = w.active_map_corresponds('Sol', 'Earth', mismatch_lat, -9)
        self.assertFalse(mismatch)
        before_position = (state.rhino_lat, state.rhino_lon, list(state.points))
        mismatch_update = state.process_status(dict(Flags=0x04000000, Latitude=mismatch_lat,
                                                     Longitude=-9,
                                                     Heading=0, StarSystem='Sol',
                                                     BodyName='Earth'),
                                                record_position=False)
        w.evaluate_status_update(mismatch_update, mismatch)
        self.assertTrue(w.transition_required)
        self.assertIs(w.pending_status_update, mismatch_update)
        self.assertEqual((state.rhino_lat, state.rhino_lon, state.points), before_position)
        self.assertEqual((state.system, state.body, state.pml_id,
                          state.pml_center_lat, state.pml_center_lon,
                          state.rhino_lat, state.rhino_lon, state.points,
                          w.current_map_path), original)

        corresponding_update = state.process_status(dict(Flags=0x04000000, Latitude=38,
                                                          Longitude=-9, Heading=0,
                                                          StarSystem='Sol', BodyName='Earth'))
        w.evaluate_status_update(corresponding_update, True)
        self.assertTrue(w.transition_required)
        self.assertIs(w.pending_status_update, mismatch_update)

        non_srv_update = state.process_status(dict(Flags=0, Latitude=38,
                                                   Longitude=-9, StarSystem='Sol',
                                                   BodyName='Earth'))
        w.evaluate_status_update(non_srv_update, None)
        self.assertTrue(w.transition_required)
        self.assertIs(w.pending_status_update, mismatch_update)

    def test_active_map_lifecycle_evaluation_handles_identity_and_protection(self):
        from mapper_core import MapperState

        for identifier in ('6', 'JD1'):
            w = self.window
            state = MapperState()
            state.process_status(dict(Flags=0x04000000, Latitude=38, Longitude=-9,
                                      Heading=0, StarSystem='Sol', BodyName='Earth'))
            state.pml_id = identifier
            state.pml_center_lat, state.pml_center_lon = 38, -9
            state.marks.append({'name': 'Keep', 'x': 0, 'y': 0, 'lat': 38, 'lon': -9})
            if identifier == 'JD1':
                state.protected = True
                state.enter_mining_mode()
            w.state = w.view.state = state
            w.transition_required = False
            w.pending_status_update = None
            before = (state.system, state.body, state.pml_id,
                      state.pml_center_lat, state.pml_center_lon,
                      list(state.marks), state.protected, state.mining_only)

            incoming = state.process_status(dict(Flags=0x04000000, Latitude=38,
                                                 Longitude=-9, Heading=0,
                                                 StarSystem='Other', BodyName='Mars'))
            correspondence = w.active_map_corresponds('Other', 'Mars', 38, -9)
            w.evaluate_status_update(incoming, correspondence)

            self.assertFalse(correspondence)
            self.assertTrue(w.transition_required)
            self.assertEqual((incoming.system, incoming.body), ('Other', 'Mars'))
            self.assertEqual((state.system, state.body, state.pml_id,
                              state.pml_center_lat, state.pml_center_lon,
                              state.marks, state.protected, state.mining_only), before)

    def test_pending_transition_retains_telemetry_and_schedules_once(self):
        from mapper_core import MapperState

        w = self.window
        state = MapperState()
        state.process_status(dict(Flags=0x04000000, Latitude=38, Longitude=-9,
                                  Heading=0, StarSystem='Sol', BodyName='Earth'))
        state.pml_id = '6'
        state.pml_center_lat, state.pml_center_lon = 38, -9
        w.state = w.view.state = state
        raw_status = dict(Flags=0x04000000, Latitude=38, Longitude=-9,
                          Heading=12, StarSystem='Other', BodyName='Mars',
                          PlanetRadius=3_000_000)
        # The pending transition keeps raw telemetry so stale data is not committed.
        update = state.process_status(raw_status, record_position=False)

        with patch('rhino_surface_mapper_qt.QTimer.singleShot') as single_shot:
            w.evaluate_status_update(update, False, raw_status)
            w.evaluate_status_update(update, False, raw_status)

        self.assertTrue(w.transition_required)
        self.assertIs(w.pending_status_update, update)
        self.assertEqual(w.pending_status_snapshot, raw_status)
        single_shot.assert_called_once()
        self.assertTrue(state.in_srv)

    def test_pending_transition_gates_foreign_poll_consumers(self):
        import json
        from mapper_core import MapperState

        w = self.window
        state = MapperState()
        state.process_status(dict(Flags=0x04000000, Latitude=38, Longitude=-9,
                                  Heading=0, StarSystem='Sol', BodyName='Earth'))
        state.pml_id = '6'
        state.pml_center_lat, state.pml_center_lon = 38, -9
        w.state = w.view.state = state
        before = (state.rhino_lat, state.rhino_lon, list(state.points),
                  state.system, state.body, state.pml_id,
                  state.pml_center_lat, state.pml_center_lon)
        w.status_path.write_text(json.dumps(dict(
            Flags=0x04000000 | 0x08000000,
            Latitude=38, Longitude=-9, Heading=45,
            StarSystem='Other', BodyName='Mars',
            PlanetRadius=3_000_000, FireGroup=0, GuiFocus=0)),
            encoding='utf-8')

        with patch.object(w, 'observe_steering') as observe, \
                patch.object(w.view.radar, 'tick') as radar_tick:
            w.poll()
            w.update_radar()

        self.assertTrue(w.transition_required)
        self.assertTrue(state.in_srv)
        self.assertEqual((state.rhino_lat, state.rhino_lon, state.points,
                          state.system, state.body, state.pml_id,
                          state.pml_center_lat, state.pml_center_lon), before)
        observe.assert_not_called()
        radar_tick.assert_not_called()
        self.assertFalse(w.overlay_button.isEnabled())

    def test_pending_transition_resolves_old_map_once_without_installing(self):
        from mapper_core import MapperState

        w = self.window
        old_state = w.state
        w.transition_required = True
        destination = dict(state=MapperState(), path=None, source_text='prepared')
        with patch.object(w, 'prepare_to_replace_current_map', return_value=True) as replace, \
                patch.object(w, 'prepare_pending_destination', return_value=destination) as prepare:
            self.assertIs(w.resolve_pending_transition(), destination)
            self.assertIs(w.resolve_pending_transition(), destination)

        replace.assert_called_once_with(
            'Change location', 'changing location', allow_cancel=False)
        prepare.assert_called_once_with()
        self.assertTrue(w.pending_old_map_resolved)
        self.assertIs(w.pending_destination, destination)
        self.assertIs(w.state, old_state)
        self.assertTrue(w.transition_required)

    def test_pending_transition_dismissal_does_not_resolve_old_map(self):
        w = self.window
        w.transition_required = True
        with patch.object(w, 'prepare_to_replace_current_map', return_value=False) as replace, \
                patch.object(w, 'prepare_pending_destination') as prepare:
            self.assertIsNone(w.resolve_pending_transition())

        replace.assert_called_once_with(
            'Change location', 'changing location', allow_cancel=False)
        prepare.assert_not_called()
        self.assertFalse(w.pending_old_map_resolved)
        self.assertIsNone(w.pending_destination)
        self.assertTrue(w.transition_required)

    def test_destination_retry_does_not_repeat_old_map_resolution(self):
        w = self.window
        w.transition_required = True
        destination = dict(state=object(), path=None, source_text='prepared')
        with patch.object(w, 'prepare_to_replace_current_map', return_value=True) as replace, \
                patch.object(w, 'prepare_pending_destination', side_effect=[None, destination]):
            self.assertIsNone(w.resolve_pending_transition())
            self.assertIs(w.resolve_pending_transition(), destination)

        replace.assert_called_once_with(
            'Change location', 'changing location', allow_cancel=False)
        self.assertTrue(w.pending_old_map_resolved)
        self.assertIs(w.pending_destination, destination)

    def test_new_destination_is_prepared_detached_from_live_state(self):
        from mapper_core import MapperState

        w = self.window
        old_state = w.state
        status = dict(Flags=0x04000000, Latitude=38, Longitude=-9,
                      Heading=0, StarSystem='Sol', BodyName='Earth',
                      PlanetRadius=3_000_000)
        w.latest_status_snapshot = status
        # Square brackets are part of persisted PML version filenames.
        destination_path = Path(self.temp.name) / 'Earth [JD1].json'

        def classify(candidate):
            candidate.pml_id = 'JD1'
            candidate.pml_center_lat = candidate.rhino_lat
            candidate.pml_center_lon = candidate.rhino_lon
            candidate.created_at = '2026-01-01T00:00:00Z'
            return True

        with patch.object(w, 'nearby_pml_maps', return_value=[]), \
                patch.object(w, 'setup_new_pml', side_effect=classify), \
                patch.object(w, 'pml_path', return_value=destination_path):
            prepared = w.prepare_pending_destination()

        self.assertIsInstance(prepared['state'], MapperState)
        self.assertIsNot(prepared['state'], old_state)
        self.assertEqual(prepared['state'].pml_id, 'JD1')
        self.assertEqual(prepared['path'], destination_path)
        self.assertTrue(destination_path.exists())
        self.assertIs(w.state, old_state)
        self.assertTrue(w.transition_required is False)

    def test_existing_destination_is_prepared_detached_without_poll(self):
        from mapper_core import MapperState

        w = self.window
        old_state = w.state
        status = dict(Flags=0x04000000, Latitude=38, Longitude=-9,
                      Heading=0, StarSystem='Sol', BodyName='Earth')
        w.latest_status_snapshot = status
        candidate = MapperState()
        candidate.process_status(status)
        candidate.pml_id = '6'
        candidate.pml_center_lat, candidate.pml_center_lon = 38, -9
        # Loading also depends on the square-bracket PML version naming scheme.
        path = Path(self.temp.name) / 'Earth [6].json'
        candidate.save(path)

        with patch.object(w, 'nearby_pml_maps', return_value=[(0, path, candidate)]), \
                patch.object(w, 'prepare_loaded_map', return_value=(candidate, path)) as prepare:
            prepared = w.prepare_pending_destination()

        self.assertIs(prepared['state'], candidate)
        self.assertEqual(prepared['path'], path)
        prepare.assert_called_once_with(candidate, path)
        self.assertIs(w.state, old_state)

    def test_pending_destination_activation_clears_state_after_success(self):
        from mapper_core import MapperState

        w = self.window
        old_state = w.state
        status = dict(Flags=0x04000000, Latitude=38, Longitude=-9,
                      Heading=12, StarSystem='Sol', BodyName='Earth')
        candidate = MapperState()
        candidate.process_status(status)
        candidate.pml_id = '6'
        candidate.pml_center_lat, candidate.pml_center_lon = 38, -9
        w.transition_required = True
        w.pending_old_map_resolved = True
        w.latest_status_snapshot = status
        w.pending_destination = dict(state=candidate, path=None,
                                     source_text='preparado')

        self.assertTrue(w.activate_pending_destination())
        self.assertIs(w.state, candidate)
        self.assertIsNot(w.state, old_state)
        self.assertFalse(w.transition_required)
        self.assertIsNone(w.pending_status_update)
        self.assertIsNone(w.pending_status_snapshot)
        self.assertIsNone(w.latest_status_snapshot)
        self.assertFalse(w.pending_old_map_resolved)
        self.assertIsNone(w.pending_destination)
        self.assertEqual((w.state.rhino_lat, w.state.rhino_lon), (38, -9))

    def test_stale_prepared_destination_remains_pending_without_installing(self):
        from mapper_core import MapperState

        w = self.window
        old_state = w.state
        status = dict(Flags=0x04000000, Latitude=38.2, Longitude=-9,
                      Heading=12, StarSystem='Sol', BodyName='Earth')
        candidate = MapperState()
        candidate.process_status(dict(Flags=0x04000000, Latitude=38,
                                      Longitude=-9, Heading=0,
                                      StarSystem='Sol', BodyName='Earth'))
        candidate.pml_id = '6'
        candidate.pml_center_lat, candidate.pml_center_lon = 38, -9
        w.transition_required = True
        w.pending_old_map_resolved = True
        w.latest_status_snapshot = status
        w.pending_destination = dict(state=candidate, path=None,
                                     source_text='preparado')

        self.assertFalse(w.activate_pending_destination())
        self.assertIs(w.state, old_state)
        self.assertTrue(w.transition_required)
        self.assertTrue(w.pending_old_map_resolved)
        self.assertIsNone(w.pending_destination)

    def test_activation_failure_preserves_pending_lifecycle_state(self):
        from mapper_core import MapperState

        w = self.window
        candidate = MapperState()
        candidate.process_status(dict(Flags=0x04000000, Latitude=38,
                                      Longitude=-9, Heading=0,
                                      StarSystem='Sol', BodyName='Earth'))
        candidate.pml_id = '6'
        candidate.pml_center_lat, candidate.pml_center_lon = 38, -9
        w.transition_required = True
        w.pending_old_map_resolved = True
        w.latest_status_snapshot = dict(
            Flags=0x04000000, Latitude=38, Longitude=-9,
            StarSystem='Sol', BodyName='Earth')
        w.pending_destination = dict(state=candidate, path=None,
                                     source_text='preparado')

        with patch.object(w, 'install_prepared_map', side_effect=OSError('falha')), \
                patch('rhino_surface_mapper_qt.QMessageBox.critical'):
            self.assertFalse(w.activate_pending_destination())

        self.assertTrue(w.transition_required)
        self.assertTrue(w.pending_old_map_resolved)
        self.assertIsNotNone(w.pending_destination)

    def test_post_commit_activation_failure_rolls_back_live_state(self):
        from mapper_core import MapperState

        w = self.window
        old_state = w.state
        candidate = MapperState()
        candidate.process_status(dict(Flags=0x04000000, Latitude=38,
                                      Longitude=-9, Heading=0,
                                      StarSystem='Sol', BodyName='Earth'))
        candidate.pml_id = '6'
        candidate.pml_center_lat, candidate.pml_center_lon = 38, -9
        w.transition_required = True
        w.pending_old_map_resolved = True
        w.latest_status_snapshot = dict(
            Flags=0x04000000, Latitude=38, Longitude=-9,
            StarSystem='Sol', BodyName='Earth')
        w.pending_destination = dict(state=candidate, path=None,
                                     source_text='preparado')

        with patch.object(w.info_left, 'setText',
                          side_effect=[RuntimeError('falha pós-commit'), None]):
            with self.assertRaises(RuntimeError):
                w.activate_pending_destination()

        self.assertIs(w.state, old_state)
        self.assertIs(w.view.state, old_state)
        self.assertTrue(w.transition_required)
        self.assertTrue(w.pending_old_map_resolved)
        self.assertIs(w.pending_destination['state'], candidate)
        self.assertTrue(w.transition_required)

        self.assertTrue(w.activate_pending_destination())
        self.assertIs(w.state, candidate)
        self.assertFalse(w.transition_required)
    def test_alter_mark_cancel_and_name_only_preserve_position(self):
        w = self.window
        with patch.object(w, 'edit_mark_values', return_value=dict(name='A', azimuth=37, distance=1234.56)):
            w.mark()
        item = w.state.marks[0]
        original = dict(item)
        with patch.object(w, 'edit_mark_values', return_value=None):
            w.alter_mark(item)
        self.assertEqual(item, original)
        def rename(existing):
            return dict(existing, name='B')
        with patch.object(w, 'edit_mark_values', side_effect=rename):
            w.alter_mark(item)
        self.assertEqual(item, dict(original, name='B'))

    def test_real_mark_dialog_preserves_fractional_and_long_distances(self):
        from qt_map_operations import MarkDialog
        w = self.window
        for distance in (1234.56, 123456.78):
            with patch.object(w, 'edit_mark_values', return_value=dict(name='A', azimuth=37, distance=distance)):
                w.mark()
            item = w.state.marks[-1]
            original = dict(item)
            def rename(existing):
                dialog = MarkDialog(w, existing)
                dialog.name.setText('B')
                result = dialog.values()
                dialog.close()
                return result
            with patch.object(w, 'edit_mark_values', side_effect=rename):
                w.alter_mark(item)
            self.assertEqual(item, dict(original, name='B'))
            def move(existing):
                dialog = MarkDialog(w, existing)
                dialog.distance.setValue(500)
                dialog.azimuth.setValue(90)
                result = dialog.values()
                dialog.close()
                return result
            with patch.object(w, 'edit_mark_values', side_effect=move):
                w.alter_mark(item)
            self.assertNotEqual(item['x'], original['x'])
