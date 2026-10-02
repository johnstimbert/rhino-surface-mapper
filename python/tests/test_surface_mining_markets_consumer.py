"""Offline tests for the Surface Mining Markets consumer orchestration."""

from datetime import datetime, timedelta, timezone
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

from elite_dangerous.market import (
    CacheFormatError,
    CommodityMarketResult,
    CommodityPriceSummary,
    CommoditySummaryResult,
    LandingPad,
    MarketIssue,
    MarketObservation,
    Station,
    SummaryCache,
    SURFACE_COMMODITIES,
)


UTC_NOW = datetime(2026, 9, 16, 12, 0, tzinfo=timezone.utc)
SCRIPT_PATH = (
    Path(__file__).resolve().parents[1]
    / "tests"
    / "surface_mining_markets_consumer.py"
)


def load_consumer(module_name="surface_mining_markets_consumer_test"):
    """Import the consumer script under a fresh module name for side-effect checks."""
    spec = importlib.util.spec_from_file_location(module_name, SCRIPT_PATH)
    if spec is None or spec.loader is None:
        raise RuntimeError("Could not load the Surface Mining Markets consumer.")
    module = importlib.util.module_from_spec(spec)
    sys.modules[module_name] = module
    spec.loader.exec_module(module)
    return module


CONSUMER = load_consumer()


def observation(
    commodity: str,
    station_name: str,
    *,
    price: int | None,
    demand: int | None,
    pad: LandingPad | None = "L",
) -> MarketObservation:
    """Build a local market observation fixture for consumer orchestration tests."""
    return MarketObservation(
        station=Station(
            station_name,
            f"market-{station_name}",
            "Kappa",
            True,
            pad,
        ),
        commodity=commodity,
        sell_price=price,
        demand=demand,
        supply=10,
        market_updated_at=UTC_NOW,
        station_updated_at=UTC_NOW,
    )


def summary_result(
    *,
    commodities: tuple[str, ...] | None = None,
    issues: tuple[MarketIssue, ...] = (),
) -> CommoditySummaryResult:
    """Build an INARA summary result that defaults to the full surface catalogue."""
    requested = tuple(SURFACE_COMMODITIES)
    names = tuple(SURFACE_COMMODITIES) if commodities is None else commodities
    return CommoditySummaryResult(
        requested=requested,
        summaries=tuple(
            CommodityPriceSummary(name, 100, 200, None)
            for name in names
        ),
        issues=issues,
    )


