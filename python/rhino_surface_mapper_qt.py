"""Qt main window and map canvas for Rhino Surface Mapper.

This module owns presentation concerns only: widgets, painting, timers,
shortcuts, and live UI state. Core mapping rules, coordinate math, PML matching,
and persistence live in Qt-free modules such as ``mapper_core``, ``map_pml``,
and ``map_persistence`` so they remain testable without a Qt application.
"""
import json
import copy
import math
import sys
import time
from pathlib import Path

from PySide6.QtCore import QPointF, QRectF, Qt, QTimer, Signal
from PySide6.QtGui import QColor, QPainter, QPen, QKeySequence, QShortcut, QFont, QPainterPath, QFontMetricsF, QIcon, QTransform
from PySide6.QtSvg import QSvgRenderer
from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout,
    QHBoxLayout, QLabel, QPushButton, QSpinBox, QCheckBox, QFileDialog, QMessageBox)
from mapper_core import MapperState
from map_pml import corresponds_to_map, newest_by_pml
from settings_persistence import load_preferences
from elite_dangerous.status import elite_dangerous_is_running, read_status_if_changed
from elite_dangerous.journal import JournalIdentityReader
from pyqt_overlay import OverlayWindow
from qt_map_operations import MapOperations
from radar import RadarPulse
from radar_input import RadarInput
from steering_ui import SteeringUI
from layout_options import (LayoutOptions, OP_EXIT, OP_MARK, OP_MARK_DEPOSIT,
                            OP_MARK_RIG)
from map_library import MapLibraryWindow
from deposit_marker import draw_deposit, deposit_bounds
from numeric_fields import MetresSpinBox, DegreesSpinBox
from i18n import install_translator, translate
from PySide6.QtWidgets import QDialog, QFormLayout, QComboBox, QLineEdit, QDialogButtonBox


OPTIONS_PATH = Path(
    sys.executable if getattr(sys, 'frozen', False) else __file__
).resolve().parent / 'options.json'


class AzimuthSpinBox(DegreesSpinBox):
    """Spin box that presents integer bearings as three digits."""

    def textFromValue(self, value):
        """Return the visible 000-359 bearing text without changing the value."""
        return f'{value:03d}'


