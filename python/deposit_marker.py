"""Shared deposit marker painting for map widgets and read-only previews.

This module belongs to the Qt presentation layer. It converts deposit records
owned by the Qt-free core into measured painter geometry, while business rules
and persistence remain in the core modules and in ``settings_persistence.py``.
Rendering is intentionally side-effect free and must not mutate source data.
"""
from pathlib import Path
from PySide6.QtCore import QRectF, Qt
from PySide6.QtGui import QColor, QPen, QFontMetricsF
from PySide6.QtSvg import QSvgRenderer
from i18n import translate


_DEPOSIT_SIZE_LABELS = {
    'Pequeno': 'Small',
    'Médio': 'Medium',
    'Grande': 'Large',
    'Enorme': 'Huge',
}


def deposit_details(item):
    """Return the localized deposit size and rig count shown in marker cards.

    Args:
        item: Deposit mapping containing optional ``size`` and ``rigs`` keys.

    Returns:
        Human-readable detail text for the second line of the deposit card.
    """
    size = item.get('size', 'Pequeno')
    label = translate('DepositDialog', _DEPOSIT_SIZE_LABELS.get(size, size))
    return f"{label} · {item.get('rigs', 1)} rigs"


def deposit_bounds(point, font, item):
    """Measure the marker rectangle required to draw a deposit at ``point``.

    Args:
        point: Scene or widget coordinate used as the symbol anchor.
        font: Painter font used to measure real label extents.
        item: Deposit mapping containing optional ``name``, ``size`` and
            ``rigs`` values.

    Returns:
        A QRectF covering the icon, name and details without changing ``item``.
    """
    metrics = QFontMetricsF(font)
    line = max(20.0, metrics.height()+2)
    width = max(line+metrics.horizontalAdvance(item.get('name', 'Depósito')),
                metrics.horizontalAdvance(deposit_details(item)))+16
    return QRectF(point.x()-line/2, point.y()-line/2, width, line*2+8)


def draw_deposit(painter, point, item):
    """Draw a two-line deposit card with symbol, name, size and rig count.

    Args:
        painter: Active QPainter that owns the current font and target device.
        point: Anchor point for the mining icon at the left of the first line.
        item: Deposit mapping read for display only.

    Side effects:
        Temporarily changes painter state and restores it before returning.
    """
    painter.save()
    bounds = deposit_bounds(point, painter.font(), item)
    line = (bounds.height()-8)/2
    painter.setPen(QPen(QColor('white'), 2))
    painter.setBrush(QColor('#172b4d'))
    painter.drawRoundedRect(bounds, 5, 5)
    renderer = QSvgRenderer(str(Path(__file__).resolve().parent/'assets'/'mining.svg'))
    renderer.render(painter, QRectF(bounds.left()+5, bounds.top()+4, line-2, line-2))
    painter.setPen(QColor('white'))
    painter.drawText(QRectF(bounds.left()+line+8, bounds.top()+4,
                           bounds.width()-line-12, line), Qt.AlignmentFlag.AlignVCenter,
                     item.get('name', 'Depósito'))
    painter.drawText(QRectF(bounds.left()+8, bounds.top()+4+line,
                           bounds.width()-16, line), Qt.AlignmentFlag.AlignVCenter, deposit_details(item))
    painter.restore()
