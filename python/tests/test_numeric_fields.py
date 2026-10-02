"""Exercise custom numeric Qt fields used by options and marker dialogs.

These tests protect formatting, validators, and wrapping semantics that constrain
user-entered map and bearing values.
"""
import os
import unittest
# Qt must use the offscreen platform before any PySide6 import.
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')
from PySide6.QtWidgets import QApplication, QSizePolicy
from PySide6.QtGui import QValidator
from numeric_fields import MetresSpinBox, DegreesSpinBox, KilometresSpinBox


class NumericFieldsTests(unittest.TestCase):
    """Cover numeric-field widgets under a reused offscreen QApplication."""
    @classmethod
    def setUpClass(cls):
        # Reusing QApplication avoids duplicate global Qt application state.
        cls.app = QApplication.instance() or QApplication([])

    def test_formats_and_limits(self):
        metres, degrees, km = MetresSpinBox(), DegreesSpinBox(), KilometresSpinBox()
        try:
            metres.setValue(99999)
            degrees.setValue(7)
            km.setValue(12.3)
            self.assertEqual(metres.text(), '99999 m')
            self.assertEqual(degrees.text(), '007º')
            self.assertEqual(km.text(), '12,3 km')
            self.assertEqual(km.maximum(), 99.9)
            for field, texts in [(metres, ['100000', '1,5', '1.5']),
                                 (degrees, ['360', '-1', '1,5']),
                                 (km, ['100,0', '12,34'])]:
                self.assertEqual(field.sizePolicy().horizontalPolicy(), QSizePolicy.Policy.Fixed)
                for text in texts:
                    self.assertNotEqual(field.validate(text, len(text))[0], QValidator.State.Acceptable)
        finally:
            for field in (metres, degrees, km):
                field.close()

    def test_degrees_wrap_at_both_boundaries(self):
        degrees = DegreesSpinBox()
        try:
            self.assertEqual((degrees.minimum(), degrees.maximum()), (0, 359))
            self.assertEqual(degrees.singleStep(), 1)
            self.assertTrue(degrees.wrapping())
            degrees.setValue(0)
            degrees.stepBy(-1)
            self.assertEqual(degrees.value(), 359)
            degrees.setValue(359)
            degrees.stepBy(1)
            self.assertEqual(degrees.value(), 0)
        finally:
            degrees.close()