class MapView(QWidget):
    """Interactive map canvas that renders ``MapperState`` in metres.

    The view owns transient presentation state: pan centre, zoom scale, cached
    vector paths, renderer assets, cursor text, and theme colours. ``MapperState``
    remains the source of map data; this widget converts between world metres and
    screen pixels, emits mouse-intent signals, and never performs file or PML
    business logic.
    """
    # Qt signals carry map intents; the window decides which operation to run.
    # This keeps the drawing widget independent from dialogs and persistence.
    cursor_changed = Signal(str)
    clicked = Signal(QPointF)
    menu_requested = Signal(QPointF)

    def __init__(self, state):
        """Initialize the canvas around shared mapper state.

        ``state`` is retained by reference and is not copied. The initial scale is
        0.08 pixels per metre, cached trail paths start empty, and the Rhino SVG
        is owned by the widget for the widget lifetime.
        """
        super().__init__()
        self.state = state
        self.radar = RadarPulse()
        self.colors = {}
        self.text_scale = 1.0
        self.dark_theme = False
        self.cursor_text = translate('MapView', 'Cursor: —')
        self.cursor_changed.connect(self.set_cursor_text)
        self.center = QPointF(0, 0)
        # Initial zoom is intentionally modest: 0.08 px per metre shows context
        # around the Rhino before the user zooms into local details.
        self.scale = 0.08
        self.drag = None
        # Trails can contain thousands of positions, so cache vector paths.
        # They are rebuilt only when content or scale changes; panning only
        # applies a different transform to the same cached geometry.
        self._trajectory_source = None
        self._trajectory_generation = None
        self._trajectory_point_count = -1
        self._trajectory_scale = None
        self._trajectory_content = None
        self._coverage_path = QPainterPath()
        self._trail_line_path = QPainterPath()
        self._trail_dot_path = QPainterPath()
        self._isolated_coverage_points = []
        self.setMinimumSize(400, 300)
        self.setMouseTracking(True)
        base = Path(__file__).resolve().parent
        self.rhino_renderer = QSvgRenderer(str(base / 'assets' / 'Rhino.svg'), self)
        self.rhino_height = 56.0

    def screen(self, x, y):
        """Convert world metres to local widget pixels.

        The transform is ``screenX = width/2 + (worldX - centerX) * scale`` and
        ``screenY = height/2 - (worldY - centerY) * scale``. World Y points north
        while screen Y points down. Returns a ``QPointF`` and has no side effects.
        """
        return QPointF(self.width()/2 + (x-self.center.x())*self.scale,
                       self.height()/2 - (y-self.center.y())*self.scale)

    def world(self, point):
        """Convert a local widget point back to world metres.

        ``point`` is usually a mouse position. The inverse transform preserves the
        north-up world convention despite Qt's down-positive screen Y axis.
        Returns a ``QPointF`` and does not alter the view.
        """
        return QPointF(self.center.x() + (point.x()-self.width()/2)/self.scale,
                       self.center.y() - (point.y()-self.height()/2)/self.scale)

    def world_transform(self):
        """Build the painter transform from world metres to pixels.

        The transform applies zoom, centre offset, and the Y-axis inversion in one
        Qt matrix so cached paths can stay in map coordinates while panning and
        repainting.
        """
        return QTransform(self.scale, 0, 0, -self.scale,
                          self.width()/2-self.center.x()*self.scale,
                          self.height()/2+self.center.y()*self.scale)

    def recenter(self):
        """Move the current Rhino position to the centre of the view.

        The method reads telemetry from ``MapperState`` and preserves the current
        zoom. If the map or Rhino position is unknown, it leaves the view unchanged.
        """
        s = self.state
        if s.rhino_lat is not None and s.center_lat is not None:
            center = QPointF(*s.llxy(s.rhino_lat, s.rhino_lon))
            if center != self.center:
                self.center = center
                self.update()

    def zoom_at(self, point, factor):
        """Zoom around a screen point without moving that world point.

        ``factor`` greater than 1 zooms in and less than 1 zooms out. Scale is
        clamped to 0.001-10 pixels per metre to avoid unusable extremes. The view
        centre is adjusted and a repaint is requested.
        """
        # Capture the world point before scaling, then compensate the centre so
        # cursor-anchored zoom does not make the terrain jump under the mouse.
        before = self.world(point)
        self.scale = max(0.001, min(10, self.scale*factor))
        self.center += before-self.world(point)
        self.update()

    def wheelEvent(self, event):
        """Handle mouse-wheel zoom using Qt wheel delta units.

        A standard 120-unit wheel step applies ``1.15 ** (delta / 120)``. Zoom is
        anchored under the cursor so the selected terrain stays stable while
        scrolling.
        """
        self.zoom_at(event.position(), 1.15 ** (event.angleDelta().y()/120))

    def mousePressEvent(self, event):
        """Translate mouse-button presses into view or window intents.

        Left clicks emit placement/navigation intent, middle clicks recenter, and
        right button presses start panning. Signals keep dialog and business
        decisions in the owning window.
        """
        if event.button() == Qt.MouseButton.LeftButton:
            self.clicked.emit(event.position())
        elif event.button() == Qt.MouseButton.MiddleButton:
            self.recenter()
        elif event.button() == Qt.MouseButton.RightButton:
            self.drag = event.position()
            self.press_position = event.position()
            self.dragged = False
            self.setCursor(Qt.CursorShape.ClosedHandCursor)

    def mouseMoveEvent(self, event):
        """Pan during drags and publish cursor coordinate text.

        Drag movement updates the view centre in metres. When map telemetry is
        available, the method reports latitude/longitude plus distance and bearing
        from the Rhino.
        """
        if self.drag is not None:
            if (event.position()-self.press_position).manhattanLength() > 4:
                self.dragged = True
            delta = event.position()-self.drag
            self.center += QPointF(-delta.x()/self.scale, delta.y()/self.scale)
            self.drag = event.position()
            self.update()
        s = self.state
        if s.center_lat is not None:
            q = self.world(event.position())
            lat, lon = s.xyll(q.x(), q.y())
            text = translate('MapView', 'Cursor: {latitude:.5f}°, {longitude:.5f}°').format(
                latitude=lat, longitude=lon)
            if s.rhino_lat is not None:
                x, y = s.llxy(s.rhino_lat, s.rhino_lon)
                dx, dy = q.x()-x, q.y()-y
                text += translate(
                    'MapView', ' | Distance: {distance:.0f} m | Bearing: {bearing:03.0f}°').format(
                        distance=math.hypot(dx,dy),
                        bearing=math.degrees(math.atan2(dx,dy)) % 360)
            self.cursor_changed.emit(text)

    def mouseReleaseEvent(self, event):
        """Finish right-button panning or request a marker menu.

        A right release without meaningful movement emits ``menu_requested``.
        Dragged releases only end panning so context menus do not appear after map
        navigation.
        """
        if event.button() == Qt.MouseButton.RightButton:
            self.drag = None
            self.unsetCursor()
            if not self.dragged:
                self.menu_requested.emit(event.position())

    def marker_at(self, position):
        """Return the topmost marker near a screen position.

        Hit testing favours explicit label geometry first: marks, then deposit
        label bounds. Remaining deposits, rigs, route markers, and the next target
        compete by nearest centre within a 14 pixel threshold. Returns
        ``(kind, item)`` or ``None``.
        """
        for item in reversed(self.state.marks):
            if self.mark_bounds(item).contains(position):
                return 'marks', item
        for item in reversed(self.state.deposits):
            if deposit_bounds(self.screen(item['x'], item['y']), self.map_font(), item).contains(position):
                return 'deposits', item
        # Centre-distance fallback accepts only markers within 14 screen pixels;
        # label geometry above takes precedence for marks and deposits.
        nearest, distance = None, 14.0
        for kind in ('deposits','rigs'):
            for item in getattr(self.state,kind):
                q = self.screen(item['x'],item['y'])
                d = math.hypot(q.x()-position.x(),q.y()-position.y())
                if d < distance:
                    nearest, distance = (kind,item), d
        for item in ([] if self.state.read_only else self.state.route_history):
            q = self.screen(item['x'], item['y'])
            d = math.hypot(q.x() - position.x(), q.y() - position.y())
            if d < distance:
                nearest, distance = ('route', item), d
        if self.state.next_target_xy is not None:
            tx, ty = self.state.next_target_xy
            q = self.screen(tx, ty)
            d = math.hypot(q.x() - position.x(), q.y() - position.y())
            if d < distance:
                route_item = {'x': tx, 'y': ty, 'number': self.state.route_index + 1, 'status': 'next'}
                nearest, distance = ('route', route_item), d
        return nearest

    def map_font(self):
        """Return the map-label font adjusted for high-DPI displays.

        Logical screen geometry scales text up to 2x on very large displays while
        respecting Qt device scaling. Map metres and zoom are not changed; only
        text and screen-fixed symbols become easier to read. The font is computed
        during painting so moving the window between monitors is handled.
        """
        font = QFont(self.font())
        screen = QWidget.screen(self)
        if screen is not None:
            geometry = screen.geometry()
            factor = max(1.0, min(2.0, min(geometry.width(), geometry.height()) / 1080.0))
            if font.pointSizeF() > 0:
                font.setPointSizeF(font.pointSizeF() * factor)
            elif font.pixelSize() > 0:
                font.setPixelSize(round(font.pixelSize() * factor))
        font.setPointSizeF(max(1, font.pointSizeF()) * getattr(self, "text_scale", 1.0))
        return font

    def draw_route_marker(self, painter, item):
        """Draw a numbered route marker above coverage and trail layers.

        The marker remains screen-sized rather than map-scaled. It uses opaque
        fill and contrasting outlines so reached and skipped route points remain
        legible. Painter save/restore keeps font, brush, and pen changes local.
        """
        painter.save()
        font = QFont(painter.font())
        font.setBold(True)
        painter.setFont(font)
        label = str(item['number'])
        metrics = painter.fontMetrics()
        # Reserve room for two digits and font-proportional padding.
        padding = max(4.0, metrics.height() * 0.22)
        diameter = max(metrics.height(), metrics.horizontalAdvance(label)) + 2 * padding
        center = self.screen(item['x'], item['y'])
        bounds = QRectF(center.x()-diameter/2, center.y()-diameter/2, diameter, diameter)
        color = QColor('#a62020' if item['status'] == 'skipped' else '#205c28')
        # The white halo keeps the marker readable over thick trail strokes.
        painter.setPen(QPen(QColor('white'), max(4.0, metrics.height()*0.22)))
        painter.setBrush(QColor('white'))
        painter.drawEllipse(bounds)
        painter.setPen(QPen(color, max(2.0, metrics.height()*0.10)))
        painter.drawEllipse(bounds)
        painter.drawText(bounds, Qt.AlignmentFlag.AlignCenter, label)
        painter.restore()

    def mark_bounds(self, item):
        """Calculate the screen-space hit rectangle for a named mark.

        The rectangle follows the current high-DPI map font and is used
        consistently by painting and hit testing. It has no side effects.
        """
        q = self.screen(item['x'], item['y'])
        metrics = QFontMetricsF(self.map_font())
        height = max(28.0, metrics.height()+10)
        return QRectF(q.x()-height/2, q.y()-height/2,
                      height+metrics.horizontalAdvance(item['name'])+12, height)

    def draw_mark(self, painter, item):
        """Draw a labelled custom mark using vector geometry.

        The icon does not depend on emoji or platform font support. The marker is
        drawn in screen space and restore calls prevent painter state from leaking.
        """
        painter.save()
        bounds = self.mark_bounds(item)
        painter.setPen(QPen(QColor('#ffffff'), 2))
        painter.setBrush(QColor('#172b4d'))
        painter.drawRoundedRect(bounds, 5, 5)
        q = self.screen(item['x'], item['y'])
        painter.save()
        painter.translate(q)
        factor = bounds.height()/28
        painter.scale(factor, factor)
        path = QPainterPath()
        path.moveTo(-5, -5)
        path.cubicTo(-5, -12, 6, -12, 6, -5)
        path.cubicTo(6, -1, 0, -1, 0, 3)
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.setPen(QPen(QColor('#ffe066'), 2.8, Qt.PenStyle.SolidLine, Qt.PenCapStyle.RoundCap))
        painter.drawPath(path)
        painter.setPen(Qt.PenStyle.NoPen)
        painter.setBrush(QColor('#ffe066'))
        painter.drawEllipse(QPointF(0, 8), 1.6, 1.6)
        painter.restore()
        painter.setPen(QColor('#ffffff'))
        text_bounds = bounds.adjusted(bounds.height(), 0, -6, 0)
        painter.drawText(text_bounds, Qt.AlignmentFlag.AlignVCenter, item['name'])
        painter.restore()

    def trajectory_paths(self):
        """Build or reuse cached coverage and trail paths.

        Paths are stored in world metres and regenerated only when points,
        generation, or scale-sensitive dot size changes. ``break_before`` starts
        a new section so gaps between independent tracks are not painted as
        travelled ground.
        """
        s = self.state
        # Include content in the signature because operations can mark a break
        # on an existing point without replacing the list object.
        content = tuple((point['x'], point['y'], bool(point.get('break_before')))
                        for point in s.points)
        signature = (id(s.points), getattr(s, 'map_generation', None),
                     len(s.points), self.scale, content)
        cached = (self._trajectory_source, self._trajectory_generation,
                  self._trajectory_point_count, self._trajectory_scale,
                  self._trajectory_content)
        if signature == cached:
            return (self._coverage_path, self._trail_line_path,
                    self._trail_dot_path, self._isolated_coverage_points)

        coverage = QPainterPath()
        trail_lines = QPainterPath()
        trail_dots = QPainterPath()
        isolated = []
        previous = None
        # Trail dots are two screen pixels in radius regardless of map zoom.
        dot_radius = 2.0 / self.scale
        for point in s.points:
            current = QPointF(point['x'], point['y'])
            connected = previous is not None and not point.get('break_before')
            if connected:
                coverage.lineTo(current)
                trail_lines.lineTo(current)
                # The first point in this section is no longer isolated once connected.
                if isolated and isolated[-1] == previous:
                    isolated.pop()
            else:
                coverage.moveTo(current)
                trail_lines.moveTo(current)
                isolated.append(current)
            trail_dots.addEllipse(QRectF(current.x()-dot_radius, current.y()-dot_radius,
                                         dot_radius*2, dot_radius*2))
            previous = current

        self._trajectory_source, self._trajectory_generation = signature[:2]
        self._trajectory_point_count, self._trajectory_scale = signature[2:4]
        self._trajectory_content = signature[4]
        self._coverage_path = coverage
        self._trail_line_path = trail_lines
        self._trail_dot_path = trail_dots
        self._isolated_coverage_points = isolated
        return coverage, trail_lines, trail_dots, isolated

    def paintEvent(self, event):
        """Paint the map layers in deterministic visual order.

        The order is background, grid, radar/coverage, trail, route/search
        geometry, Rhino, persistent markers, overlays, and corner labels. Earlier
        layers provide context while later layers remain clickable and legible.
        The method only paints current state and should not change navigation
        decisions.
        """
        p = QPainter(self)
        p.setRenderHint(QPainter.RenderHint.Antialiasing)
        # Only text uses high-DPI scaling; metres, zoom, and telemetry stay unchanged.
        p.setFont(self.map_font())
        p.fillRect(self.rect(), QColor(self.colors.get('map_background', self.default_map_color('map_background'))))
        s = self.state
        if s.center_lat is None:
            p.setPen(QColor('#b9c9ce' if self.dark_theme else '#233448'))
            p.drawText(self.rect(), Qt.AlignmentFlag.AlignCenter,
                       translate('MapView', 'Enter the SRV to start mapping.'))
            self.draw_corners(p, 10 ** math.ceil(math.log10(70/self.scale)))
            return
        lo, hi = self.world(QPointF(0,self.height())), self.world(QPointF(self.width(),0))
        # Choose a power-of-ten grid step targeting roughly 70 px spacing.
        # The loops draw only visible grid lines, even for very large maps.
        step = 10 ** math.ceil(math.log10(70/self.scale))
        p.setPen(QPen(QColor(self.colors.get('grid_color', self.default_map_color('grid_color')))))
        for x in range(math.floor(lo.x()/step), math.ceil(hi.x()/step)+1):
            p.drawLine(self.screen(x*step,lo.y()), self.screen(x*step,hi.y()))
        for y in range(math.floor(lo.y()/step), math.ceil(hi.y()/step)+1):
            p.drawLine(self.screen(lo.x(),y*step), self.screen(hi.x(),y*step))
        if not s.read_only:
            # Draw radar coverage before trails so it reads as background context.
            # Later trail strokes and points remain visible and easier to follow.
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor(self.colors.get('wave_color','#69b574')))
            for pulse in s.radar_coverage:
                radius = pulse['radius'] * self.scale
                p.drawEllipse(self.screen(pulse['x'],pulse['y']),radius,radius)
            # Rebuilding coverage from saved points preserves it after reopening
            # the map, without storing images or mixing it with transient radar
            # pulses. The path is in metres and is rebuilt only when points or
            # zoom change.
            coverage_path, trail_lines, trail_dots, isolated_points = self.trajectory_paths()
            coverage_color = QColor(self.colors.get('coverage_color','#8cbd8c'))
            p.save()
            p.setWorldTransform(self.world_transform())
            p.setPen(QPen(coverage_color, s.coverage_width_m, Qt.PenStyle.SolidLine,
                          Qt.PenCapStyle.RoundCap, Qt.PenJoinStyle.RoundJoin))
            p.setBrush(Qt.BrushStyle.NoBrush)
            p.drawPath(coverage_path)
            # A one-point section has no line segment, so keep its coverage disc.
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(coverage_color)
            coverage_radius_m = s.coverage_width_m / 2
            for point in isolated_points:
                p.drawEllipse(point, coverage_radius_m, coverage_radius_m)
            p.restore()
            if s.in_srv and s.rhino_lat is not None:
                q = self.screen(*s.llxy(s.rhino_lat,s.rhino_lon))
                if s.points:
                    last = s.points[-1]
                    x, y = s.llxy(s.rhino_lat, s.rhino_lon)
                    if math.hypot(x-last['x'], y-last['y']) <= 100:
                        p.drawLine(self.screen(last['x'], last['y']), q)
                # Current coverage disc stays behind trail and marker layers.
                p.setPen(QPen(QColor(self.colors.get('trail_color','#2f7d32')), 2))
                p.setBrush(QColor(self.colors.get('coverage_color','#8cbd8c')))
                coverage_radius = s.coverage_width_m*self.scale/2
                p.drawEllipse(q, coverage_radius, coverage_radius)
            for pulse, _, _ in self.radar.waves:
                if any(item is pulse for item in s.radar_coverage):
                    p.setPen(QPen(QColor(self.colors.get('wave_color','#69b574')), 1.5))
                    p.setBrush(Qt.BrushStyle.NoBrush)
                    radius = pulse['radius'] * self.scale
                    p.drawEllipse(self.screen(pulse['x'],pulse['y']),radius,radius)
            # Trail lines and dots are separate paths, each drawn with one Qt call.
            # This keeps large recorded tracks responsive.
            trail_color = QColor(self.colors.get('trail_color','#2f7d32'))
            p.save()
            p.setWorldTransform(self.world_transform())
            p.setPen(QPen(trail_color, 1/self.scale))
            p.setBrush(Qt.BrushStyle.NoBrush)
            p.drawPath(trail_lines)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(trail_color)
            p.drawPath(trail_dots)
            p.restore()
            p.setBrush(Qt.BrushStyle.NoBrush)
            # Datum is fixed; NEXT advances as the search route progresses.
            if s.datum_lat is not None:
                q = self.screen(*s.llxy(s.datum_lat,s.datum_lon))
                p.setPen(QPen(QColor('#cc7a00'),2))
                p.drawEllipse(q,8,8)
                p.drawText(q+QPointF(12,12),'DATUM')
        if s.in_srv and s.rhino_lat is not None:
            q = self.screen(*s.llxy(s.rhino_lat,s.rhino_lon))
            p.setPen(QPen(QColor('#7777aa'),1,Qt.PenStyle.DashLine))
            radius = s.scanner_range_m*self.scale
            if not s.read_only:
                p.drawEllipse(q,radius,radius)
            if s.next_target_xy is not None and not s.search_paused:
                target = self.screen(*s.next_target_xy)
                p.setPen(QPen(QColor('#e08a00'),2,Qt.PenStyle.DashLine))
                p.drawLine(q,target)
                p.drawEllipse(target,9,9)
                p.drawText(target+QPointF(12,-12), translate('MapView', 'NEXT'))
            if s.active_nav_target is not None:
                nav_pos = self.screen(s.active_nav_target['x'], s.active_nav_target['y'])
                p.setPen(QPen(QColor('#00bfff'), 2.5, Qt.PenStyle.DashLine))
                p.drawLine(q, nav_pos)
                p.drawEllipse(nav_pos, 8, 8)
                p.drawText(
                    nav_pos + QPointF(12, -12),
                    translate('MapperWindow', 'NAVIGATE: {name}').format(
                        name=s.active_nav_target.get('name', '')))
            elif s.return_to_pause and s.search_pause_point is not None:
                pause_screen = self.screen(*s.search_pause_point)
                p.setPen(QPen(QColor('#ffb300'), 2.5, Qt.PenStyle.DashLine))
                p.drawLine(q, pause_screen)
            # The SVG points north; heading rotates it clockwise in screen space.
            # The vehicle symbol stays screen-sized regardless of map zoom.
            if s.rhino_heading is not None and self.rhino_renderer.isValid():
                bounds = self.rhino_renderer.viewBoxF()
                height = self.rhino_height
                width = height * bounds.width() / bounds.height()
                p.save()
                p.translate(q)
                p.rotate(s.rhino_heading % 360)
                self.rhino_renderer.render(p, QRectF(-width/2, -height/2, width, height))
                p.restore()
            else:
                p.setPen(QPen(QColor('#cc2222'),3))
                p.drawEllipse(q,6,6)
        p.setPen(QPen(QColor('#c43b3b'),2))
        for item in s.deposits:
            q = self.screen(item['x'],item['y'])
            draw_deposit(p, q, item)
        p.setPen(QPen(QColor('#3333aa'),2))
        for item in s.rigs:
            q = self.screen(item['x'],item['y'])
            p.drawRect(int(q.x()-6),int(q.y()-6),12,12)
            p.drawText(q+QPointF(10,0), translate('MapView', 'Rig'))
        for item in ([] if s.read_only else s.route_history):
            # Route numbers are a top marker layer so they remain above the trail.
            self.draw_route_marker(p, item)
        for item in s.marks:
            self.draw_mark(p, item)
        if s.search_pause_point is not None:
            p_pos = self.screen(*s.search_pause_point)
            p.save()
            p.setPen(QPen(QColor('#101010'), 2))
            p.setBrush(QColor('#ffd21c'))
            p.drawEllipse(p_pos, 11, 11)
            p.setPen(Qt.PenStyle.NoPen)
            p.setBrush(QColor('#101010'))
            p.drawRect(int(p_pos.x() - 4.5), int(p_pos.y() - 6), 3, 12)
            p.drawRect(int(p_pos.x() + 1.5), int(p_pos.y() - 6), 3, 12)
            p.setFont(self.map_font())
            p.setPen(QPen(QColor('#b8860b'), 1))
            p.drawText(p_pos + QPointF(15, 4), translate('MapView', 'PAUSED'))
            p.restore()
        self.draw_corners(p, step)

    def default_map_color(self, key):
        """Return a fallback map colour for the active theme.

        ``key`` must be one of the supported colour names. Unknown keys raise the
        normal ``KeyError`` from dictionary access.
        """
        defaults = {'map_background': '#606060' if self.dark_theme else '#ffffff',
                    'grid_color': '#747474' if self.dark_theme else '#eeeeee'}
        return defaults[key]

    def set_cursor_text(self, text):
        """Store cursor status text and request a repaint."""
        self.cursor_text = text
        self.update()

    def leaveEvent(self, event):
        """Clear cursor coordinates when the mouse leaves the canvas."""
        self.set_cursor_text(translate('MapView', 'Cursor: —'))
        super().leaveEvent(event)

    def draw_corners(self, p, step):
        """Draw static corner labels and the north compass.

        ``step`` is the current grid spacing in metres. Labels are painted last in
        screen space so they stay readable over all map layers.
        """
        p.save()
        p.setFont(self.map_font())
        fg = QColor('#edf1f5' if self.dark_theme else '#233448')
        bg = QColor(self.colors.get('map_background', self.default_map_color('map_background')))
        fg = QColor('#edf1f5' if bg.lightnessF() < .55 else '#233448')
        def label(rect, value, alignment):
            """Paint a filled text label within the corner overlay."""
            p.fillRect(rect, bg)
            p.setPen(fg)
            p.drawText(rect.adjusted(5,0,-5,0), alignment | Qt.AlignmentFlag.AlignVCenter, value)
        title = ' — '.join(str(v) for v in (self.state.system,self.state.body) if v) or translate('MapView', 'System and body: —')
        metrics = p.fontMetrics()
        height = metrics.height()+10
        title = metrics.elidedText(title, Qt.TextElideMode.ElideRight, max(50,self.width()-110))
        label(QRectF(8,8,min(metrics.horizontalAdvance(title)+14,self.width()-90),height),title,Qt.AlignmentFlag.AlignLeft)
        grid = translate('MapView', 'Grid: {step:g} m').format(step=step)
        grid_width = metrics.horizontalAdvance(grid)+14
        label(QRectF(8,self.height()-height-8,grid_width,height),grid,Qt.AlignmentFlag.AlignLeft)
        available = max(30,self.width()-grid_width-35)
        cursor = metrics.elidedText(self.cursor_text,Qt.TextElideMode.ElideRight,available-10)
        width = min(available,metrics.horizontalAdvance(cursor)+14)
        label(QRectF(self.width()-width-8,self.height()-height-8,width,height),cursor,Qt.AlignmentFlag.AlignRight)
        p.setBrush(bg)
        p.setPen(QPen(QColor('#87949c'),1))
        p.drawEllipse(QRectF(self.width()-64,10,52,52))
        p.setPen(Qt.PenStyle.NoPen)
        p.setBrush(QColor('#cc3434'))
        arrow = QPainterPath()
        arrow.moveTo(self.width()-38,17)
        arrow.lineTo(self.width()-44,32)
        arrow.lineTo(self.width()-32,32)
        arrow.closeSubpath()
        p.drawPath(arrow)
        p.setPen(fg)
        p.drawText(QRectF(self.width()-64,32,52,25),Qt.AlignmentFlag.AlignCenter,'N')
        p.restore()



