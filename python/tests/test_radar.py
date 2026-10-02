"""Behavioural tests for radar pulses and input binding discovery.

These tests specify deterministic Qt-free radar coverage, persistence, and bind
parsing rules so the UI can remain a thin consumer of stable core behaviour.
"""

import json
import tempfile
import unittest
from pathlib import Path
from mapper_core import MapperState
from radar import RadarPulse
from radar_input import read_bindings, virtual_key


class RadarTests(unittest.TestCase):
    """Covers radar wave lifecycle, persisted coverage, and active SRV binds."""

    def state(self):
        s = MapperState()
        # 0x04000000 is the Elite Dangerous SRV flag required to accept telemetry.
        s.process_status(dict(Flags=0x04000000, Latitude=0, Longitude=0,
                              BodyName='Test', PlanetRadius=6000000))
        return s

    def test_speed_hold_repeat_new_origin_and_release(self):
        s, radar = self.state(), RadarPulse()
        radar.tick(s, 0, True, False, 'map')
        radar.tick(s, .1, True, True, 'map')
        radar.tick(s, 1.1, True, True, 'map')
        # Radar waves expand at 2000/3 m/s, so one second reaches this radius.
        self.assertAlmostEqual(s.radar_coverage[0]['radius'], 2000/3)
        s.rhino_lon = .01
        radar.tick(s, 3.1, True, True, 'map')
        self.assertEqual(len(s.radar_coverage), 2)
        self.assertEqual(s.radar_coverage[0]['x'], 0)
        self.assertGreater(s.radar_coverage[1]['x'], 0)
        radar.tick(s, 6.1, True, False, 'map')
        self.assertEqual(len(s.radar_coverage), 2)
        self.assertIsNone(radar.active)

    def test_focus_loss_and_map_change_require_release(self):
        s, radar = self.state(), RadarPulse()
        radar.tick(s, 0, True, True, 'map')
        self.assertFalse(s.radar_coverage)
        radar.tick(s, 1, True, False, 'map')
        radar.tick(s, 2, True, True, 'map')
        radar.tick(s, 4, False, True, 'map')
        radar.tick(s, 5, True, True, 'map')
        self.assertEqual(len(s.radar_coverage), 1)
        s.new_map()
        radar.tick(s, 6, True, True, 'new')
        self.assertFalse(s.radar_coverage)

    def test_refocus_while_held_cannot_repeat_finishing_wave(self):
        s, radar = self.state(), RadarPulse()
        radar.tick(s, 0, True, False, 'map')
        radar.tick(s, .1, True, True, 'map')
        radar.tick(s, 1, False, None, 'map')
        radar.tick(s, 4.1, True, True, 'map')
        self.assertEqual(len(s.radar_coverage), 1)
        self.assertIsNone(radar.active)

    def test_clicks_during_wave_are_ignored_and_not_queued(self):
        s, radar = self.state(), RadarPulse()
        for t, down in [(0, False), (.1, True), (.2, False), (.3, True)]:
            radar.tick(s, t, True, down, 'map')
        self.assertEqual(len(s.radar_coverage), 1)
        radar.tick(s, 1, True, False, 'map')
        self.assertAlmostEqual(s.radar_coverage[0]['radius'], 600)
        radar.tick(s, 3.1, True, False, 'map')
        self.assertEqual(len(s.radar_coverage), 1)
        self.assertIsNone(radar.active)
        radar.tick(s, 3.2, True, True, 'map')
        self.assertEqual(len(s.radar_coverage), 2)

    def test_release_observed_while_waiting_for_telemetry(self):
        s, radar = self.state(), RadarPulse()
        radar.tick(s, 0, False, False, 'map')
        radar.tick(s, .1, True, True, 'map')
        self.assertEqual(len(s.radar_coverage), 1)

    def test_persistence_and_invalid_coverage(self):
        s = self.state()
        s.radar_coverage = [dict(x=12, y=34, radius=750)]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)/'map.json'
            s.save(path)
            loaded = MapperState()
            loaded.load(path)
            self.assertEqual(loaded.radar_coverage, s.radar_coverage)
            data = json.loads(path.read_text(encoding='utf-8'))
            data['radar_coverage'][0]['radius'] = -1
            path.write_text(json.dumps(data), encoding='utf-8')
            with self.assertRaises(ValueError):
                loaded.load(path)
            self.assertEqual(loaded.radar_coverage, s.radar_coverage)
            del data['radar_coverage']
            path.write_text(json.dumps(data), encoding='utf-8')
            loaded.load(path)
            self.assertEqual(loaded.radar_coverage, [])

    def test_active_srv_profile_and_modifiers(self):
        with tempfile.TemporaryDirectory() as folder:
            folder = Path(folder)
            (folder/'StartPreset.4.start').write_text('Ship\nShip\nRover\nFoot')
            for version in (1, 2):
                (folder/f'Rover.4.{version}.binds').write_text(f'''<Root PresetName="Rover" MajorVersion="4" MinorVersion="{version}">
                    <BuggyPrimaryFireButton><Primary Device="Mouse" Key="Mouse_1"/>
                    <Secondary Device="Keyboard" Key="Key_R"><Modifier Device="Keyboard" Key="Key_LeftControl"/></Secondary>
                    </BuggyPrimaryFireButton></Root>''')
            path, bindings = read_bindings(folder)
            self.assertEqual(path.name, 'Rover.4.2.binds')
            self.assertEqual(bindings[1], [('Keyboard','Key_R'),('Keyboard','Key_LeftControl')])
            self.assertEqual(virtual_key('Mouse','Mouse_1'), 1)
            self.assertIsNone(virtual_key('Mouse','Mouse_WheelUp'))
