"""Tests for local market filtering and fleet-carrier exclusion policy."""

import unittest

from elite_dangerous.market import (
    MarketObservation,
    Station,
    filter_market_observations,
    is_carrier_name,
)


def observation(
    station_name="Portside",
    *,
    demand=10,
    sell_price=100,
):
    """Build a policy-focused observation with configurable market facts."""
    return MarketObservation(
        station=Station(station_name, "1", "System", False, "L"),
        commodity="Platinum",
        sell_price=sell_price,
        demand=demand,
        supply=None,
        market_updated_at=None,
        station_updated_at=None,
    )


class MarketPolicyTests(unittest.TestCase):
    """Covers carrier detection, demand thresholds, and sell-price filtering."""

    def test_carrier_name_shapes_strip_outer_whitespace(self):
        # Carrier names arrive as both four-character IDs and three-three identifiers.
        for name in (" FC01 ", " ABC-123 ", "Ab9-xY0"):
            with self.subTest(name=name):
                self.assertTrue(is_carrier_name(name))

    def test_carrier_name_near_matches_are_not_classified(self):
        for name in (
            "ABC-12",
            "ABCD-123",
            "ABC-1234",
            "ABC-123-X",
            "ABC_123",
            "ÄBC-123",
            "Long Carrier Station",
        ):
            with self.subTest(name=name):
                self.assertFalse(is_carrier_name(name))

    def test_four_character_fleet_carrier_is_excluded_by_default(self):
        carrier = observation(" FC01 ")
        self.assertEqual(filter_market_observations((carrier,)), ())

    def test_three_three_carrier_is_excluded_by_default(self):
        carrier = observation(" ABC-123 ")
        self.assertEqual(filter_market_observations((carrier,)), ())

    def test_carriers_can_be_retained_explicitly(self):
        fleet_carrier = observation("FC01")
        carrier = observation("ABC-123")
        self.assertEqual(
            filter_market_observations(
                (fleet_carrier, carrier),
                exclude_carriers=False,
            ),
            (fleet_carrier, carrier),
        )

    def test_no_demand_threshold_preserves_unknown_demand(self):
        unknown = observation(demand=None)
        self.assertEqual(filter_market_observations((unknown,)), (unknown,))

    def test_demand_threshold_is_strict_and_excludes_unknown(self):
        below = observation(demand=9)
        equal = observation(demand=10)
        above = observation(demand=11)
        unknown = observation(demand=None)
        self.assertEqual(
            filter_market_observations(
                (below, equal, above, unknown),
                demand_greater_than=10,
            ),
            (above,),
        )

    def test_demand_filter_does_not_mutate_input(self):
        unknown = observation(demand=None)
        sufficient = observation(demand=11)
        inputs = [unknown, sufficient]
        filtered = filter_market_observations(inputs, demand_greater_than=10)
        self.assertEqual(inputs, [unknown, sufficient])
        self.assertEqual(filtered, (sufficient,))

    def test_positive_sell_price_filter_is_opt_in(self):
        unknown = observation(sell_price=None)
        zero = observation(sell_price=0)
        negative = observation(sell_price=-1)
        positive = observation(sell_price=1)
        observations = (unknown, zero, negative, positive)
        self.assertEqual(filter_market_observations(observations), observations)
        self.assertEqual(
            filter_market_observations(
                observations,
                require_positive_sell_price=True,
            ),
            (positive,),
        )

    def test_market_policy_does_not_mutate_partial_result(self):
        from elite_dangerous.market import (
            CommodityMarketResult,
            MarketIssue,
        )

        issue = MarketIssue("Station B", "Market unavailable")
        available = observation("Portside", demand=None)
        result = CommodityMarketResult("Platinum", (available,), (issue,))

        filtered = filter_market_observations(
            result.observations,
            demand_greater_than=5,
        )

        self.assertEqual(filtered, ())
        self.assertEqual(result.observations, (available,))
        self.assertEqual(result.issues, (issue,))
        self.assertFalse(result.is_complete)


if __name__ == "__main__":
    unittest.main()
