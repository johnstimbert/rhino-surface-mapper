"""Numeric Qt input fields with consistent units, locale and sizing.

These widgets are part of the Qt presentation layer. They present values that
the core consumes elsewhere; business rules and persistence remain in the
Qt-free modules and in ``settings_persistence.py``.
"""
from PySide6.QtCore import QLocale
from PySide6.QtWidgets import QSpinBox, QDoubleSpinBox, QSizePolicy, QWidget, QHBoxLayout, QLabel


def compact(field):
    """Apply the shared locale and fixed sizing policy to a numeric field.

    Args:
        field: QSpinBox or QDoubleSpinBox instance being configured.

    Returns:
        The same field, allowing constructor-style use.

    Notes:
        Portuguese locale formatting is used without group separators so metres,
        degrees and kilometres remain compact and predictable across fields.
    """
    locale = QLocale(QLocale.Language.Portuguese, QLocale.Country.Portugal)
    locale.setNumberOptions(QLocale.NumberOption.OmitGroupSeparator | QLocale.NumberOption.RejectGroupSeparator)
    field.setLocale(locale)
    field.setGroupSeparatorShown(False)
    # Qt sizeHint already includes digits, unit suffix, arrows, font and DPI scale.
    field.setSizePolicy(QSizePolicy.Policy.Fixed, QSizePolicy.Policy.Fixed)
    return field


class MetresSpinBox(QSpinBox):
    """Spin box for metre values in the inclusive range 0 m to 99,999 m."""

    def __init__(self, parent=None):
        """Create a metre field with the shared locale and compact width policy.

        Args:
            parent: Optional Qt parent that owns the widget lifecycle.
        """
        super().__init__(parent)
        self.setRange(0, 99999)
        self.setSuffix(' m')
        compact(self)


class DegreesSpinBox(QSpinBox):
    """Spin box for headings as wrapped, zero-padded degrees from 000º to 359º."""

    def __init__(self, parent=None):
        """Create a wrapped degree field with the shared compact formatting.

        Args:
            parent: Optional Qt parent that owns the widget lifecycle.
        """
        super().__init__(parent)
        self.setRange(0, 359)
        self.setWrapping(True)
        self.setSuffix('º')
        compact(self)

    def textFromValue(self, value):
        """Format degrees as three digits so bearings align visually.

        Args:
            value: Integer value supplied by QSpinBox after range wrapping.

        Returns:
            Zero-padded degree text without the suffix added by Qt.
        """
        return f'{value:03d}'


class KilometresSpinBox(QDoubleSpinBox):
    """Spin box for kilometre distances from 0.0 km to 99.9 km."""

    def __init__(self, parent=None):
        """Create a one-decimal kilometre field with 0.1 km increments.

        Args:
            parent: Optional Qt parent that owns the widget lifecycle.
        """
        super().__init__(parent)
        self.setDecimals(1)
        self.setRange(0, 99.9)
        self.setSingleStep(.1)
        self.setSuffix(' km')
        compact(self)


def labelled_field(text, field):
    """Return a compact label-and-field widget pair.

    Args:
        text: Label text shown to the left of the field.
        field: Input widget used as the label buddy.

    Returns:
        QWidget containing the label and field with fixed sizing.
    """
    pair = QWidget()
    row = QHBoxLayout(pair)
    row.setContentsMargins(0, 0, 0, 0)
    row.setSpacing(4)
    label = QLabel(text)
    label.setBuddy(field)
    row.addWidget(label)
    row.addWidget(field)
    pair.setSizePolicy(QSizePolicy.Policy.Fixed, QSizePolicy.Policy.Fixed)
    return pair
