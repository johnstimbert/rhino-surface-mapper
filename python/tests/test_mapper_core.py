"""Behavioural tests for the headless mapper core.

These tests specify deterministic Qt-free state transitions, persistence, navigation,
and telemetry handling that must remain stable outside the PySide6 interface.
"""

import math
import copy
import sys
import unittest
from pathlib import Path


APP_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(APP_DIR))

from mapper_core import MapperState, SRV_FLAG


def status(**changes):
    base = {
        # SRV_FLAG keeps sample telemetry in rover mode so the core accepts it.
        "Flags": SRV_FLAG,
        "Latitude": 38.0,
        "Longitude": -9.0,
        "Heading": 0.0,
        "PlanetRadius": 6_371_000.0,
        "StarSystem": "Teste",
        "BodyName": "A 1",
        "Fuel": {"FuelReservoir": 0.8},
    }
    base.update(changes)
    return base


class MapperCoreTests(unittest.TestCase):
    """Covers core map state, search routes, telemetry, and persistence invariants."""

    def test_search_azimuth_rotates_route_and_survives_reload(self):
        import tempfile
        for azimuth in (0, 90, 180, 270, 359):
            with self.subTest(azimuth=azimuth):
                state = MapperState()
                state.process_status(status())
                state.start_search(azimuth)
                angle = math.radians(azimuth)
                self.assertAlmostEqual(state.next_target_xy[0], 3500 * math.sin(angle))
                self.assertAlmostEqual(state.next_target_xy[1], 3500 * math.cos(angle))
                state.rhino_lat, state.rhino_lon = state.xyll(*state.next_target_xy)
                state.update_next()
                angle += 2 * math.pi / state.search_total_points
                self.assertAlmostEqual(state.next_target_xy[0], 3500 * math.sin(angle))
                self.assertAlmostEqual(state.next_target_xy[1], 3500 * math.cos(angle))
                with tempfile.TemporaryDirectory() as directory:
                    path = Path(directory) / 'map.json'
                    state.save(path)
                    loaded = MapperState()
                    loaded.load(path)
                    self.assertEqual(loaded.search_azimuth, azimuth)
                    self.assertEqual(loaded.next_target_xy, state.next_target_xy)

    def test_search_rejects_invalid_azimuth(self):
        state = MapperState()
        # Azimuth must be an exact int; bool is rejected despite being an int subclass.
        for value in (-1, 360, 1.5, '90', True):
            with self.assertRaises(ValueError):
                state.start_search(value)

    def test_status_creates_a_position_and_normalizes_heading(self):
        state = MapperState()
        self.assertTrue(state.process_status(status(Heading=361)))
        self.assertEqual(state.rhino_heading, 1.0)
        self.assertEqual(len(state.points), 1)
        self.assertEqual(state.fuel_percent, 100.0)

    def test_status_ignores_non_srv_telemetry(self):
        state = MapperState()
        self.assertFalse(state.process_status(status(Flags=0)))
        self.assertEqual(state.points, [])

    def test_status_reports_body_change_without_resetting_active_map(self):
        state = MapperState()
        state.process_status(status())
        state.pml_id = '6'
        state.points.append({'x': 12, 'y': 34, 'lat': 38, 'lon': -9})
        before = copy.deepcopy((state.system, state.body, state.pml_id, state.points,
                                state.deposits, state.rigs, state.marks,
                                state.route_history, state.radar_coverage))
        result = state.process_status(status(BodyName='A 2', Latitude=39.0, Longitude=-8.0))

        self.assertTrue(result)
        self.assertTrue(result.location_changed)
        self.assertEqual((result.system, result.body, result.latitude, result.longitude),
                         ('Teste', 'A 2', 39.0, -8.0))
        after = (state.system, state.body, state.pml_id, state.points,
                 state.deposits, state.rigs, state.marks,
                 state.route_history, state.radar_coverage)
        self.assertEqual(after, before)

    def test_status_reports_system_change_without_resetting_active_map(self):
        state = MapperState()
        state.process_status(status())
        state.pml_id = 'JD1'
        before = copy.deepcopy((state.system, state.body, state.pml_id, state.points,
                                state.deposits, state.rigs, state.marks,
                                state.route_history, state.radar_coverage))
        result = state.process_status(status(StarSystem='Outro'))

        self.assertTrue(result.location_changed)
        self.assertEqual((result.system, result.body), ('Outro', 'A 1'))
        after = (state.system, state.body, state.pml_id, state.points,
                 state.deposits, state.rigs, state.marks,
                 state.route_history, state.radar_coverage)
        self.assertEqual(after, before)

    def test_non_srv_status_preserves_active_map(self):
        state = MapperState()
        state.process_status(status())
        state.pml_id = '6'
        result = state.process_status(status(Flags=0))

        self.assertFalse(result)
        self.assertFalse(result.location_changed)
        self.assertEqual((state.system, state.body, state.pml_id), ('Teste', 'A 1', '6'))

    def test_changed_location_can_be_processed_without_mutating_position_or_trail(self):
        state = MapperState()
        state.process_status(status())
        old_position = (state.rhino_lat, state.rhino_lon)
        old_points = copy.deepcopy(state.points)

        result = state.process_status(status(Latitude=39.0, Longitude=-8.0),
                                      record_position=False)

        self.assertTrue(result)
        self.assertEqual((state.rhino_lat, state.rhino_lon), old_position)
        self.assertEqual(state.points, old_points)

    def test_missing_system_keeps_the_known_system_on_the_same_planet(self):
        """Missing StarSystem telemetry must not clear a known same-planet map."""
        state = MapperState()
        state.process_status(status())
        state.marks.append({'name': 'Centro [6]', 'x': 0, 'y': 0,
                            'lat': 38.0, 'lon': -9.0})
        state.process_status(status(StarSystem='', Latitude=38.001))
        self.assertEqual(state.system, 'Teste')
        self.assertEqual(state.body, 'A 1')
        self.assertEqual(len(state.marks), 1)

    def test_late_system_completes_same_body_identity_without_resetting_map(self):
        state = MapperState()
        state.process_status(status(StarSystem=''))
        state.pml_id = '6'
        state.deposits.append({'name': 'A', 'size': 'Grande', 'rigs': 3})
        state.marks.append({'name': 'Keep'})
        before = copy.deepcopy((state.points, state.deposits, state.marks, state.pml_id))

        result = state.process_status(status())

        self.assertTrue(result)
        self.assertFalse(result.location_changed)
        self.assertEqual((state.system, state.body, state.body_key),
                         ('Teste', 'A 1', 'Teste|A 1'))
        self.assertEqual((state.points, state.deposits, state.marks, state.pml_id), before)

    def test_search_starts_north_of_datum_and_can_skip(self):
        state = MapperState()
        state.process_status(status())
        self.assertTrue(state.start_search())
        target_x, target_y = state.next_target_xy
        self.assertAlmostEqual(target_x, 0.0, places=6)
        self.assertAlmostEqual(target_y, 3_500.0, places=6)
        self.assertTrue(state.skip_next())
        self.assertEqual(state.route_history[-1]["status"], "skipped")
        self.assertEqual(state.route_history[-1]["number"], 1)

    def test_overlay_navigation_matches_heading_thresholds(self):
        state = MapperState()
        state.process_status(status())
        state.start_search()
        self.assertEqual(state.overlay_navigation()[:3], ("000°", "3500 m", "#00cc44"))
        state.rhino_heading = 358.0
        self.assertEqual(state.overlay_navigation()[:3], ("000°", "3500 m", "#00cc44"))

    def test_overlay_colour_boundaries_and_arrow_counts(self):
        state=MapperState()
        state.process_status(status())
        state.start_search()
        for deviation,colour,count in ((0,'#00cc44',0),(2,'#00cc44',0),
                (2.1,'#ffd21c',3),(8,'#ffd21c',3),(8.1,'#ff3030',5),(179,'#ff3030',5)):
            for side in (-1,1):
                state.rhino_heading=(-side*deviation)%360
                heading,_,actual,_,_=state.overlay_navigation()
                self.assertEqual(actual,colour)
                self.assertEqual(heading.count('»' if side>0 else '<'),count)
        state.search_started=False
        self.assertEqual(state.overlay_navigation()[2],'#888888')

    def test_overlay_is_inactive_when_heading_is_missing(self):
        state = MapperState()
        state.process_status(status())
        state.start_search()
        state.rhino_heading = None

        self.assertEqual(
            state.overlay_navigation(),
            ("—", "—", "#888888", "white", "Busca: Ponto 1"),
        )

    def test_completed_route_stays_completed_after_repeated_updates(self):
        state = MapperState()
        state.process_status(status())
        state.start_search()

        for _ in range(state.search_total_points):
            state.rhino_lat, state.rhino_lon = state.xyll(*state.next_target_xy)
            state.update_next()

        history = list(state.route_history)
        self.assertIsNone(state.next_target_xy)
        self.assertEqual(state.route_index, state.search_total_points)

        state.update_next()
        state.update_next()

        self.assertIsNone(state.next_target_xy)
        self.assertEqual(state.route_index, state.search_total_points)
        self.assertEqual(state.route_history, history)

    def test_coordinate_conversion_round_trip(self):
        state = MapperState()
        state.process_status(status())
        x, y = state.llxy(38.01, -8.98)
        lat, lon = state.xyll(x, y)
        self.assertTrue(math.isclose(lat, 38.01, abs_tol=1e-10))
        self.assertTrue(math.isclose(lon, -8.98, abs_tol=1e-10))

    def test_pml_metadata_is_saved_loaded_and_reset_with_a_new_map(self):
        """PML metadata belongs to one map and must not leak into a new planet."""
        import tempfile
        state = MapperState()
        state.process_status(status())
        state.pml_id = '6'
        state.pml_center_lat, state.pml_center_lon = 38.01, -9.02
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'map.json'
            state.save(path)
            loaded = MapperState()
            loaded.load(path)
        self.assertEqual(loaded.pml_id, '6')
        self.assertEqual((loaded.pml_center_lat, loaded.pml_center_lon), (38.01, -9.02))
        loaded.new_map()
        self.assertEqual(loaded.pml_id, '')
        self.assertIsNone(loaded.pml_center_lat)

    def test_new_map_can_keep_the_current_pml(self):
        """The New action can preserve the current PML without marking it legacy."""
        state = MapperState()
        state.process_status(status())
        state.pml_id = '6'
        state.pml_center_lat, state.pml_center_lon = 38.01, -9.02
        state.new_map(keep_pml=True)
        self.assertEqual(state.pml_id, '6')
        self.assertEqual((state.pml_center_lat, state.pml_center_lon), (38.01, -9.02))

    def test_old_map_can_record_dates_from_its_file_properties(self):
        """Migration must fill missing dates without inventing a new save time."""
        import tempfile
        state = MapperState()
        state.process_status(status())
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'old.json'
            state.save(path)
            loaded = MapperState()
            loaded.load(path)
            self.assertIsNone(loaded.created_at)
            self.assertTrue(loaded.populate_missing_timestamps(path))
            original_saved_at = loaded.last_saved_at
            loaded.save(path, update_saved_at=False)
            reopened = MapperState()
            reopened.load(path)
        self.assertIsNotNone(reopened.created_at)
        self.assertEqual(reopened.last_saved_at, original_saved_at)


if __name__ == "__main__":
    unittest.main()
