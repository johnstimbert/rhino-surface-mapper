"""Behavioural tests for marker navigation and search-pause overlays.

These tests specify deterministic Qt-free navigation state used by the overlay
while the PySide6 interface starts, pauses, and resumes search routes.
"""

import math
import sys
import unittest
from pathlib import Path

APP_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(APP_DIR))

from mapper_core import MapperState, SRV_FLAG


def sample_status(**changes):
    base = {
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


class NavigationMarkerTests(unittest.TestCase):
    """Covers active marker targets and pause-return semantics from a fresh state."""

    def setUp(self):
        self.state = MapperState()
        self.state.process_status(sample_status())

    def test_overlay_allowed_when_navigating_to_target_without_search(self):
        self.assertFalse(self.state.overlay_allowed())
        self.state.active_nav_target = {
            "type": "marks",
            "name": "[Marca] Teste",
            "x": 0.0,
            "y": 500.0,
        }
        self.assertTrue(self.state.overlay_allowed())
        self.state.active_nav_target = None
        self.assertFalse(self.state.overlay_allowed())

    def test_overlay_navigation_calculates_bearing_distance_and_target_name(self):
        # The target is 1000 m north (Y = +1000) from the Rhino at (0, 0).
        self.state.active_nav_target = {
            "type": "marks",
            "name": "[Marca] Alfa",
            "x": 0.0,
            "y": 1000.0,
        }
        heading, distance, color, dist_color, target_name = self.state.overlay_navigation()
        self.assertEqual(heading, "000°")
        self.assertEqual(distance, "1000 m")
        self.assertEqual(color, "#00cc44")
        self.assertEqual(target_name, "[Marca] Alfa")

    def test_search_pauses_when_navigating_and_stores_pause_point(self):
        self.state.start_search(0)
        self.assertTrue(self.state.search_started)
        self.assertFalse(self.state.search_paused)
        self.assertIsNone(self.state.search_pause_point)

        # Simulate starting marker navigation while preserving the search pause point.
        rhino_x, rhino_y = self.state.llxy(self.state.rhino_lat, self.state.rhino_lon)
        self.state.search_paused = True
        self.state.search_pause_point = (rhino_x, rhino_y)
        self.state.active_nav_target = {
            "type": "marks",
            "name": "[Marca] Mina",
            "x": 500.0,
            "y": 500.0,
        }

        self.assertTrue(self.state.search_paused)
        self.assertEqual(self.state.search_pause_point, (rhino_x, rhino_y))

        # The overlay must point to the marker instead of the paused search route.
        _, _, _, _, name = self.state.overlay_navigation()
        self.assertEqual(name, "[Marca] Mina")

    def test_arrival_within_100m_transitions_to_return_to_pause(self):
        pause_xy = (0.0, 0.0)
        mark_target = {"type": "marks", "name": "[Marca] Destino", "x": 50.0, "y": 50.0}
        self.state.search_started = True
        self.state.search_paused = True
        self.state.search_pause_point = pause_xy
        self.state.active_nav_target = mark_target

        # At 50 m from the target, the Rhino is inside the 100 m arrival radius.
        self.state.rhino_lat, self.state.rhino_lon = self.state.xyll(50.0, 50.0)
        self.state.overlay_navigation()

        # Arrival clears the marker and starts return navigation to the pause point.
        self.assertIsNone(self.state.active_nav_target)
        self.assertTrue(self.state.return_to_pause)

        # The next overlay call should aim at the recorded pause point.
        _, _, _, _, name = self.state.overlay_navigation()
        self.assertEqual(name, "Ponto de Pausa ⏸")

    def test_arrival_at_pause_point_resumes_search(self):
        self.state.start_search(0)
        self.state.search_paused = True
        self.state.return_to_pause = True
        self.state.search_pause_point = (0.0, 0.0)

        # The Rhino reaches the pause point at (0, 0).
        self.state.rhino_lat, self.state.rhino_lon = self.state.xyll(0.0, 0.0)
        self.state.overlay_navigation()

        # Reaching the pause point exits pause mode and resumes the search route.
        self.assertFalse(self.state.return_to_pause)
        self.assertFalse(self.state.search_paused)
        self.assertIsNone(self.state.search_pause_point)

        _, _, _, _, name = self.state.overlay_navigation()
        self.assertTrue(name.startswith("Busca: Ponto"))

    def test_overlay_disallowed_when_commander_exits_rhino(self):
        self.state.start_search(0)
        self.assertTrue(self.state.overlay_allowed())

        # The commander leaves the Rhino when Flags no longer include SRV_FLAG.
        self.state.process_status(sample_status(Flags=0))
        self.assertFalse(self.state.in_srv)
        self.assertFalse(self.state.overlay_allowed())

        # Re-entering the Rhino restores overlay eligibility.
        self.state.process_status(sample_status())
        self.assertTrue(self.state.in_srv)
        self.assertTrue(self.state.overlay_allowed())


if __name__ == "__main__":
    unittest.main()
