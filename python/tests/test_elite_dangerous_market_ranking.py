"""Tests for deterministic market observation ranking semantics."""

import unittest

from elite_dangerous.market import (
    CommodityMarketResult,
    MarketIssue,
    MarketObservation,
    Station,
    rank_market_observations,
)


def observation(
    station_name,
    *,
    price=100,
    demand=10,
    pad="L",
):
    """Build a ranking fixture with configurable price, demand, and pad size."""
    return MarketObservation(
        station=Station(station_name, station_name, "System", False, pad),
        commodity="Platinum",
        sell_price=price,
        demand=demand,
        supply=None,
        market_updated_at=None,
        station_updated_at=None,
    )


class MarketRankingTests(unittest.TestCase):
    """Covers ranking by price, demand, pad size, and stable ties."""

    def test_sell_price_is_descending(self):
        low = observation("Low", price=10)
        high = observation("High", price=20)
        self.assertEqual(rank_market_observations((low, high)), (high, low))

    def test_equal_prices_are_ordered_by_demand_descending(self):
        low = observation("Low demand", demand=5)
        high = observation("High demand", demand=20)
        self.assertEqual(rank_market_observations((low, high)), (high, low))

    def test_equal_price_and_demand_are_ordered_by_pad_capability(self):
        small = observation("S", pad="S")
        medium = observation("M", pad="M")
        large = observation("L", pad="L")
        unknown = observation("Unknown", pad=None)
        self.assertEqual(
            rank_market_observations((small, unknown, large, medium)),
            (large, medium, small, unknown),
        )

    def test_unknown_price_ranks_after_known_price(self):
        unknown = observation("Unknown", price=None)
        zero = observation("Zero", price=0)
        self.assertEqual(rank_market_observations((unknown, zero)), (zero, unknown))

    def test_unknown_demand_ranks_after_known_demand_when_price_ties(self):
        unknown = observation("Unknown", demand=None)
        zero = observation("Zero", demand=0)
        self.assertEqual(rank_market_observations((unknown, zero)), (zero, unknown))

    def test_unknown_pad_ranks_after_small_when_earlier_criteria_tie(self):
        unknown = observation("Unknown", pad=None)
        small = observation("Small", pad="S")
        self.assertEqual(rank_market_observations((unknown, small)), (small, unknown))

    def test_exact_ranking_ties_preserve_input_order_and_input_is_unchanged(self):
        first = observation("First")
        second = observation("Second")
        inputs = [first, second]
        ranked = rank_market_observations(inputs)
        self.assertEqual(ranked, (first, second))
        self.assertEqual(inputs, [first, second])

    def test_ranking_partial_result_preserves_result_and_issues(self):
        issue = MarketIssue("Station B", "Market unavailable")
        high = observation("High", price=200)
        low = observation("Low", price=100)
        result = CommodityMarketResult("Platinum", (low, high), (issue,))

        ranked = rank_market_observations(result.observations)

        self.assertEqual(ranked, (high, low))
        self.assertEqual(result.observations, (low, high))
        self.assertEqual(result.issues, (issue,))
        self.assertFalse(result.is_complete)


if __name__ == "__main__":
    unittest.main()
