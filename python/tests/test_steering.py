"""Exercise steering decisions and simulated input without touching the game.

These tests protect timing thresholds, command bounds, manual-input detection, and
window integration so assistance remains predictable and safe.
"""
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import Mock
# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM','offscreen')
from steering import SteeringAssist, angle_delta
from steering import STATUS_CORRECTING, STATUS_SLOWING, STATUS_WAITING


class SteeringTests(unittest.TestCase):
    """Cover pure steering decisions with deterministic telemetry samples."""
    def ready(self, heading=0, movement=(0,2)):
        assist = SteeringAssist()
        assist.observe(0,0,0,heading)
        assist.start()
        assist.observe(1,*movement,heading)
        return assist

    def test_north_wrap_and_direction(self):
        """Heading deltas wrap across north and choose the shortest turn."""
        self.assertEqual(angle_delta(359,1),2)
        self.assertEqual(angle_delta(1,359),-2)
        self.assertEqual(self.ready().decide(1,(100,1000))[0],1)
        self.assertEqual(self.ready().decide(1,(-100,1000))[0],-1)

    def test_heading_only_update_does_not_inflate_speed(self):
        a=SteeringAssist()
        a.observe(0,0,0,0)
        a.start()
        a.observe(1,0,0,1)
        self.assertIsNone(a.decide(1,(100,1000)))
        self.assertEqual(a.overlay_text(),'Waiting for a new position')
        # Speed uses the last position delta, not the last heading-only update.
        a.observe(2,0,6,1)
        self.assertIsNotNone(a.decide(2,(100,1000)))
        self.assertEqual(a.speed,3)
        a.observe(3,0,6,2)
        self.assertIsNotNone(a.decide(3,(100,1000)))
        self.assertEqual(a.speed,3)

    def test_fresh_slow_position_after_long_interval_can_correct(self):
        a=SteeringAssist()
        a.observe(0,0,0,0)
        a.start()
        a.observe(4,0,2,0)
        self.assertIsNotNone(a.decide(4,(100,1000)))
        self.assertEqual(a.speed,.5)
        # Samples older than 2.5 s are stale even after a valid slow correction.
        self.assertIsNone(a.decide(7,(100,1000)))
        self.assertEqual(a.overlay_text(),'Waiting for fresh telemetry')

    def test_large_turn_displays_required_limit_and_recovers(self):
        a=self.ready(movement=(0,16))
        self.assertIsNone(a.decide(1,(1000,2)))
        self.assertEqual(a.overlay_text(),'Reduce speed · up to 15.0 m/s')
        a.observe(2,0,30,0)
        self.assertIsNotNone(a.decide(2,(1000,2)))
        self.assertEqual(a.overlay_text(),'Correcting right')

    def test_correction_overlay_does_not_parse_message_wording(self):
        a=self.ready(movement=(0,10))
        self.assertIsNotNone(a.decide(1,(1000,10)))
        self.assertEqual(a.status, STATUS_CORRECTING)
        a.message='Translated correction text'
        self.assertEqual(a.overlay_text(),'Correcting right')

    def test_slowing_overlay_does_not_depend_on_message_wording(self):
        a=self.ready(movement=(0,16))
        self.assertIsNone(a.decide(1,(100,1000)))
        self.assertEqual(a.status, STATUS_SLOWING)
        a.message='Translated speed warning'
        self.assertEqual(a.overlay_text(),'Reduce speed · up to 15.0 m/s')

    def test_waiting_state_does_not_depend_on_message_wording(self):
        a=SteeringAssist()
        a.start()
        a.wait('Translated text')
        self.assertEqual(a.status, STATUS_WAITING)
        self.assertEqual(a.overlay_text(),'Translated text')

    def test_no_repeated_commands_from_same_sample(self):
        assist=self.ready()
        command=assist.decide(1,(100,1000))
        self.assertGreater(command[1],0)
        self.assertLessEqual(command[1],.8)
        # The 0.25 s cooldown prevents repeated commands from one telemetry sample.
        for t in (1.2,1.5,2,3):
            assist.observe(t,0,2,0)
            self.assertIsNone(assist.decide(t,(100,1000)))

    def test_fifteen_metres_per_second_can_turn_without_hidden_slow_limit(self):
        slow=self.ready(movement=(0,3)).decide(1,(1000,15))
        fast=self.ready(movement=(0,15))
        command=fast.decide(1,(1000,15))
        self.assertIsNotNone(command)
        self.assertEqual(command[0],1)
        self.assertLess(command[1],slow[1])
        self.assertIsNone(fast.decide(1.5,(1000,15)))

    def test_waits_for_sample_after_turn_and_settling(self):
        a=self.ready(movement=(0,10))
        self.assertAlmostEqual(a.decide(1,(1000,10))[1],1.2)
        a.observe(1.5,0,15,0)
        self.assertIsNone(a.decide(1.5,(1000,10)))
        self.assertIn('correcting',a.message)
        self.assertIsNone(a.decide(2.5,(1000,10)))
        self.assertEqual(a.overlay_text(),'Waiting for steering response')
        a.observe(2.6,0,26,0)
        self.assertIsNotNone(a.decide(2.6,(1000,10)))

    def test_turn_aggression_bands(self):
        """Sharper turns scale pulses through the documented aggression bands."""
        import math
        # Aggression bands use 1/1.5/2/3 multipliers as turns become sharper.
        for angle,factor in ((20,1),(60,1.5),(100,2),(150,3)):
            a=self.ready(movement=(0,10))
            target=(1000*math.sin(math.radians(angle)),10+1000*math.cos(math.radians(angle)))
            command=a.decide(1,target)
            self.assertAlmostEqual(command[1],min(.8,angle/46.5*.7)*factor)

    def test_configured_higher_speed_limit_is_respected(self):
        a=self.ready(movement=(0,20))
        a.max_speed=25
        self.assertIsNotNone(a.decide(1,(1000,20)))
        self.assertEqual(a.speed_limit,25)

    def test_excess_speed_warns_for_any_turn(self):
        for movement,target in [((0,16),(100,1000)),((0,16),(1000,2))]:
            a=self.ready(movement=movement)
            self.assertIsNone(a.decide(1,target))
            self.assertEqual(a.message,'Reduce speed')

    def test_stale_stationary_reverse_and_irregular_samples(self):
        # Stale, stationary, reversing, and sub-0.3 m/s samples must all wait.
        for t,movement in [(31,(0,2)),(1,(0,0)),(1,(0,-2)),(.1,(0,2))]:
            a=SteeringAssist()
            a.observe(0,0,0,0)
            a.start()
            a.observe(t,*movement,0)
            self.assertIsNone(a.decide(t,(100,1000)))
        a=self.ready()
        self.assertIsNone(a.decide(5,(100,1000)))

    def test_configurable_tolerance_independent_of_overlay(self):
        a=self.ready()
        a.tolerance=8
        self.assertIsNone(a.decide(1,(100,1000)))
        self.assertEqual(a.message,'Assistance: on course')
        b=self.ready()
        b.tolerance=3
        self.assertIsNotNone(b.decide(1,(100,1000)))

    def test_tolerance_and_anticipation(self):
        self.assertIsNone(self.ready().decide(1,(0,1000)))
        a=SteeringAssist()
        a.observe(0,0,0,0)
        a.start()
        a.observe(1,0,2,10)
        self.assertIsNone(a.decide(1,(249,1000)))
        self.assertIn('Easing',a.message)

    def test_heading_jump_and_recovery(self):
        a=SteeringAssist()
        a.observe(0,0,0,0)
        a.start()
        a.observe(1,0,2,40)
        self.assertIsNone(a.decide(1,(100,1000)))
        # A 20 deg/s heading change or 100 m jump suppresses unstable telemetry.
        self.assertIn('stability',a.message)
        a.observe(2,1,3,40)
        self.assertIsNotNone(a.decide(2,(100,1000)))


