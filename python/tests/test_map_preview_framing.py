"""Exercise MapPreview framing for routes, labels, deposits, and resizing.

These tests protect preview bounds and finite scaling so thumbnails include all
visible annotations without clipping at different widget sizes.
"""
import os
import unittest
from types import SimpleNamespace

# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtCore import QPointF, QRectF
from PySide6.QtGui import QFont, QFontMetricsF
from PySide6.QtWidgets import QApplication

from deposit_marker import deposit_bounds
from map_library import MapPreview


def state_with(**collections):
    return SimpleNamespace(points=collections.get('points', []),
                           deposits=collections.get('deposits', []),
                           rigs=collections.get('rigs', []),
                           marks=collections.get('marks', []))


def screen_point(item, framing, width, height):
    scale, cx, cy = framing
    return QPointF(width / 2 + (item['x'] - cx) * scale,
                   height / 2 - (item['y'] - cy) * scale)


class MapPreviewFramingTests(unittest.TestCase):
    """Cover preview geometry with shared offscreen font and widget dimensions."""
    @classmethod
    def setUpClass(cls):
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def setUp(self):
        self.font = QFont()
        self.width, self.height = 460, 300

    def assert_visual_rect_inside(self, rect, width=None, height=None):
        width = width or self.width
        height = height or self.height
        self.assertGreaterEqual(rect.left(), 0)
        self.assertGreaterEqual(rect.top(), 0)
        self.assertLessEqual(rect.right(), width)
        self.assertLessEqual(rect.bottom(), height)

    def test_route_only_geometry_has_finite_positive_scale(self):
        state = state_with(points=[{'x': -500, 'y': 0}, {'x': 500, 'y': 0}])
        scale, _, _ = MapPreview._framing(state, self.width, self.height, self.font)
        self.assertGreater(scale, 0)

    def test_deposit_card_at_world_edge_is_contained(self):
        item = {'x': 500, 'y': 0, 'name': 'Edge', 'size': 'Grande', 'rigs': 3}
        state = state_with(deposits=[item])
        framing = MapPreview._framing(state, self.width, self.height, self.font)
        point = screen_point(item, framing, self.width, self.height)
        # Deposit cards include labels, so framing must reserve their visual bounds.
        bounds = deposit_bounds(point, self.font, item).adjusted(-3, -3, 3, 3)
        self.assert_visual_rect_inside(bounds)

    def test_long_labels_affect_required_extent(self):
        short = state_with(marks=[{'x': 0, 'y': 0, 'name': 'A'}])
        # Long annotations widen the required preview extent and reduce scale.
        long = state_with(marks=[{'x': 0, 'y': 0, 'name': 'A' * 80}])
        short_scale = MapPreview._framing(short, 1000, 1000, self.font)[0]
        long_scale = MapPreview._framing(long, 1000, 1000, self.font)[0]
        self.assertLess(long_scale, short_scale)

    def test_mark_and_rig_labels_are_contained(self):
        mark = {'x': 500, 'y': 500, 'name': 'Long annotation'}
        rig = {'x': -500, 'y': -500, 'name': 'Long annotation'}
        state = state_with(marks=[mark], rigs=[rig])
        framing = MapPreview._framing(state, self.width, self.height, self.font)
        metrics = QFontMetricsF(self.font)
        for item in (mark, rig):
            point = screen_point(item, framing, self.width, self.height)
            rect = QRectF(point.x() - 4, point.y() - 4,
                          12 + metrics.horizontalAdvance(item['name']),
                          metrics.height() + 8).adjusted(-3, -3, 3, 3)
            self.assert_visual_rect_inside(rect)

    def test_mixed_content_and_resize_remain_finite(self):
        state = state_with(points=[{'x': -1000, 'y': -200}, {'x': 1000, 'y': 800}],
                           deposits=[{'x': -1000, 'y': -200, 'name': 'D'}],
                           marks=[{'x': 1000, 'y': 800, 'name': 'M'}],
                           rigs=[{'x': 0, 'y': 0, 'name': 'R'}])
        for width, height in ((460, 300), (900, 500), (100, 80)):
            scale, _, _ = MapPreview._framing(state, width, height, self.font)
            self.assertGreater(scale, 0)

    def test_asymmetric_annotations_shift_center(self):
        """Framing recentres asymmetric label extents instead of clipping them."""
        state = state_with(marks=[{'x': 0, 'y': 0, 'name': 'A' * 40}])
        scale, center_x, center_y = MapPreview._framing(
            state, self.width, self.height, self.font)
        self.assertNotEqual(center_x, 0)
        self.assertEqual(center_y, 0)
        self.assertGreater(scale, 0)


if __name__ == '__main__':
    unittest.main()
