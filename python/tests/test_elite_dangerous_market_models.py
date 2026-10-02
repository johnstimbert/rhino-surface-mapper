"""Tests for immutable Elite Dangerous market domain model behavior."""

import unittest
from dataclasses import FrozenInstanceError
from datetime import datetime, timezone

from elite_dangerous.market import (
    CommodityMarketResult,
    CommodityPriceSummary,
    CommoditySummaryResult,
    MarketIssue,
    MarketObservation,
    Station,
)
from elite_dangerous.market.commodities import SURFACE_COMMODITIES


def make_station(max_landing_pad="L"):
    """Build a reusable station fixture with configurable pad capability."""
    return Station(
        name="Test Station",
        market_id="12345",
        system_name="Test System",
        is_planetary=False,
        max_landing_pad=max_landing_pad,
    )


def make_observation(commodity="Platinum", station=None):
    """Build a market observation fixture with known prices and timestamps."""
    return MarketObservation(
        station=station or make_station(),
        commodity=commodity,
        sell_price=100,
        demand=25,
        supply=0,
        market_updated_at=datetime(2026, 9, 15, tzinfo=timezone.utc),
        station_updated_at=datetime(2026, 9, 15, tzinfo=timezone.utc),
    )


def make_summary(commodity):
    """Build a commodity summary fixture with simple global price values."""
    return CommodityPriceSummary(
        commodity=commodity,
        average_sell=100,
        maximum_sell=200,
        total_demand=None,
    )