class SteeringInputTests(unittest.TestCase):
    """Cover the input driver with mocked Windows and joystick APIs."""
    def driver(self):
        import threading
        from steering_input import SteeringInput
        driver=SteeringInput.__new__(SteeringInput)
        driver.lock=threading.RLock()
        driver.held=None
        driver.keys={-1:117,1:118}
        driver.radar=Mock()
        driver.radar.game_focused.return_value=True
        driver.user=Mock()
        driver.user.GetForegroundWindow.return_value=123
        driver.send=Mock()
        driver.error=''
        driver.sent_pulses=0
        return driver

    def test_commands_are_bounded_and_release_on_stop(self):
        d=self.driver()
        self.assertTrue(d.pulse(1,.06))
        self.assertEqual(d.sent_pulses,1)
        d.send.assert_called_once_with(118)
        d.pulse(-1,.06)
        self.assertEqual(d.send.call_count,1)
        d.release()
        d.send.assert_called_with(118,True)
        self.assertIsNone(d.held)
        d.radar.game_focused.return_value=False
        d.send.reset_mock()
        self.assertFalse(d.pulse(1,.06))
        self.assertEqual(d.sent_pulses,1)
        d.send.assert_not_called()

    def test_arrival_brake_uses_s_for_three_seconds(self):
        from unittest.mock import patch
        d=self.driver()
        # Arrival braking holds S for three seconds, then releases through the driver.
        with patch('steering_input.time.monotonic',return_value=10):
            self.assertTrue(d.brake())
        d.send.assert_called_once_with(0x53)
        # Long pulses are capped at the three-second safety deadline.
        self.assertEqual(d.deadline,13)
        d.release()
        d.send.assert_called_with(0x53,True)
        d.radar.game_focused.return_value=False
        self.assertFalse(d.brake())

    def test_long_pulse_reaches_driver_and_is_capped(self):
        from unittest.mock import patch
        d=self.driver()
        with patch('steering_input.time.monotonic',return_value=10):
            self.assertTrue(d.pulse(1,.8))
            self.assertAlmostEqual(d.deadline,10.8)
            d.release()
            self.assertTrue(d.pulse(-1,5))
            self.assertAlmostEqual(d.deadline,13)
        d.release()

    def test_watchdog_releases_without_qt_tick(self):
        import threading
        d=self.driver()
        released=threading.Event()
        d.done=threading.Event()
        d.send.side_effect=lambda vk,up=False: released.set() if up else None
        d.pulse(1,.02)
        thread=threading.Thread(target=d.watch_release)
        thread.start()
        try:
            self.assertTrue(released.wait(.5))
            self.assertIsNone(d.held)
        finally:
            d.done.set()
            thread.join(.5)

    def test_test_hold_renews_without_repeated_keydown_and_releases_on_focus_loss(self):
        from unittest.mock import patch
        d=self.driver()
        with patch('steering_input.time.monotonic',return_value=10):
            self.assertTrue(d.hold_test(-1,5))
        self.assertAlmostEqual(d.deadline,10.15)
        with patch('steering_input.time.monotonic',return_value=10.05):
            self.assertTrue(d.hold_test(-1,4.95))
        self.assertAlmostEqual(d.deadline,10.2)
        d.send.assert_called_once_with(117)
        d.radar.game_focused.return_value=False
        self.assertFalse(d.hold_test(-1,4))
        d.send.assert_called_with(117,True)
        self.assertIsNone(d.held)

    def test_injected_keys_are_not_manual_input(self):
        import ctypes as C
        from steering_input import HookData
        d=self.driver()
        d.monitored={117,118,27}
        d.physical=set()
        d.manual_pressed=False
        d.hook=None
        # Injected key events are our own pulses and must not count as manual input.
        injected=HookData(117,0,0x10,0,0)
        d.on_key(0,0x100,C.addressof(injected))
        self.assertFalse(d.manual_pressed)
        physical=HookData(117,0,0,0,0)
        d.on_key(0,0x100,C.addressof(physical))
        self.assertTrue(d.manual_pressed)
        self.assertIn(117,d.physical)
        d.on_key(0,0x101,C.addressof(physical))
        self.assertNotIn(117,d.physical)

    def test_failed_send_releases_and_records_error(self):
        d=self.driver()
        d.send.side_effect=[OSError('Falha simulada'),None]
        d.pulse(1,.06)
        self.assertEqual(d.error,'Falha simulada')
        self.assertEqual(d.sent_pulses,0)
        self.assertIsNone(d.held)
        d.send.assert_called_with(118,True)

    def test_rudder_override_and_device_loss_stop(self):
        import ctypes as C
        from steering_input import JoyInfo
        d=self.driver()
        d.manual_pressed=False
        d.physical=set()
        d.axis=(1,'r',0,65535)
        def sample(index,pointer):
            info=C.cast(pointer,C.POINTER(JoyInfo)).contents
            info.r=45000
            info.v=32767
            return 0
        d.radar.winmm.joyGetPosEx.side_effect=sample
        self.assertTrue(d.manual())
        d.radar.winmm.joyGetPosEx.side_effect=None
        d.radar.winmm.joyGetPosEx.return_value=1
        self.assertIsNone(d.manual())