class MapperWindow(LayoutOptions, SteeringUI, MapOperations, QMainWindow):
    """Main Qt window that coordinates controls, timers, and live map state.

    The window owns the shared ``MapperState``, ``MapView``, overlay, timers,
    input helpers, and child dialogs/windows. It delegates map operations to
    ``MapOperations`` and steering widgets to ``SteeringUI`` while keeping core
    business rules in Qt-free modules.
    """
    def __init__(self, status_path=None, game_running_check=None):
        """Construct the main window and start live UI timers.

        ``status_path`` and ``game_running_check`` allow tests to replace game
        telemetry. The 50 ms poll timer reads Status.json changes, the 16 ms radar
        timer advances radar pulses, and ``SteeringUI`` creates a 16 ms assistance
        timer for steering. Startup loads preferences, input bindings, overlay,
        shortcuts, and performs an initial poll.
        """
        super().__init__()
        self.setWindowTitle(translate('MapperWindow', 'Rhino Surface Mapper'))
        # Transient messages have dialog alternatives; hide the status bar to
        # avoid a redundant second line below the footer.
        self.statusBar().hide()
        self.setWindowIcon(QIcon(str(Path(__file__).resolve().parent/'assets'/'Rhino_App.svg')))
        self.resize(1150,800)
        # One state instance is shared by the window, map view, and operations.
        # The overlay receives text and colours only; it does not mutate map data.
        self.state = MapperState()
        self.placing_rig = False
        self.parameter_spins = {}
        self.status_path = Path(status_path) if status_path else Path.home()/'Saved Games'/'Frontier Developments'/'Elite Dangerous'/'Status.json'
        # Path of the opened map, so Replace writes this version rather than
        # accidentally overwriting the canonical file for the same PML.
        self.current_map_path = None
        self.last_mtime = None
        self.live_status = {}
        self.status_valid = False
        self.retry_status = False
        self._game_running_check = game_running_check or elite_dangerous_is_running
        self._game_running = False
        self._next_game_running_check = 0.0
        journal_directory = (Path(status_path).parent / '__no-test-journal__'
                             if game_running_check is not None and status_path is not None
                             else None)
        self.journal_identity = JournalIdentityReader(journal_directory)
        self.transition_required = False
        self.pending_status_update = None
        self.pending_status_snapshot = None
        self.latest_status_snapshot = None
        self.pending_old_map_resolved = False
        self.pending_destination = None
        self._transition_attempt_snapshot = None
        self._transition_schedule_pending = False
        self._transition_resolution_active = False
        self.radar_input = RadarInput()
        self.options_path = OPTIONS_PATH
        self.scanner_group = 0
        self.bindings_override = ''
        options = load_preferences(self.options_path)
        group = options.get('scanner_group', 0)
        if type(group) is int and 0 <= group <= 25:
            self.scanner_group = group
        override = options.get('bindings_path', '')
        if isinstance(override, str):
            self.bindings_override = override
        self.radar_input.load(self.bindings_override or None)
        self.overlay = OverlayWindow()
        self.overlay_mode_active = False
        self.overlay_navigation_active = False
        self.view = MapView(self.state)
        container = QWidget()
        layout = QVBoxLayout(container)
        controls = QHBoxLayout()
        operations = QHBoxLayout()
        layout.addLayout(operations)
        self.op_buttons = {}
        for button_id, title, callback in [(OP_MARK,'Marker',self.mark),
                                           (OP_MARK_DEPOSIT,'Mark deposit',self.mark_deposit),
                                           (OP_MARK_RIG,'Mark rig',self.mark_rig),
                                           (OP_EXIT,'Exit',self.close)]:
            button = QPushButton(translate('MapperWindow', title))
            button.clicked.connect(callback)
            operations.addWidget(button)
            self.op_buttons[button_id] = button
        layout.addLayout(controls)
        for label, field, minimum in [('Cobertura:','coverage_width_m',100),('Scanner:','scanner_range_m',500)]:
            spin = MetresSpinBox()
            self.parameter_spins[field] = spin
            spin.setRange(minimum,5000)
            spin.setSingleStep(100)
            spin.setValue(int(getattr(self.state,field)))
            # field=field captures this loop iteration; otherwise every lambda
            # would use the last field value when invoked later by Qt.
            spin.valueChanged.connect(lambda value, field=field: self.set_parameter(field,value))
        self.search_azimuth_spin = AzimuthSpinBox()
        self.search_azimuth_spin.setRange(0,359)
        self.search_azimuth_spin.setValue(0)
        self.search_azimuth_spin.setToolTip(translate('MapperWindow', 'Initial bearing for the next search: 000=North, 090=East, 180=South, 270=West.'))
        self.parameter_spins['search_azimuth'] = self.search_azimuth_spin
        for control_id, title, callback, enabled in [
                ('search_button', 'Start search', self.handle_search_button, True),
                ('skip_button', 'Skip next', self.handle_skip_button, True),
                ('overlay_button', 'Overlay', self.toggle_overlay, False)]:
            button = QPushButton(translate('MapperWindow', title))
            setattr(self, control_id, button)
            button.setEnabled(enabled)
            button.clicked.connect(callback)
        self.follow = QCheckBox(translate('MapperWindow', 'Centre'))
        self.follow.toggled.connect(lambda checked: self.view.recenter() if checked else None)
        # Consolidate live status into a single footer row.
        self.info_bar = QHBoxLayout()
        self.info_left = QLabel(translate('MapperWindow', 'Waiting for Status.json'))
        self.info_right = QLabel('FUEL : —')
        self.info_bar.addWidget(self.info_left, 1)
        self.info_bar.addWidget(self.info_right)
        layout.addLayout(self.info_bar)
        
        self.radar_info = QLabel(translate('MapperWindow', 'Radar: waiting for the game'))
        self.radar_info.setToolTip(self.radar_input.message)
        layout.addWidget(self.radar_info)
        layout.addWidget(self.view,1)
        # The footer keeps active file information on the left and navigation
        # guidance aligned to the right.
        footer = QHBoxLayout()
        self.navigation = QLabel(translate('MapperWindow', 'Suggested sec.: —'))
        self.map_flag_icons = QLabel()
        self.map_flag_icons.setFixedHeight(22)
        footer.addWidget(self.map_flag_icons)
        self.map_file_info = QLabel(translate('MapperWindow', 'Map: not saved yet'))
        self.map_file_info.setTextFormat(Qt.TextFormat.PlainText)
        footer.addWidget(self.map_file_info, 1)
        footer.addWidget(self.navigation)
        layout.addLayout(footer)
        self.setup_steering(layout, options if isinstance(options,dict) else {})
        self.build_layout_options(layout, operations, controls, options)
        self.setCentralWidget(container)
        self.view.clicked.connect(self.place_rig)
        self.view.menu_requested.connect(self.marker_menu)
        escape = QShortcut(QKeySequence('Escape'),self)
        escape.activated.connect(self.cancel_placement)
        for key, callback in [('Home',self.view.recenter),('+',lambda:self.view.zoom_at(QPointF(self.view.rect().center()),1.15)),('-',lambda:self.view.zoom_at(QPointF(self.view.rect().center()),1/1.15))]:
            shortcut = QShortcut(QKeySequence(key),self)
            shortcut.activated.connect(callback)
        # Window-owned timers run on the main Qt event loop.
        # The same loop services both the main window and the overlay.
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.poll)
        # Poll Status.json every 50 ms; game-process checks inside poll are
        # throttled to once per second to avoid repeated process scans.
        self.timer.start(50)
        self.radar_timer = QTimer(self)
        self.radar_timer.timeout.connect(self.update_radar)
        # Radar animation ticks at about 60 Hz; steering assistance uses the
        # same 16 ms cadence in SteeringUI.setup_steering.
        self.radar_timer.start(16)
        screen = QApplication.primaryScreen().availableGeometry()
        self.overlay.move(screen.x()+(screen.width()-self.overlay.width())//2,screen.y()+40)
        self.poll()

    def radar_options(self):
        """Open or close the options panel that contains radar controls."""
        self.options_button.toggle()

    def update_radar(self):
        """Advance radar pulse state and update the radar status label.

        The 16 ms timer calls this method. It suppresses pulses while map
        transitions are unresolved or the map is read-only, then checks SRV,
        analysis-mode, fire-group, focus, and binding prerequisites before
        ticking the radar model.
        """
        now = time.monotonic()
        s = self.state
        if self.transition_required:
            self.view.radar.waves.clear()
            self.radar_info.setText(translate('MapperWindow', 'Radar: waiting for map change'))
            return
        if s.read_only:
            self.view.radar.waves.clear()
            return
        flags = self.live_status.get('Flags', 0)
        eligible = (self.status_valid and bool(flags & 0x04000000) and bool(flags & 0x08000000)
                    and self.live_status.get('FireGroup') == self.scanner_group
                    and self.live_status.get('GuiFocus', 0) == 0
                    and s.rhino_lat is not None and s.center_lat is not None
                    and bool(self.radar_input.bindings))
        focused = self.radar_input.game_focused() if self.radar_input.bindings else False
        enabled = eligible and focused
        down = self.radar_input.down() if focused else None
        if self.view.radar.tick(s, now, enabled, down, (id(s), s.body_key, s.map_generation)):
            self.view.update()
        text = (translate('MapperWindow', 'Radar: controls unavailable — check Options') if not self.radar_input.bindings else
                translate('MapperWindow', 'Radar: pulse in progress') if self.view.radar.active is not None else
                translate('MapperWindow', 'Radar: ready') if enabled else
                translate('MapperWindow', 'Radar: waiting for a valid SRV position') if not self.status_valid else
                translate('MapperWindow', 'Radar: waiting for game focus') if not focused else
                translate('MapperWindow', 'Radar: select Analysis Mode') if not flags & 0x08000000 else
                translate('MapperWindow', 'Radar: select group {group}').format(group=chr(65+self.scanner_group)) if self.live_status.get('FireGroup') != self.scanner_group else
                translate('MapperWindow', 'Radar: close the game panel'))
        self.radar_info.setText(text)

    def set_parameter(self, field, value):
        """Copy a numeric UI parameter into mapper state.

        ``field`` names a mutable ``MapperState`` attribute and ``value`` is the
        spin-box value. Read-only maps ignore the change; editable maps request a
        repaint.
        """
        if self.state.read_only:
            return
        setattr(self.state,field,value)
        self.view.update()

    def show_map_library(self):
        """Show the independent map-library window."""
        if not hasattr(self, 'map_library') or self.map_library is None:
            self.map_library = MapLibraryWindow(
                self,
                self.view.dark_theme,
                preferences=self.preferences,
                save_preference=self.save_preference,
            )
        self.map_library.show()
        self.map_library.raise_()
        self.map_library.activateWindow()

    def choose_status(self):
        """Prompt for an alternate ``Status.json`` and poll it immediately.

        Cancelling leaves the current path and live state untouched.
        """
        path, _ = QFileDialog.getOpenFileName(self,'Escolher Status.json',str(self.status_path),'JSON (*.json)')
        if path:
            self.status_path = Path(path)
            self.status_valid = False
            self.last_mtime = None
            self.poll()

    def start_search(self):
        """Start a circular search from the current SRV position.

        The method refuses unresolved transitions and read-only maps, confirms
        Datum replacement, interprets the bearing control, then refreshes controls
        and overlay.
        """
        if self.transition_required or self.map_is_read_only():
            return
        if self.state.search_started and QMessageBox.question(
                self, translate('MapperWindow', 'Start search'),
                translate('MapperWindow', 'Replace the current Datum?')) != QMessageBox.StandardButton.Yes:
            return
        self.search_azimuth_spin.interpretText()
        if not self.state.start_search(self.search_azimuth_spin.value()):
            QMessageBox.information(
                self, translate('MapperWindow', 'Start search'),
                translate('MapperWindow', 'Enter the SRV first and wait for its position.'))
        self.refresh()

    def handle_search_button(self):
        """Dispatch the search button between start and stop actions."""
        if self.state.search_started:
            if QMessageBox.question(
                    self, translate('MapperWindow', 'Stop search'),
                    translate('MapperWindow', 'Stop the current search?')) == QMessageBox.StandardButton.Yes:
                self.state.search_started = False
                self.state.search_paused = False
                self.state.search_pause_point = None
                self.state.return_to_pause = False
                self.state.next_target_xy = None
                if self.state.active_nav_target is None:
                    self.overlay.hide()
                    self.overlay_mode_active = False
                self.refresh()
        else:
            self.start_search()

    def skip_next(self):
        """Advance the core search route to the next target and refresh the UI."""
        if self.transition_required:
            return
        self.state.skip_next()
        self.refresh()

    def handle_skip_button(self):
        """Resume a paused search or skip the current search target."""
        if self.transition_required or self.state.read_only:
            return
        if self.state.search_paused or self.state.return_to_pause:
            self.state.search_paused = False
            self.state.search_pause_point = None
            self.state.return_to_pause = False
            self.state.active_nav_target = None
            self.statusBar().showMessage(translate('MapperWindow', 'Search resumed.'))
            self.refresh()
        elif self.state.search_started:
            self.skip_next()

    def toggle_overlay(self):
        """Toggle overlay visibility within modes that allow navigation output.

        Visibility is driven by navigation state and the user's manual choice,
        not by window focus, so dragging the overlay does not hide it.
        """
        if self.transition_required or not (self.state.overlay_allowed() or (self.state.in_srv and self.direction_test_active())):
            return
        self.overlay_mode_active = not self.overlay_mode_active
        self.refresh()

    def toggle_assistance(self):
        """Keep steering assistance inactive while the active map is unresolved."""
        if self.transition_required:
            self.stop_assistance('Assistência desligada: mudança de mapa pendente')
            return
        super().toggle_assistance()

    def update_assistance(self):
        """Skip assistance/navigation calculations for an unresolved location."""
        if self.transition_required:
            return
        super().update_assistance()

    def refresh(self, redraw_map=True):
        """Synchronize controls, labels, overlay, and optional map repaint.

        Control enablement requires a valid SRV session, an editable map, and no
        pending transition. Overlay visibility follows navigation/search state
        rather than game window focus.
        """
        s = self.state
        fuel = s.fuel_percent
        if fuel is None:
            self.info_right.setText('FUEL : —')
        else:
            level = '⛔ Sem combustível' if fuel <= 0 else '🔴 Crítico' if fuel <= 15 else '⚠ Baixo' if fuel <= 30 else '✓ Normal'
            self.info_right.setText(f'FUEL : {fuel:.0f}% {level}')
            
        if self.transition_required:
            self.overlay.set_navigation('—', '—', '#888888', 'white', '')
            navigation = None
        else:
            navigation = s.overlay_navigation()
            self.overlay.set_navigation(*navigation)
        
        # The overlay is visible only when:
        # 1. the current mode allows it (search_started, active_nav, etc.);
        # 2. the commander is in the Rhino (in_srv);
        # 3. the user has not hidden it manually (overlay_mode_active).
        # Window focus is not part of the rule: clicking the overlay to move
        # or resize it naturally takes focus away from the game.
        
        # Distinguish navigation ending from a temporary suspension outside
        # the vehicle or without focus. Only a new mode resets the automatic
        # choice; leaving and re-entering the Rhino preserves manual choice.
        navigating = bool(s.search_started or s.active_nav_target is not None or s.return_to_pause or self.direction_test_active())
        if navigating and not self.overlay_navigation_active:
            self.overlay_mode_active = True
        elif not navigating:
            self.overlay_mode_active = False
        self.overlay_navigation_active = navigating
        allowed = (not self.transition_required and
                    (s.overlay_allowed() or (s.in_srv and self.direction_test_active())))
        self.overlay_button.setEnabled(allowed)
        
        should_be_visible = allowed and self.overlay_mode_active
        
        if not allowed:
            self.overlay.hide()
        elif should_be_visible:
            if not self.overlay.isVisible():
                self.overlay.show()
        else:
            self.overlay.hide()

        # Enable editing controls only for SRV, editable-map, resolved-transition state.
        in_srv = s.in_srv
        self.search_button.setEnabled(in_srv and not s.read_only and not self.transition_required)
        self.skip_button.setEnabled(in_srv and not self.transition_required and
                                    (s.search_started or s.search_paused or s.return_to_pause))
        
        # Marker operation buttons follow the same editability rules.
        if hasattr(self, 'op_buttons'):
            for button_id, btn in self.op_buttons.items():
                if button_id in (OP_MARK, OP_MARK_DEPOSIT, OP_MARK_RIG):
                    btn.setEnabled(in_srv and not s.read_only and not self.transition_required)

        # Search and skip/pause buttons reflect the current route state.
        if hasattr(self, 'search_button'):
            self.search_button.setText(translate('MapperWindow', 'Stop search' if s.search_started else 'Start search'))

        if hasattr(self, 'skip_button'):
            if s.search_paused or s.return_to_pause:
                self.skip_button.setText(translate('MapperWindow', 'Search paused'))
                is_bright = int(time.monotonic() * 2) % 2 == 0
                bg = '#ffd21c' if is_bright else '#d4a000'
                self.skip_button.setStyleSheet(f"background-color: {bg}; color: #101010; font-weight: bold; border: 1px solid #705000; border-radius: 4px; padding: 3px 8px;")
            else:
                self.skip_button.setText(translate('MapperWindow', 'Skip next'))
                self.skip_button.setEnabled(in_srv and not self.transition_required and
                                            s.search_started and s.next_target_xy is not None)
                self.skip_button.setStyleSheet("")

        from map_badges import flag_pixmap
        self.map_flag_icons.setPixmap(flag_pixmap(s.favorite, s.protected))
        self.map_flag_icons.setVisible(s.favorite or s.protected)
        if self.current_map_path and self.current_map_path.exists():
            stamp = time.strftime('%Y-%m-%d %H:%M', time.localtime(self.current_map_path.stat().st_mtime))
            self.map_file_info.setText(
                f'{self.current_map_path.name} — {stamp}'
                + (f" · {translate('MapperWindow', 'Mining only')}" if s.mining_only else ''))
        else:
            self.map_file_info.setText(translate('MapperWindow', 'Map not saved yet'))
        if self.transition_required:
            self.navigation.setText(translate('MapperWindow', 'Waiting for map change'))
        elif s.active_nav_target is not None:
            self.navigation.setText(translate('MapperWindow', 'Navigating: {system} | {body} | {target}').format(system=navigation[0], body=navigation[1], target=navigation[4]))
        elif s.return_to_pause:
            self.navigation.setText(translate('MapperWindow', 'Returning to pause point | {system} | {body}').format(system=navigation[0], body=navigation[1]))
        elif s.search_started and s.next_target_xy is None:
            self.navigation.setText(translate('MapperWindow', 'Circular search completed'))
        else:
            self.navigation.setText(translate('MapperWindow', 'Suggested sec.: {system} | {body}').format(system=navigation[0], body=navigation[1]))

        if redraw_map:
            self.view.update()

    def map_signature(self):
        """Return a compact tuple representing repaint-relevant live state."""
        s = self.state
        nav_sig = (s.active_nav_target.get('x'), s.active_nav_target.get('y')) if s.active_nav_target else None
        return (s.body_key, s.rhino_lat, s.rhino_lon, s.rhino_heading, s.in_srv,
                len(s.points), s.next_target_xy, len(s.route_history),
                nav_sig, s.search_paused, s.search_pause_point, s.return_to_pause)

    def active_map_corresponds(self, system, body, latitude, longitude):
        """Evaluate incoming SRV location without changing the active map."""
        state = self.state
        if not (state.body_key and state.pml_id.strip()
                and state.pml_center_lat is not None
                and state.pml_center_lon is not None):
            return None
        return corresponds_to_map(state, system, body, latitude, longitude)

    def evaluate_status_update(self, status_update, correspondence, raw_status=None):
        """Capture the first accepted telemetry mismatch for transition handling.

        The method records snapshots and schedules deferred resolution after the
        current poll returns, avoiding state replacement in the middle of
        Status.json handling.
        """
        if (not self.transition_required and status_update.accepted
                and (status_update.location_changed or correspondence is False)):
            self.transition_required = True
            self.pending_status_update = status_update
            snapshot = self.latest_status_snapshot if raw_status is None else raw_status
            self.pending_status_snapshot = copy.deepcopy(snapshot)
            self.stop_assistance('Assistência desligada: mudança de mapa pendente')
            self.schedule_pending_transition()

    def schedule_pending_transition(self):
        """Queue a single-shot transition pass after the current event returns."""
        if self._transition_schedule_pending or self._transition_resolution_active:
            return
        self._transition_schedule_pending = True
        # Resolve after the current poll returns, not while telemetry state is
        # still being accepted and compared.
        QTimer.singleShot(0, self._run_pending_transition)

    def _run_pending_transition(self):
        """Execute one deferred transition-resolution attempt."""
        self._transition_schedule_pending = False
        if not self.transition_required or self._transition_resolution_active:
            return
        self._transition_resolution_active = True
        try:
            self._transition_attempt_snapshot = copy.deepcopy(self.latest_status_snapshot)
            if self.resolve_pending_transition() is not None:
                self.activate_pending_destination()
        finally:
            self._transition_resolution_active = False
            if (self.transition_required and self.pending_destination is None
                    and self.latest_status_snapshot != self._transition_attempt_snapshot):
                self.schedule_pending_transition()

    def resolve_pending_transition(self):
        """Resolve saving/discarding the old map and prepare the destination map.

        Returns the pending destination dictionary, ``None`` if user or telemetry
        state prevents activation, and reports recoverable preparation failures
        through Qt.
        """
        if not self.transition_required or self.pending_destination is not None:
            return self.pending_destination
        try:
            if not self.pending_old_map_resolved:
                if not self.prepare_to_replace_current_map(
                        translate('MapperWindow', 'Change location'),
                        translate('MapperWindow', 'changing location'), allow_cancel=False):
                    return None
                self.pending_old_map_resolved = True
            self.pending_destination = self.prepare_pending_destination()
        except (OSError, ValueError, TypeError, KeyError, AttributeError,
                OverflowError, ZeroDivisionError) as exc:
            QMessageBox.critical(
                self, translate('MapperWindow', 'Error preparing map change'), str(exc))
            return None
        return self.pending_destination

    def clear_transition_state(self):
        """Reset lifecycle-only fields after a transition is completed or cancelled."""
        self.transition_required = False
        self.pending_status_update = None
        self.pending_status_snapshot = None
        self.latest_status_snapshot = None
        self.pending_old_map_resolved = False
        self.pending_destination = None
        self._transition_attempt_snapshot = None
        self._transition_schedule_pending = False
        self._transition_resolution_active = False

    def activate_pending_destination(self):
        """Install a prepared destination after validating fresh telemetry.

        The destination is rejected if the latest SRV location no longer matches
        it. Successful activation clears transition state, marks telemetry valid,
        and refreshes the UI.
        """
        if (not self.transition_required or not self.pending_old_map_resolved
                or self.pending_destination is None):
            return False
        status = self._current_transition_status()
        if status is None:
            return False
        destination = self.pending_destination
        candidate = destination.get('state')
        if candidate is None:
            return False
        system, body = status['StarSystem'], status['BodyName']
        latitude, longitude = float(status['Latitude']), float(status['Longitude'])
        if not corresponds_to_map(candidate, system, body, latitude, longitude):
            self.pending_destination = None
            return False
        try:
            candidate.process_status(status)
            if not corresponds_to_map(candidate, system, body, latitude, longitude):
                self.pending_destination = None
                return False
            self.install_prepared_map(
                candidate,
                destination.get('source_text', translate('MapperWindow', 'Loaded map')),
                destination.get('path'),
                poll_after_install=False)
        except (OSError, ValueError, TypeError, KeyError, AttributeError,
                OverflowError, ZeroDivisionError) as exc:
            QMessageBox.critical(
                self, translate('MapperWindow', 'Error activating map change'), str(exc))
            return False
        if not corresponds_to_map(self.state, system, body, latitude, longitude):
            return False
        self.status_valid = True
        self.clear_transition_state()
        self.refresh()
        return True

    def install_loaded_map(self, candidate, source_text='Loaded map', source_path=None):
        """Install a manual map and supersede pending lifecycle state if valid."""
        result = super().install_loaded_map(candidate, source_text, source_path)
        if result and self.transition_required:
            status = self._current_transition_status()
            if (status is not None and corresponds_to_map(
                    self.state, status['StarSystem'], status['BodyName'],
                    float(status['Latitude']), float(status['Longitude']))):
                self.clear_transition_state()
                self.refresh()
        return result

    def _current_transition_status(self):
        """Return the latest valid SRV status suitable for destination preparation."""
        status = self.latest_status_snapshot or self.pending_status_snapshot
        if not status or not (int(status.get('Flags', 0)) & 0x04000000):
            return None
        if not (status.get('StarSystem', '').strip()
                and status.get('BodyName', '').strip()
                and status.get('Latitude') is not None
                and status.get('Longitude') is not None):
            return None
        return copy.deepcopy(status)

    def prepare_pending_destination(self):
        """Build the candidate map for a pending location transition.

        Existing nearby PMLs are preferred and deduplicated by newest version. If
        none match, the user is prompted to identify and save a new PML before
        activation.
        """
        status = self._current_transition_status()
        if status is None:
            return None
        system = status['StarSystem']
        body = status['BodyName']
        latitude = float(status['Latitude'])
        longitude = float(status['Longitude'])
        matches = self.nearby_pml_maps(system, body, latitude, longitude)
        matches = newest_by_pml(matches)
        if len(matches) > 1:
            matches.sort(key=lambda item: item[1].stat().st_mtime, reverse=True)
            labels = [
                translate('MapperWindow', '[{pml_id}] — saved {timestamp} — {filename}').format(
                    pml_id=candidate.pml_id,
                    timestamp=datetime.fromtimestamp(path.stat().st_mtime).strftime('%Y-%m-%d %H:%M'),
                    filename=path.name)
                for _, path, candidate in matches
            ]
            chosen_index = self.choose_list_item(
                translate('MapperWindow', 'Several nearby PMLs'),
                translate('MapperWindow', 'Choose the PML:'), labels)
            if chosen_index is None:
                return None
            path, candidate = matches[chosen_index][1:]
        elif matches:
            _, path, candidate = matches[0]
        else:
            candidate = MapperState()
            candidate.process_status(status)
            if not self.setup_new_pml(candidate):
                return None
            path = self.pml_path(candidate)
            if path is None:
                return None
            path.parent.mkdir(parents=True, exist_ok=True)
            candidate.save(path)
            return dict(state=candidate, path=path,
                        source_text=translate(
                            'MapperWindow', 'PML [{pml_id}] created').format(pml_id=candidate.pml_id))

        status['StarSystem'] = candidate.system
        status['BodyName'] = candidate.body
        candidate.process_status(status)
        prepared = self.prepare_loaded_map(candidate, path)
        if prepared is None:
            return None
        candidate, path = prepared
        return dict(state=candidate, path=path,
                    source_text=translate(
                        'MapperWindow', 'PML [{pml_id}] prepared').format(pml_id=candidate.pml_id))

    def game_is_running(self):
        """Check the game process with a one-second throttle.

        The 50 ms poll loop calls this frequently, so results are cached until the
        next allowed check to avoid expensive process scans.
        """
        now = time.monotonic()
        if now >= self._next_game_running_check:
            self._game_running = bool(self._game_running_check())
            self._next_game_running_check = now + 1.0
        return self._game_running

    def set_offline(self):
        """Clear live-session telemetry without changing persistent map data."""
        had_live_state = bool(
            self.status_valid or self.live_status or self.transition_required
            or self.state.in_srv or self.state.fuel_percent is not None
            or self.state.rhino_lat is not None or self.state.rhino_lon is not None
            or self.state.rhino_heading is not None)
        try:
            self.last_mtime = self.status_path.stat().st_mtime_ns
        except OSError:
            self.last_mtime = None
        self.retry_status = False
        self.status_valid = False
        self.live_status = {}
        if self.transition_required:
            self.clear_transition_state()
        self.state.in_srv = False
        self.state.fuel_reservoir = None
        self.state.fuel_percent = None
        self.state.fuel_low = False
        self.state.rhino_lat = None
        self.state.rhino_lon = None
        self.state.rhino_heading = None
        if had_live_state:
            self.stop_assistance()
            self.view.radar.waves.clear()
        self.journal_identity.reset()
        self.info_left.setText(translate('MapperWindow', 'Waiting for Status.json'))

    def poll(self, reloading_map=False):
        """Poll game telemetry, update mapper state, and refresh presentation.

        The 50 ms timer calls this method. It reads Status.json only when needed,
        handles journal identity fallback, defers map transitions until after
        polling returns, and avoids overwriting a freshly loaded map with telemetry
        from another body.
        """
        changed = False
        if not self.game_is_running():
            before = self.map_signature()
            self.set_offline()
            self.refresh(redraw_map=before != self.map_signature())
            return
        try:
            status = read_status_if_changed(
                self.status_path,
                self.last_mtime,
                force=self.retry_status,
            )
            if (status is None and self.status_valid and self.live_status
                    and not self.live_status.get('StarSystem')):
                journal_identity = self.journal_identity.current_identity()
                if journal_identity is not None and journal_identity.system:
                    status = (self.last_mtime, dict(self.live_status))
            if status is not None:
                mtime, data = status
                self.retry_status = False
                if not data.get('StarSystem'):
                    journal_identity = self.journal_identity.current_identity()
                    if journal_identity is not None and journal_identity.system:
                        data = dict(data)
                        data['StarSystem'] = journal_identity.system
                if not data.get('StarSystem') and data.get('BodyName') == self.state.body:
                    # Match the same process_status rule before the body check
                    # used while opening a map file.
                    data['StarSystem'] = self.state.system
                # While opening a file, telemetry from another body must not erase
                # the map the user just selected.
                if reloading_map and f"{data.get('StarSystem','')}|{data.get('BodyName','')}" != self.state.body_key:
                    self.status_valid = False
                    self.last_mtime = mtime
                    self.info_left.setText(translate(
                        'MapperWindow', '{system} — {body} | Map loaded; Rhino on another body').format(
                            system=self.state.system, body=self.state.body))
                    self.refresh()
                    return
                incoming_system = data.get('StarSystem', '')
                incoming_body = data.get('BodyName', '')
                if not incoming_system and incoming_body == self.state.body:
                    incoming_system = self.state.system
                incoming_lat = data.get('Latitude')
                incoming_lon = data.get('Longitude')
                correspondence = None
                if (data.get('Flags', 0) & 0x04000000
                        and incoming_lat is not None and incoming_lon is not None):
                    correspondence = self.active_map_corresponds(
                        incoming_system, incoming_body,
                        float(incoming_lat), float(incoming_lon))
                old_body = self.state.body_key
                before = self.map_signature()
                status_update = self.state.process_status(
                    data, record_position=correspondence is not False)
                accepted = bool(status_update)
                self.live_status = data
                self.live_status['Flags'] = int(data.get('Flags', 0))
                self.status_valid = accepted
                if accepted:
                    self.latest_status_snapshot = copy.deepcopy(data)
                    if (self.transition_required
                            and self.latest_status_snapshot != self._transition_attempt_snapshot):
                        self.schedule_pending_transition()
                self.evaluate_status_update(status_update, correspondence, data)
                changed = before != self.map_signature()
                # Remember the timestamp only after a valid read is processed.
                self.last_mtime = mtime
                if accepted:
                    if not self.transition_required:
                        self.observe_steering()
                    if old_body != self.state.body_key:
                        self.cancel_placement()
                        self.view.scale = 0.08
                        self.view.recenter()
                        # Telemetry identifies the body; map operations decide
                        # whether a known PML exists within 10 km or the user
                        # must identify a new PML.
                        self.open_or_create_pml_for_current_position()
                        changed = True
                    if self.follow.isChecked():
                        self.view.recenter()
                    s = self.state
                    self.info_left.setText(translate(
                        'MapperWindow', '{system} — {body} | Lat {latitude:.5f}° Lon {longitude:.5f}° | Points {points}').format(
                            system=s.system, body=s.body, latitude=s.rhino_lat,
                            longitude=s.rhino_lon, points=len(s.points)))
                else:
                    flags = int(data.get('Flags', 0))
                    if flags != 0 and not (flags & 0x04000000):
                        self.info_left.setText(translate('MapperWindow', 'Commander is not in the SRV (on foot or aboard the ship).'))
                    else:
                        self.info_left.setText(translate('MapperWindow', 'Waiting for the Rhino position.'))
        except (OSError, ValueError, TypeError, AttributeError) as exc:
            self.status_valid = False
            self.retry_status = True
            self.info_left.setText(translate(
                'MapperWindow', 'Waiting for a valid Status.json read: {error}').format(error=exc))

        # Even without new telemetry, refresh navigation and blinking UI state.
        self.refresh(redraw_map=changed)

    def closeEvent(self, event):
        """Shut down timers, assistance input, child windows, and overlay safely.

        Closing first confirms the PML save/discard flow. Shutdown stops all
        periodic work and releases injected steering input before the Qt window is
        destroyed.
        """
        if not self.confirm_pml_exit():
            event.ignore()
            return
        self.timer.stop()
        self.radar_timer.stop()
        self.assist_timer.stop()
        self.stop_assistance()
        map_library = getattr(self, 'map_library', None)
        if map_library is not None:
            map_library.close()
        self.steering_input.close()
        self.overlay.close()
        super().closeEvent(event)


# This block runs only on direct execution, never when tests import the module.
# QApplication is singular, and exec() waits until all windows close.
def main():
    """Create the Qt application and run the event loop.

    The function installs translations, sets Windows taskbar identity when needed,
    keeps the main window alive for ``exec()``, and returns Qt's exit code.
    """
    # Set a distinct Windows taskbar identity when running through Python.
    if sys.platform == 'win32':
        import ctypes
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID('Rhino.SurfaceMapper')
    app = QApplication(sys.argv)
    app.setApplicationName('Rhino Surface Mapper')
    app.setWindowIcon(QIcon(str(Path(__file__).resolve().parent/'assets'/'Rhino_App.svg')))
    options = load_preferences(OPTIONS_PATH)
    translator = install_translator(app, options.get('language'))
    window = MapperWindow()
    window.show()
    return app.exec()


if __name__ == '__main__':
    raise SystemExit(main())
