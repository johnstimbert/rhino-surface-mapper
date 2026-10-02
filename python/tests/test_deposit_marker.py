"""Exercise deposit marker display text and translation context usage.

These tests protect canonical persisted size values while ensuring displayed labels
are translated through the owning deposit-dialog context.
"""
import unittest
from unittest.mock import patch

from deposit_marker import deposit_details


class DepositMarkerTests(unittest.TestCase):
    """Cover deposit marker text without mutating persisted item data."""
    def test_deposit_details_translates_canonical_size_without_mutating_item(self):
        item = {'size': 'Grande', 'rigs': 3}
        with patch('deposit_marker.translate', side_effect=lambda context, text: f'{context}:{text}') as translate:
            result = deposit_details(item)

        self.assertEqual(result, 'DepositDialog:Large · 3 rigs')
        translate.assert_called_once_with('DepositDialog', 'Large')
        self.assertEqual(item, {'size': 'Grande', 'rigs': 3})


if __name__ == '__main__':
    unittest.main()
