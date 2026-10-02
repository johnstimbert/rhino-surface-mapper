"""Interactive transparent navigation overlay for the Mapper.

This module is a Qt presentation-layer widget. It displays navigation state
computed by the core, while persistence and business rules remain outside the
overlay and in ``settings_persistence.py``.
"""


from PySide6.QtCore import QPoint, QRectF, Qt
from PySide6.QtGui import QColor, QFont, QPainter, QPen
from PySide6.QtWidgets import QWidget


class OverlayWindow(QWidget):
    """Movable, resizable overlay that owns only transient presentation state.

    The widget owns hover, drag, resize and displayed-text state. Its lifecycle
    is normal Qt parent/window ownership, and it deliberately remains
    interactive instead of transparent to input so users can drag or resize it.
    """

    def __init__(self):
        """Configure the frameless, translucent, always-on-top tool window.

        Side effects:
            Sets window flags, widget attributes, mouse tracking, initial text
            state, the fixed 2.4:1 aspect ratio and the initial 360 × 150 size.
        """
        super().__init__()
        # FramelessWindowHint removes chrome; WindowStaysOnTopHint keeps the
        # overlay visible; Tool keeps it auxiliary rather than a taskbar window.
        self.setWindowFlags(
            Qt.WindowType.FramelessWindowHint
            | Qt.WindowType.WindowStaysOnTopHint
            | Qt.WindowType.Tool
        )
        # Per-pixel translucency lets the painter create a nearly invisible
        # surface that still receives mouse events inside the widget rectangle.
        self.setAttribute(Qt.WidgetAttribute.WA_TranslucentBackground, True)
        # Showing the overlay must not steal focus from the game window.
        self.setAttribute(Qt.WidgetAttribute.WA_ShowWithoutActivating, True)
        # WindowTransparentForInput is intentionally not set: this overlay is
        # interactive, draggable and resizable by design.
        self.setMouseTracking(True)

        self.hovered = False
        self.dragging = False
        self.resizing = False
        self.drag_offset = QPoint()
        # Ten pixels give practical edge targets for all eight resize modes.
        self.margin = 10
        # Width/height is fixed at 2.4:1 so the navigation typography scales
        # predictably and matches the default 360 × 150 window.
        self.aspect_ratio = 360 / 150
        self.heading_text = "—"
        self.distance_text = "—"
        self.heading_color = QColor("#888888")
        self.distance_color = QColor("white")
        self.target_name = ""
        self.assistance_notice = ''
        self.assistance_warning = False
        self.resize(360, 150)

    def set_navigation(self, heading_text, distance_text, heading_color, distance_color, target_name=""):
        """Update navigation labels and colours supplied by the core.

        Args:
            heading_text: Heading display text, already formatted by callers.
            distance_text: Distance display text, already formatted by callers.
            heading_color: QColor-compatible colour for the heading.
            distance_color: QColor-compatible colour for the distance.
            target_name: Optional destination label shown above the heading.

        Side effects:
            Repaints only when any displayed value changes.
        """
        if (self.heading_text == heading_text and self.distance_text == distance_text
                and self.heading_color == QColor(heading_color)
                and self.distance_color == QColor(distance_color)
                and self.target_name == target_name):
            return
        self.heading_text = heading_text
        self.distance_text = distance_text
        self.heading_color = QColor(heading_color)
        self.distance_color = QColor(distance_color)
        self.target_name = target_name
        self.update()

    def set_assistance_notice(self, text, warning=False):
        """Show or update the steering-assistance notice band.

        Args:
            text: Notice text. An empty string removes the band.
            warning: Whether to use the warning colour instead of the normal
                assistance colour.

        Side effects:
            Requests a repaint when notice state changes.
        """
        if text != self.assistance_notice or warning != self.assistance_warning:
            self.assistance_notice = text
            self.assistance_warning = warning
            self.update()

    def paintEvent(self, event):
        """Paint the overlay background, border, navigation text and notice.

        Args:
            event: Qt paint event supplied by the framework.

        Painting rules:
            The fill is almost transparent; the red border appears only while
            hovered, dragging or resizing. Target names are blue, while heading
            and distance colours are supplied by the core. The optional
            assistance notice occupies a bottom band.
        """
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)

        # Alpha 1 keeps the widget effectively invisible but mouse-addressable.
        painter.fillRect(self.rect(), QColor(255, 255, 255, 1))

        # The border is a mode indicator, not permanent decoration.
        if self.hovered or self.dragging or self.resizing:
            painter.setPen(QPen(QColor(255, 0, 0, 220), 2))
            painter.drawRect(1, 1, self.width() - 2, self.height() - 2)

        painter.save()
        if self.assistance_notice:
            painter.scale(1,.78)
        if self.target_name:
            name_font = QFont("Segoe UI", max(10, int(self.height() * 0.17)), QFont.Weight.DemiBold)
            heading_font = QFont("Segoe UI", max(13, int(self.height() * 0.28)), QFont.Weight.Bold)
            distance_font = QFont("Segoe UI", max(11, int(self.height() * 0.22)), QFont.Weight.Bold)

            h = float(self.height())
            w = float(self.width())
            painter.setPen(QColor("#80c8ff"))
            painter.setFont(name_font)
            # The destination may include a type prefix such as "[Deposit] ...".
            # Shrink only this label so a too-wide centered string does not lose
            # its most identifying characters at the start.
            while (painter.fontMetrics().horizontalAdvance(self.target_name) > w - 12
                   and name_font.pointSize() > 8):
                name_font.setPointSize(name_font.pointSize() - 1)
                painter.setFont(name_font)
            painter.drawText(
                QRectF(6.0, 4.0, w - 12.0, h * 0.24),
                Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter,
                self.target_name,
            )

            painter.setPen(self.heading_color)
            painter.setFont(heading_font)
            painter.drawText(
                QRectF(0.0, h * 0.26, w, h * 0.40),
                Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter,
                self.heading_text,
            )

            painter.setPen(self.distance_color)
            painter.setFont(distance_font)
            painter.drawText(
                QRectF(0.0, h * 0.66, w, h * 0.30),
                Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter,
                self.distance_text,
            )
        else:
            heading_font = QFont("Segoe UI", max(14, int(self.height() * 0.32)), QFont.Weight.Bold)
            distance_font = QFont("Segoe UI", max(12, int(self.height() * 0.23)), QFont.Weight.Bold)

            painter.setPen(self.heading_color)
            painter.setFont(heading_font)
            painter.drawText(
                self.rect().adjusted(0, 8, 0, -self.height() // 2),
                Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter,
                self.heading_text,
            )

            painter.setPen(self.distance_color)
            painter.setFont(distance_font)
            painter.drawText(
                self.rect().adjusted(0, self.height() // 2 - 8, 0, -8),
                Qt.AlignmentFlag.AlignHCenter | Qt.AlignmentFlag.AlignVCenter,
                self.distance_text,
            )

        painter.restore()
        if self.assistance_notice:
            zone = QRectF(3,self.height()*.80,self.width()-6,self.height()*.18)
            painter.fillRect(zone,QColor('#18232e'))
            painter.setPen(QColor('#ffd21c' if self.assistance_warning else '#a8d7eb'))
            font = QFont('Segoe UI',max(8,int(self.height()*.095)),QFont.Weight.Bold)
            painter.setFont(font)
            while painter.fontMetrics().horizontalAdvance(self.assistance_notice)>zone.width()-6 and font.pointSize()>5:
                font.setPointSize(font.pointSize()-1)
                painter.setFont(font)
            painter.drawText(zone,Qt.AlignmentFlag.AlignCenter,self.assistance_notice)

    def enterEvent(self, event):
        """Mark hover state when the mouse enters the overlay.

        Args:
            event: Qt enter event supplied by the framework.

        Side effects:
            Requests a repaint so the red interaction border can appear.
        """
        self.hovered = True
        self.update()

    def leaveEvent(self, event):
        """Clear hover state after the mouse leaves when no drag is active.

        Args:
            event: Qt leave event supplied by the framework.

        Side effects:
            Restores the arrow cursor and repaints the border state.
        """
        if not self.dragging and not self.resizing:
            self.hovered = False
        self.setCursor(Qt.CursorShape.ArrowCursor)
        self.update()

    def hit_test(self, pos):
        """Classify a local mouse position into move or resize zones.

        Args:
            pos: Local widget coordinate.

        Returns:
            One of the eight resize directions (``n``, ``s``, ``e``, ``w``,
            ``ne``, ``nw``, ``se``, ``sw``) or ``move`` for the interior.
        """
        x, y = pos.x(), pos.y()
        w, h, m = self.width(), self.height(), self.margin
        left, right = x <= m, x >= w - m
        top, bottom = y <= m, y >= h - m
        if top and left:
            return "nw"
        if top and right:
            return "ne"
        if bottom and left:
            return "sw"
        if bottom and right:
            return "se"
        if left:
            return "w"
        if right:
            return "e"
        if top:
            return "n"
        if bottom:
            return "s"
        return "move"

    def mouseMoveEvent(self, event):
        """Update the cursor, drag the window or resize from a prior press.

        Args:
            event: Qt mouse event with local and global positions.

        Notes:
            Global mouse coordinates keep the reference stable while the window
            itself is moving or changing size.
        """
        mode = self.hit_test(event.position().toPoint())
        if not event.buttons():
            cursors = {
                "nw": Qt.CursorShape.SizeFDiagCursor, "se": Qt.CursorShape.SizeFDiagCursor,
                "ne": Qt.CursorShape.SizeBDiagCursor, "sw": Qt.CursorShape.SizeBDiagCursor,
                "w": Qt.CursorShape.SizeHorCursor, "e": Qt.CursorShape.SizeHorCursor,
                "n": Qt.CursorShape.SizeVerCursor, "s": Qt.CursorShape.SizeVerCursor,
                "move": Qt.CursorShape.SizeAllCursor,
            }
            self.setCursor(cursors[mode])
            return

        global_pos = event.globalPosition().toPoint()
        if self.dragging:
            self.move(global_pos - self.drag_offset)
        elif self.resizing:
            self.resize_proportional(global_pos)
        self.update()

    def resize_proportional(self, global_pos):
        """Resize the overlay while preserving the fixed 2.4:1 aspect ratio.

        Args:
            global_pos: Current global mouse coordinate.

        Side effects:
            Sets the widget geometry. The dragged edge or corner drives the
            change, the opposite side stays anchored, simple edges grow the
            other axis around the centre, and width never goes below 180 px.
        """
        start = self.start_geometry
        dx = global_pos.x() - self.press_global.x()
        dy = global_pos.y() - self.press_global.y()
        min_w = 180
        min_h = round(min_w / self.aspect_ratio)
        mode = self.resize_mode

        # Corner drags use the axis with the larger movement and derive the
        # other dimension from the fixed aspect ratio.
        if mode in ("se", "sw", "ne", "nw"):
            if abs(dx) >= abs(dy):
                new_w = max(min_w, start.width() + (dx if "e" in mode else -dx))
                new_h = max(min_h, round(new_w / self.aspect_ratio))
            else:
                new_h = max(min_h, start.height() + (dy if "s" in mode else -dy))
                new_w = max(min_w, round(new_h * self.aspect_ratio))
            x = start.x() if "e" in mode else start.x() + start.width() - new_w
            y = start.y() if "s" in mode else start.y() + start.height() - new_h
        elif mode in ("n", "s"):
            new_h = max(min_h, start.height() + (dy if mode == "s" else -dy))
            new_w = max(min_w, round(new_h * self.aspect_ratio))
            x = start.x() + (start.width() - new_w) // 2
            y = start.y() if mode == "s" else start.y() + start.height() - new_h
        else:
            new_w = max(min_w, start.width() + (dx if mode == "e" else -dx))
            new_h = max(min_h, round(new_w / self.aspect_ratio))
            x = start.x() if mode == "e" else start.x() + start.width() - new_w
            y = start.y() + (start.height() - new_h) // 2

        self.setGeometry(int(x), int(y), int(new_w), int(new_h))

    def mousePressEvent(self, event):
        """Start a drag or resize operation from a left-button press.

        Args:
            event: Qt mouse event containing the pressed button and positions.

        Side effects:
            Stores global press position and starting geometry for later moves.
        """
        if event.button() != Qt.MouseButton.LeftButton:
            return
        mode = self.hit_test(event.position().toPoint())
        self.press_global = event.globalPosition().toPoint()
        self.start_geometry = self.geometry()
        self.resize_mode = mode
        if mode == "move":
            self.dragging = True
            self.drag_offset = event.globalPosition().toPoint() - self.frameGeometry().topLeft()
        else:
            self.resizing = True
        self.update()

    def mouseReleaseEvent(self, event):
        """Finish the current drag or resize operation.

        Args:
            event: Qt mouse event containing the released button.

        Side effects:
            Clears interaction flags and leaves the hover border visible.
        """
        if event.button() == Qt.MouseButton.LeftButton:
            self.dragging = False
            self.resizing = False
            self.hovered = True
            self.update()