class SteeringWindowTests(unittest.TestCase):
    """Cover MapperWindow assistance wiring with mocked steering input."""
    @classmethod
    def setUpClass(cls):
        from PySide6.QtWidgets import QApplication
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app=QApplication.instance() or QApplication([])

    def setUp(self):
        from rhino_surface_mapper_qt import MapperWindow
        self.temp=tempfile.TemporaryDirectory()
        self.path=Path(self.temp.name)/'Status.json'
        self.path.write_text(json.dumps(dict(Flags=0x04000000,Latitude=0,Longitude=0,Heading=0,BodyName='Test')))
        self.w=MapperWindow(self.path, game_running_check=lambda: True)
        self.w.direction_test=False
        self.w.turn_results_directory=Path(self.temp.name)
        # Stop timers so assistance updates occur only through explicit calls.
        for timer in (self.w.timer,self.w.radar_timer,self.w.assist_timer): timer.stop()
        self.w.steering_input.close()
        self.driver=Mock(keys={-1:117,1:118},message='OK',error='')
        self.driver.manual.return_value=False
        self.w.steering_input=self.driver
        self.w.radar_input.game_focused=lambda: True
        self.w.radar_input.available=False
        self.w.start_search()
        self.w.toggle_assistance()
        self.assertTrue(self.w.assist.enabled)

    def tearDown(self):
        self.w.close()
        self.temp.cleanup()

    def test_arrival_brakes_once_and_f8_cancels(self):
        self.w.state.navigation_arrivals=1
        self.w.update_assistance()
        self.driver.brake.assert_called_once()
        self.w.update_assistance()
        self.driver.brake.assert_called_once()
        self.assertGreater(self.w.braking_until,0)
        self.w.toggle_assistance()
        self.assertEqual(self.w.braking_until,0)
        self.driver.release.assert_called()

    def test_invalid_telemetry_waits_then_resumes_with_fresh_samples(self):
        from unittest.mock import patch
        self.w.status_valid=False
        self.w.update_assistance()
        self.assertTrue(self.w.assist.enabled)
        self.driver.release.assert_called()
        self.driver.pulse.assert_not_called()
        self.w.status_valid=True
        self.w.steering_target=lambda:(1000,10)
        # 16 m/s exceeds the default 15 m/s limit, so warning replaces commands.
        self.w.assist.observe(9,0,0,0)
        self.w.assist.observe(10,0,10,0)
        with patch('steering_ui.time.monotonic',return_value=10):
            self.w.update_assistance()
        self.driver.pulse.assert_called_once()

    def test_device_read_failure_waits_without_manual_shutdown(self):
        self.driver.manual.return_value=None
        self.w.update_assistance()
        self.assertTrue(self.w.assist.enabled)
        self.assertIn('joystick',self.w.overlay.assistance_notice)
        self.driver.pulse.assert_not_called()

    def test_options_save_integer_tolerance(self):
        from PySide6.QtWidgets import QSpinBox
        self.w.options_path=Path(self.temp.name)/'options.json'
        self.w.radar_options()
        field=self.w.options_panel.findChild(QSpinBox,'assist_tolerance_deg')
        self.assertEqual(field.value(),self.w.assist.tolerance)
        field.setValue(7)
        self.assertEqual(self.w.assist.tolerance,7)
        self.assertEqual(json.loads(self.w.options_path.read_text())['assist_tolerance_deg'],7)

    def test_manual_input_stops_and_releases(self):
        self.driver.manual.return_value=True
        self.w.update_assistance()
        self.assertFalse(self.w.assist.enabled)
        self.driver.release.assert_called()
        self.driver.pulse.assert_not_called()

    def test_requested_direction_cycle_without_target_or_speed_data(self):
        from unittest.mock import patch
        self.w.stop_assistance()
        self.w.direction_test=True
        self.w.state.search_started=False
        with patch('steering_ui.time.monotonic',return_value=100):
            self.w.toggle_assistance()
        self.w.refresh()
        self.assertTrue(self.w.overlay.isVisible())
        for seconds,direction,label in [(0,-1,'LEFT'),(1.9,-1,'LEFT'),
                (2,0,'FORWARD'),(6.9,0,'FORWARD'),(7,1,'RIGHT'),
                (8.9,1,'RIGHT'),(9,0,'FORWARD'),(13.9,0,'FORWARD'),(14,-1,'LEFT')]:
            with self.subTest(seconds=seconds):
                self.driver.reset_mock()
                with patch('steering_ui.time.monotonic',return_value=100+seconds):
                    self.w.update_assistance()
                self.assertIn(label,self.w.overlay.assistance_notice)
                if direction:
                    self.assertEqual(self.driver.hold_test.call_args.args[0],direction)
                else:
                    self.driver.hold_test.assert_not_called()
                    self.driver.release.assert_called()
                self.driver.pulse.assert_not_called()
        self.w.toggle_assistance()
        self.assertFalse(self.w.assist.enabled)
        self.driver.release.assert_called()

    def test_direction_cycle_waits_on_focus_loss(self):
        self.w.stop_assistance()
        self.w.direction_test=True
        self.w.toggle_assistance()
        self.driver.reset_mock()
        self.w.radar_input.game_focused=lambda:False
        self.w.update_assistance()
        self.assertTrue(self.w.assist.enabled)
        self.driver.release.assert_called()
        self.driver.hold_test.assert_not_called()

    def test_speed_warning_is_visible_in_overlay_without_commands(self):
        from unittest.mock import patch
        self.w.assist.max_speed=15
        self.w.assist.reset_samples()
        self.w.assist.observe(9,0,0,0)
        self.w.assist.observe(10,0,16,0)
        with patch('steering_ui.time.monotonic',return_value=10):
            self.w.update_assistance()
        self.assertEqual(self.w.overlay.assistance_notice,'Reduce speed · up to 15.0 m/s')
        self.assertTrue(self.w.overlay.assistance_warning)
        self.w.overlay.set_assistance_notice('Translated warning', warning=True)
        self.assertTrue(self.w.overlay.assistance_warning)
        self.driver.pulse.assert_not_called()
        self.driver.release.assert_called()

    def test_focus_loss_waits_but_does_not_hide_overlay(self):
        self.w.radar_input.game_focused=lambda: False
        self.w.update_assistance()
        self.w.refresh()
        self.assertTrue(self.w.assist.enabled)
        self.assertTrue(self.w.overlay.isVisible())
        self.driver.release.assert_called()

    def test_map_vehicle_target_and_panel_changes_stop(self):
        for change in ('map','vehicle','target','panel'):
            with self.subTest(change=change):
                self.w.state.in_srv=True
                self.w.state.search_started=True
                self.w.live_status['GuiFocus']=0
                self.w.state.update_next()
                self.w.toggle_assistance() if not self.w.assist.enabled else None
                if change=='map': self.w.state.map_generation+=1
                if change=='vehicle': self.w.state.in_srv=False
                if change=='target': self.w.state.search_started=False
                if change=='panel': self.w.live_status['GuiFocus']=1
                self.w.update_assistance()
                self.assertEqual(self.w.assist.enabled,change!='target')
                self.driver.pulse.assert_not_called()

    def test_pending_button_waits_for_focus_and_shutdown_releases(self):
        self.w.stop_assistance()
        self.w.radar_input.game_focused=lambda: False
        self.w.toggle_assistance()
        self.assertTrue(self.w.assist_pending)
        self.assertFalse(self.w.assist.enabled)
        self.w.radar_input.game_focused=lambda: True
        self.w.update_assistance()
        self.assertTrue(self.w.assist.enabled)
        self.w.close()
        self.driver.release.assert_called()
        self.driver.close.assert_called()