class MarketModelTests(unittest.TestCase):
    """Covers market dataclass immutability, completeness, and normalization."""

    def test_station_supports_each_pad_size_and_unknown_capability(self):
        for pad in ("L", "M", "S", None):
            with self.subTest(pad=pad):
                self.assertEqual(make_station(pad).max_landing_pad, pad)

    def test_models_are_frozen(self):
        station = make_station()
        with self.assertRaises(FrozenInstanceError):
            station.name = "Changed"

        observation = make_observation(station=station)
        with self.assertRaises(FrozenInstanceError):
            observation.sell_price = 0

        issue = MarketIssue("Station B", "Unavailable")
        with self.assertRaises(FrozenInstanceError):
            issue.message = "Changed"

        market_result = CommodityMarketResult("Platinum", (), ())
        with self.assertRaises(FrozenInstanceError):
            market_result.observations = (make_observation(),)

        summary = make_summary("Platinum")
        with self.assertRaises(FrozenInstanceError):
            summary.average_sell = 0

        summary_result = CommoditySummaryResult(("Platinum",), (summary,))
        with self.assertRaises(FrozenInstanceError):
            summary_result.summaries = ()

    def test_observation_accepts_unavailable_market_values(self):
        observation = MarketObservation(
            station=make_station(None),
            commodity="Platinum",
            sell_price=None,
            demand=None,
            supply=None,
            market_updated_at=None,
            station_updated_at=None,
        )
        self.assertIsNone(observation.sell_price)
        self.assertIsNone(observation.demand)
        self.assertIsNone(observation.supply)
        self.assertIsNone(observation.market_updated_at)
        self.assertIsNone(observation.station_updated_at)

    def test_result_collections_require_tuples(self):
        with self.assertRaises(TypeError):
            CommodityMarketResult("Platinum", [], ())
        with self.assertRaises(TypeError):
            CommoditySummaryResult([], (), ())

    def test_complete_commodity_market_result(self):
        result = CommodityMarketResult(
            requested_commodity="Platinum",
            observations=(make_observation(),),
            issues=(),
        )
        self.assertTrue(result.is_complete)
        self.assertEqual(result.missing, ())

    def test_market_result_without_matching_observation_is_incomplete(self):
        result = CommodityMarketResult("Platinum", (), ())
        self.assertFalse(result.is_complete)
        self.assertEqual(result.missing, ("Platinum",))

    def test_observations_and_recoverable_issues_coexist(self):
        issue = MarketIssue("Station B", "Market details unavailable")
        result = CommodityMarketResult(
            "Platinum",
            (make_observation(),),
            (issue,),
        )
        self.assertEqual(result.observations, (make_observation(),))
        self.assertEqual(result.issues, (issue,))
        self.assertFalse(result.is_complete)
        self.assertEqual(result.observations[0].sell_price, 100)

    def test_complete_summary_result(self):
        requested = ("Bastnäsite", "Methanol Crystals")
        result = CommoditySummaryResult(
            requested=requested,
            summaries=tuple(make_summary(name) for name in requested),
        )
        self.assertTrue(result.is_complete)
        self.assertEqual(result.missing, ())

    def test_35_of_37_summaries_preserve_values_and_report_missing(self):
        # The current catalogue has 37 products; 35 summaries leave two known gaps.
        missing_names = {"Bastnäsite", "Methanol Crystals"}
        summaries = tuple(
            make_summary(name)
            for name in SURFACE_COMMODITIES
            if name not in missing_names
        )
        result = CommoditySummaryResult(SURFACE_COMMODITIES, summaries)

        self.assertEqual(len(result.summaries), 35)
        self.assertEqual(set(result.missing), missing_names)
        self.assertEqual(len(result.missing), 2)
        self.assertFalse(result.is_complete)
        self.assertEqual(result.summaries[0].average_sell, 100)

    def test_summary_comparison_does_not_depend_on_return_order(self):
        requested = ("Platinum", "Gold", "Silver")
        summaries = tuple(make_summary(name) for name in reversed(requested))
        result = CommoditySummaryResult(requested, summaries)
        self.assertTrue(result.is_complete)
        self.assertEqual(result.missing, ())

    def test_alias_and_name_normalization_do_not_create_false_missing_items(self):
        requested = ("Bastnäsite", "Low Temperature Diamonds")
        result = CommoditySummaryResult(
            requested=requested,
            summaries=(
                make_summary("Bastnasite"),
                make_summary("LOW-temperature diamonds"),
            ),
        )
        self.assertTrue(result.is_complete)
        self.assertEqual(result.missing, ())

    def test_distinct_unknown_names_with_empty_normalized_keys_do_not_match(self):
        result = CommoditySummaryResult(
            requested=("???",),
            summaries=(make_summary("!!!"),),
        )
        self.assertEqual(result.missing, ("???",))
        self.assertFalse(result.is_complete)

    def test_unknown_names_with_nonempty_normalized_keys_still_match(self):
        result = CommoditySummaryResult(
            requested=("Unknown-Commodity",),
            summaries=(make_summary("unknown commodity"),),
        )
        self.assertEqual(result.missing, ())
        self.assertTrue(result.is_complete)

    def test_duplicate_canonical_requests_are_reported_missing_once(self):
        result = CommoditySummaryResult(
            requested=("Platinum", "Platinum", "Gold"),
            summaries=(),
        )
        self.assertEqual(result.missing, ("Platinum", "Gold"))

    def test_canonical_and_alias_requests_are_reported_missing_once(self):
        result = CommoditySummaryResult(
            requested=("Bastnäsite", "Bastnasite", "Methanol Crystals"),
            summaries=(),
        )
        self.assertEqual(result.missing, ("Bastnäsite", "Methanol Crystals"))

    def test_summary_issues_preserve_available_data(self):
        issue = MarketIssue("INARA", "Some entries were unavailable")
        summary = make_summary("Platinum")
        result = CommoditySummaryResult(
            requested=("Platinum", "Gold"),
            summaries=(summary,),
            issues=(issue,),
        )
        self.assertEqual(result.summaries, (summary,))
        self.assertEqual(result.issues, (issue,))
        self.assertEqual(result.missing, ("Gold",))
        self.assertFalse(result.is_complete)
        self.assertEqual(result.summaries[0].maximum_sell, 200)