class SurfaceMiningConsumerTests(unittest.TestCase):
    """Covers local market orchestration, cache policy, and script entry points."""

    def test_one_batch_fetch_uses_catalogue_and_reusable_policy_and_ranking(self):
        issue = MarketIssue("Bad Station", "market request failed")
        # ABCD and ABC-123 represent the carrier name shapes excluded by policy.
        observations = (
            observation("Platinum", "Market One", price=500, demand=101, pad="S"),
            observation("Platinum", "ABCD", price=900, demand=500),
            observation("Platinum", "ABC-123", price=900, demand=500),
            observation("Platinum", "Market Two", price=500, demand=100),
            observation("Platinum", "Market Three", price=500, demand=None),
            observation("Platinum", "Market Four", price=0, demand=500),
            observation("Platinum", "Market Five", price=600, demand=102, pad="L"),
        )
        batch = tuple(
            CommodityMarketResult("Platinum", observations, (issue,))
            for _ in range(2)
        )
        progress = []

        with patch.object(CONSUMER, "fetch_commodity_markets", return_value=batch) as fetch:
            exact_name, grouped, issues = CONSUMER.query_system_markets(
                "kappa",
                progress=progress.append,
            )

        fetch.assert_called_once_with("kappa", SURFACE_COMMODITIES)
        self.assertIs(CONSUMER.SURFACE_COMMODITIES, SURFACE_COMMODITIES)
        self.assertEqual(exact_name, "Kappa")
        self.assertEqual(
            tuple(item.station.name for item in grouped["Platinum"]),
            ("Market Five", "Market One"),
        )
        self.assertEqual(issues, (issue,))
        self.assertEqual(len(progress), 2)

    def test_product_order_uses_best_observation_and_stable_ties(self):
        results = {
            "Platinum": (observation("Platinum", "P", price=1000, demand=500, pad="L"),),
            "Gold": (observation("Gold", "G", price=1000, demand=500, pad="L"),),
            "Water": (observation("Water", "W", price=1001, demand=1, pad="S"),),
            "Palladium": (observation("Palladium", "Pd", price=999, demand=999, pad="L"),),
            "Silver": (),
        }

        selected = CONSUMER.select_top_products(results)

        self.assertEqual(
            tuple(product for product, _ in selected),
            ("Water", "Platinum", "Gold"),
        )
        self.assertEqual(selected[1][1], results["Platinum"])

    def test_current_consumer_cache_path_is_separate_from_legacy_cache(self):
        legacy_path = SCRIPT_PATH.parent / "inara_commodities_cache.json"

        self.assertEqual(CONSUMER.CACHE_PATH.name, "inara_summary_cache_v1.json")
        self.assertEqual(CONSUMER.CACHE_PATH.parent, SCRIPT_PATH.parent)
        self.assertNotEqual(CONSUMER.CACHE_PATH, legacy_path)

    def test_orchestration_uses_only_the_new_cache_and_does_not_migrate_legacy(self):
        legacy_path = SCRIPT_PATH.parent / "inara_commodities_cache.json"
        legacy_before = legacy_path.read_bytes() if legacy_path.exists() else None
        cached = SummaryCache(summary_result(), UTC_NOW)
        with tempfile.TemporaryDirectory() as directory:
            new_cache_path = Path(directory) / "inara_summary_cache_v1.json"
            with (
                patch.object(CONSUMER, "load_summary_cache", return_value=cached) as load,
                patch.object(CONSUMER, "fetch_inara_html") as acquire,
                patch.object(CONSUMER, "save_summary_cache") as save,
            ):
                result, status, _, warning = CONSUMER.load_or_refresh_inara(
                    new_cache_path,
                    now=UTC_NOW,
                )

        load.assert_called_once_with(new_cache_path, SURFACE_COMMODITIES)
        acquire.assert_not_called()
        save.assert_not_called()
        self.assertIs(result, cached.result)
        self.assertEqual(status, "fresh cache")
        self.assertIsNone(warning)
        legacy_after = legacy_path.read_bytes() if legacy_path.exists() else None
        self.assertEqual(legacy_after, legacy_before)

    def test_top_product_limit_zero_returns_no_products(self):
        results = {
            "Platinum": (observation("Platinum", "P", price=100, demand=101),)
        }

        self.assertEqual(CONSUMER.select_top_products(results, limit=0), ())

    def test_fresh_cache_is_used_without_inara_acquisition(self):
        cached_result = summary_result()
        cache = SummaryCache(cached_result, UTC_NOW - timedelta(minutes=30))
        with tempfile.TemporaryDirectory() as directory:
            cache_path = Path(directory) / "summary.json"
            with (
                patch.object(CONSUMER, "load_summary_cache", return_value=cache) as load,
                patch.object(CONSUMER, "fetch_inara_html") as acquire,
            ):
                result, status, stored_at, warning = CONSUMER.load_or_refresh_inara(
                    cache_path,
                    now=UTC_NOW,
                )

        load.assert_called_once_with(cache_path, SURFACE_COMMODITIES)
        acquire.assert_not_called()
        self.assertIs(result, cached_result)
        self.assertEqual(status, "fresh cache")
        self.assertEqual(stored_at, cache.stored_at)
        self.assertIsNone(warning)

    def test_stale_cache_survives_refresh_failure(self):
        cached_result = summary_result()
        cache = SummaryCache(cached_result, UTC_NOW - timedelta(hours=2))
        with tempfile.TemporaryDirectory() as directory:
            with (
                patch.object(CONSUMER, "load_summary_cache", return_value=cache),
                patch.object(
                    CONSUMER,
                    "fetch_inara_html",
                    side_effect=RuntimeError("browser unavailable"),
                ) as acquire,
            ):
                result, status, stored_at, warning = CONSUMER.load_or_refresh_inara(
                    Path(directory) / "summary.json",
                    now=UTC_NOW,
                )

        # A stale complete cache is safer than dropping global values entirely.
        acquire.assert_called_once_with()
        self.assertIs(result, cached_result)
        self.assertEqual(status, "stale cache fallback")
        self.assertEqual(stored_at, cache.stored_at)
        self.assertIn("browser unavailable", warning)

    def test_missing_and_invalid_cache_both_attempt_refresh(self):
        invalid_json = json.JSONDecodeError("invalid", "{", 0)
        cases = (
            (FileNotFoundError(), "missing cache"),
            (CacheFormatError("unsupported schema"), "invalid cache"),
            (invalid_json, "malformed JSON"),
        )
        for error, label in cases:
            with self.subTest(cache=label), tempfile.TemporaryDirectory() as directory:
                with (
                    patch.object(CONSUMER, "load_summary_cache", side_effect=error),
                    patch.object(CONSUMER, "fetch_inara_html", return_value="<table/>") as acquire,
                    patch.object(CONSUMER, "parse_inara_summaries", return_value=summary_result()),
                    patch.object(CONSUMER, "save_summary_cache") as save,
                ):
                    result, status, _, warning = CONSUMER.load_or_refresh_inara(
                        Path(directory) / "summary.json",
                        now=UTC_NOW,
                    )

            acquire.assert_called_once_with()
            save.assert_called_once()
            self.assertTrue(result.is_complete)
            self.assertEqual(status, "refreshed")
            self.assertIsNone(warning)

    def test_complete_issue_free_refresh_is_parsed_and_saved(self):
        refreshed = summary_result()
        with tempfile.TemporaryDirectory() as directory:
            cache_path = Path(directory) / "summary.json"
            with (
                patch.object(CONSUMER, "load_summary_cache", side_effect=FileNotFoundError),
                patch.object(CONSUMER, "fetch_inara_html", return_value="<table>INARA</table>"),
                patch.object(CONSUMER, "parse_inara_summaries", return_value=refreshed) as parse,
                patch.object(CONSUMER, "save_summary_cache") as save,
            ):
                result, status, stored_at, warning = CONSUMER.load_or_refresh_inara(
                    cache_path,
                    now=UTC_NOW,
                )

        parse.assert_called_once_with("<table>INARA</table>", SURFACE_COMMODITIES)
        save.assert_called_once_with(cache_path, refreshed, stored_at=UTC_NOW)
        self.assertIs(result, refreshed)
        self.assertEqual(status, "refreshed")
        self.assertEqual(stored_at, UTC_NOW)
        self.assertIsNone(warning)

    def test_partial_or_issue_bearing_refresh_does_not_replace_stale_cache(self):
        partial_results = (
            summary_result(commodities=(SURFACE_COMMODITIES[0],)),
            summary_result(issues=(MarketIssue("INARA", "Malformed row"),)),
        )
        stale_result = summary_result(commodities=(SURFACE_COMMODITIES[0],))
        stale_cache = SummaryCache(stale_result, UTC_NOW - timedelta(hours=3))

        for refreshed in partial_results:
            with self.subTest(missing=len(refreshed.missing), issues=len(refreshed.issues)):
                with tempfile.TemporaryDirectory() as directory:
                    with (
                        patch.object(CONSUMER, "load_summary_cache", return_value=stale_cache),
                        patch.object(CONSUMER, "fetch_inara_html", return_value="<table/>"),
                        patch.object(CONSUMER, "parse_inara_summaries", return_value=refreshed),
                        patch.object(CONSUMER, "save_summary_cache") as save,
                    ):
                        result, status, stored_at, warning = CONSUMER.load_or_refresh_inara(
                            Path(directory) / "summary.json",
                            now=UTC_NOW,
                        )

                save.assert_not_called()
                # Suspect refreshes must not overwrite the last complete good cache.
                self.assertIs(result, stale_result)
                self.assertEqual(status, "stale cache fallback")
                self.assertEqual(stored_at, stale_cache.stored_at)
                self.assertIn("partial values were not used", warning)

    def test_cache_save_failure_uses_stale_cache(self):
        complete = summary_result()
        stale_result = summary_result(commodities=(SURFACE_COMMODITIES[0],))
        stale_cache = SummaryCache(stale_result, UTC_NOW - timedelta(hours=3))
        with tempfile.TemporaryDirectory() as directory:
            with (
                patch.object(CONSUMER, "load_summary_cache", return_value=stale_cache),
                patch.object(CONSUMER, "fetch_inara_html", return_value="<table/>"),
                patch.object(CONSUMER, "parse_inara_summaries", return_value=complete),
                patch.object(CONSUMER, "save_summary_cache", side_effect=OSError("disk full")),
            ):
                result, status, stored_at, warning = CONSUMER.load_or_refresh_inara(
                    Path(directory) / "summary.json",
                    now=UTC_NOW,
                )

        self.assertIs(result, stale_result)
        self.assertEqual(status, "stale cache fallback")
        self.assertEqual(stored_at, stale_cache.stored_at)
        self.assertIn("disk full", warning)

    def test_inara_failure_without_cache_leaves_global_values_unavailable(self):
        with tempfile.TemporaryDirectory() as directory:
            with (
                patch.object(CONSUMER, "load_summary_cache", side_effect=FileNotFoundError),
                patch.object(CONSUMER, "fetch_inara_html", side_effect=RuntimeError("offline")),
            ):
                result, status, stored_at, warning = CONSUMER.load_or_refresh_inara(
                    Path(directory) / "summary.json",
                    now=UTC_NOW,
                )

        self.assertIsNone(result)
        self.assertEqual(status, "unavailable")
        self.assertIsNone(stored_at)
        self.assertIn("offline", warning)

    def test_main_keeps_local_results_when_inara_is_unavailable(self):
        local_results = {
            "Platinum": (observation("Platinum", "Market", price=500, demand=101),)
        }
        with (
            patch.object(CONSUMER.sys, "argv", [str(SCRIPT_PATH), "Kappa"]),
            patch.object(
                CONSUMER,
                "query_system_markets",
                return_value=("Kappa", local_results, ()),
            ),
            patch.object(
                CONSUMER,
                "load_or_refresh_inara",
                return_value=(None, "unavailable", None, "INARA is offline"),
            ),
            patch.object(CONSUMER, "print_results") as render,
        ):
            exit_code = CONSUMER.main()

        self.assertEqual(exit_code, 0)
        rendered = render.call_args.args
        self.assertEqual(rendered[0], "Kappa")
        self.assertEqual(rendered[1], (("Platinum", local_results["Platinum"]),))
        self.assertIsNone(rendered[2])
        self.assertEqual(rendered[3], "unavailable")
        self.assertEqual(rendered[5], "INARA is offline")

    def test_import_does_not_prompt_fetch_or_save(self):
        cache_existed = CONSUMER.CACHE_PATH.exists()
        with (
            patch("builtins.input") as prompt,
            patch("subprocess.run") as browser,
            patch("elite_dangerous.market.fetch_commodity_markets") as fetch,
            patch("elite_dangerous.market.load_summary_cache") as load_cache,
            patch("elite_dangerous.market.save_summary_cache") as save,
        ):
            imported = load_consumer("surface_mining_consumer_import_side_effect_test")

        # Importing must be a pure operation: no prompt, fetch, browser, or cache write.
        prompt.assert_not_called()
        browser.assert_not_called()
        fetch.assert_not_called()
        load_cache.assert_not_called()
        save.assert_not_called()
        self.assertEqual(imported.CACHE_PATH.exists(), cache_existed)


if __name__ == "__main__":
    unittest.main()
