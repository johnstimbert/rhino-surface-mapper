"""Tests for the INARA summary cache schema, freshness, and safe writes."""

from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from elite_dangerous.market import (
    CacheFormatError,
    CommodityPriceSummary,
    CommoditySummaryResult,
    MarketIssue,
    SummaryCache,
    load_summary_cache,
    save_summary_cache,
)


UTC_TIME = datetime(2026, 9, 16, 12, 0, tzinfo=timezone.utc)


def summary(commodity, average=100, maximum=200, demand=None):
    """Build a commodity summary fixture for cache records."""
    return CommodityPriceSummary(commodity, average, maximum, demand)


def result(*summaries, requested=None, issues=()):
    """Build a summary result while defaulting requests to supplied summaries."""
    names = requested if requested is not None else tuple(
        item.commodity for item in summaries
    )
    return CommoditySummaryResult(names, tuple(summaries), tuple(issues))


def record(commodity="Platinum", average=100, maximum=200, demand=None):
    """Build a serialized cache summary record."""
    return {
        "commodity": commodity,
        "average_sell": average,
        "maximum_sell": maximum,
        "total_demand": demand,
    }


def payload(*records, stored_at="2026-09-16T12:00:00+00:00", version=1):
    """Build a serialized versioned cache payload."""
    return {"version": version, "stored_at": stored_at, "summaries": list(records)}


class SummaryCacheTests(unittest.TestCase):
    """Covers cache persistence, validation, freshness, and atomic replacement."""

    def setUp(self):
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary_directory.name)
        self.cache_path = self.directory / "market.json"

    def tearDown(self):
        self.temporary_directory.cleanup()

    def write_payload(self, data):
        """Write raw JSON data directly to the test cache file."""
        self.cache_path.write_text(json.dumps(data), encoding="utf-8")

    def test_save_writes_the_versioned_domain_schema(self):
        save_summary_cache(
            self.cache_path,
            result(summary("Platinum", average=0, maximum=None, demand=45)),
            stored_at=UTC_TIME,
        )

        data = json.loads(self.cache_path.read_text(encoding="utf-8"))
        self.assertEqual(set(data), {"version", "stored_at", "summaries"})
        self.assertEqual(data["version"], 1)
        self.assertEqual(data["stored_at"], "2026-09-16T12:00:00+00:00")
        self.assertEqual(data["summaries"], [record("Platinum", 0, None, 45)])
        self.assertNotIn("requested", data)
        self.assertNotIn("issues", data)

    def test_save_normalizes_explicit_offset_timestamp_to_utc(self):
        local_time = datetime(2026, 9, 16, 14, 0, tzinfo=timezone(timedelta(hours=2)))
        save_summary_cache(self.cache_path, result(summary("Platinum")), stored_at=local_time)

        data = json.loads(self.cache_path.read_text(encoding="utf-8"))
        self.assertEqual(data["stored_at"], "2026-09-16T12:00:00+00:00")

    def test_default_stored_at_uses_an_aware_utc_timestamp(self):
        before = datetime.now(timezone.utc)
        save_summary_cache(self.cache_path, result(summary("Platinum")))
        after = datetime.now(timezone.utc)

        stored_at = datetime.fromisoformat(
            json.loads(self.cache_path.read_text(encoding="utf-8"))["stored_at"]
        )
        self.assertIsNotNone(stored_at.tzinfo)
        self.assertEqual(stored_at.utcoffset(), timedelta(0))
        self.assertLessEqual(before, stored_at)
        self.assertLessEqual(stored_at, after)

    def test_save_rejects_naive_timestamp_before_creating_cache(self):
        with self.assertRaises(ValueError):
            save_summary_cache(
                self.cache_path,
                result(summary("Platinum")),
                stored_at=datetime(2026, 9, 16, 12, 0),
            )
        self.assertFalse(self.cache_path.exists())

    def test_save_rejects_results_with_issues_without_replacing_existing_cache(self):
        save_summary_cache(self.cache_path, result(summary("Platinum")), stored_at=UTC_TIME)
        previous = self.cache_path.read_bytes()
        issue_result = result(
            summary("Gold"),
            requested=("Gold",),
            issues=(MarketIssue("INARA", "Malformed row"),),
        )

        with self.assertRaises(ValueError):
            save_summary_cache(self.cache_path, issue_result, stored_at=UTC_TIME)
        self.assertEqual(self.cache_path.read_bytes(), previous)

    def test_partial_coverage_is_saved_without_a_catalogue_size_requirement(self):
        twenty = tuple(f"Future commodity {index}" for index in range(20))
        partial = result(
            summary("Future commodity 0"),
            requested=twenty,
        )
        save_summary_cache(self.cache_path, partial, stored_at=UTC_TIME)

        loaded_present = load_summary_cache(self.cache_path, (twenty[0],)).result
        loaded_missing = load_summary_cache(self.cache_path, (twenty[1],)).result
        self.assertTrue(loaded_present.is_complete)
        self.assertEqual(loaded_missing.missing, (twenty[1],))
        self.assertFalse(loaded_missing.is_complete)

    def test_load_selects_requested_subset_and_excludes_extra_records(self):
        self.write_payload(
            payload(
                record("Platinum"),
                record("Gold"),
                record("Silver"),
                record("Copper"),
            )
        )

        loaded = load_summary_cache(self.cache_path, ("Gold", "Silver"))
        self.assertTrue(loaded.result.is_complete)
        self.assertEqual(
            tuple(item.commodity for item in loaded.result.summaries),
            ("Gold", "Silver"),
        )

    def test_load_keeps_available_requested_summaries_and_derives_missing(self):
        self.write_payload(payload(record("Platinum"), record("Gold")))

        loaded = load_summary_cache(
            self.cache_path,
            ("Platinum", "Gold", "Copper"),
        )
        self.assertEqual(
            tuple(item.commodity for item in loaded.result.summaries),
            ("Platinum", "Gold"),
        )
        self.assertEqual(loaded.result.missing, ("Copper",))
        self.assertFalse(loaded.result.is_complete)
        self.assertEqual(loaded.result.issues, ())

    def test_load_canonicalizes_known_aliases_and_preserves_unknown_commodities(self):
        self.write_payload(
            payload(
                record("Bastnasite"),
                record("Methanol Monohydrate Crystals"),
                record("New-Community Product"),
            )
        )

        loaded = load_summary_cache(
            self.cache_path,
            ("Bastnasite", "Methanol Crystals", "New Community Product"),
        )
        self.assertEqual(
            tuple(item.commodity for item in loaded.result.summaries),
            ("Bastnäsite", "Methanol Crystals", "New-Community Product"),
        )
        self.assertTrue(loaded.result.is_complete)

    def test_empty_normalization_keys_do_not_collapse_distinct_unknown_names(self):
        self.write_payload(payload(record("!!!")))

        loaded = load_summary_cache(self.cache_path, ("???",))
        self.assertEqual(loaded.result.summaries, ())
        self.assertEqual(loaded.result.missing, ("???",))

    def test_missing_cache_preserves_file_not_found_error(self):
        with self.assertRaises(FileNotFoundError):
            load_summary_cache(self.cache_path, ("Platinum",))

    def test_malformed_json_preserves_json_decode_error(self):
        self.cache_path.write_text("{", encoding="utf-8")
        with self.assertRaises(json.JSONDecodeError):
            load_summary_cache(self.cache_path, ("Platinum",))

    def test_valid_json_schema_errors_raise_cache_format_error(self):
        invalid_payloads = (
            [],
            {"version": 2, "stored_at": "2026-09-16T12:00:00+00:00", "summaries": [record()]},
            payload(record(), stored_at="not-a-time"),
            payload(record(), stored_at="2026-09-16T12:00:00"),
            payload(),
            payload({"commodity": "Platinum"}),
            payload(record("")),
            payload(record(average="100")),
            payload(record(average=True)),
            payload(record("Platinum"), record("Platinum")),
            payload(record("Bastnäsite"), record("Bastnasite")),
        )
        for invalid in invalid_payloads:
            with self.subTest(invalid=invalid):
                self.write_payload(invalid)
                with self.assertRaises(CacheFormatError):
                    load_summary_cache(self.cache_path, ("Platinum",))

    def test_load_normalizes_stored_timestamp_to_utc(self):
        self.write_payload(
            payload(record(), stored_at="2026-09-16T14:00:00+02:00")
        )

        loaded = load_summary_cache(self.cache_path, ("Platinum",))
        self.assertEqual(loaded.stored_at, UTC_TIME)
        self.assertEqual(loaded.stored_at.utcoffset(), timedelta(0))

    def test_freshness_uses_strict_boundary(self):
        cache = SummaryCache(result=result(summary("Platinum")), stored_at=UTC_TIME)
        age_limit = timedelta(seconds=60)

        # Freshness uses age < max_age, so equality is already stale.
        self.assertTrue(cache.is_fresh(age_limit, now=UTC_TIME + timedelta(seconds=59)))
        self.assertFalse(cache.is_fresh(age_limit, now=UTC_TIME + age_limit))
        self.assertFalse(cache.is_fresh(age_limit, now=UTC_TIME + timedelta(seconds=61)))

    def test_freshness_normalizes_now_and_clamps_future_stored_time(self):
        local_now = datetime(2026, 9, 16, 7, 0, tzinfo=timezone(timedelta(hours=-5)))
        same_instant = SummaryCache(result=result(summary("Platinum")), stored_at=UTC_TIME)
        future = SummaryCache(
            result=result(summary("Platinum")),
            stored_at=UTC_TIME + timedelta(minutes=5),
        )

        self.assertTrue(same_instant.is_fresh(timedelta(seconds=1), now=local_now))
        self.assertTrue(future.is_fresh(timedelta(seconds=1), now=UTC_TIME))

    def test_freshness_rejects_naive_now_and_negative_max_age(self):
        cache = SummaryCache(result=result(summary("Platinum")), stored_at=UTC_TIME)

        with self.assertRaises(ValueError):
            cache.is_fresh(timedelta(seconds=1), now=datetime(2026, 9, 16, 12, 0))
        with self.assertRaises(ValueError):
            cache.is_fresh(timedelta(seconds=-1), now=UTC_TIME)

    def test_successful_save_replaces_previous_cache_content(self):
        save_summary_cache(self.cache_path, result(summary("Platinum")), stored_at=UTC_TIME)
        save_summary_cache(self.cache_path, result(summary("Gold")), stored_at=UTC_TIME)

        loaded = load_summary_cache(self.cache_path, ("Platinum", "Gold"))
        self.assertEqual(tuple(item.commodity for item in loaded.result.summaries), ("Gold",))

    def test_replace_failure_preserves_destination_and_cleans_temporary_file(self):
        save_summary_cache(self.cache_path, result(summary("Platinum")), stored_at=UTC_TIME)
        previous = self.cache_path.read_bytes()

        with patch(
            "elite_dangerous.market.cache.os.replace",
            side_effect=PermissionError("replacement blocked"),
        ):
            with self.assertRaises(PermissionError):
                save_summary_cache(self.cache_path, result(summary("Gold")), stored_at=UTC_TIME)

        self.assertEqual(self.cache_path.read_bytes(), previous)
        self.assertEqual(tuple(self.directory.glob(".market.json.*.tmp")), ())

    def test_write_failure_preserves_destination_and_cleans_temporary_file(self):
        save_summary_cache(self.cache_path, result(summary("Platinum")), stored_at=UTC_TIME)
        previous = self.cache_path.read_bytes()

        with patch(
            "elite_dangerous.market.cache.os.fsync",
            side_effect=OSError("flush failed"),
        ):
            with self.assertRaises(OSError):
                save_summary_cache(self.cache_path, result(summary("Gold")), stored_at=UTC_TIME)

        self.assertEqual(self.cache_path.read_bytes(), previous)
        self.assertEqual(tuple(self.directory.glob(".market.json.*.tmp")), ())

    def test_save_does_not_create_missing_parent_directory(self):
        missing_parent_path = self.directory / "not-created" / "market.json"

        with self.assertRaises(FileNotFoundError):
            save_summary_cache(
                missing_parent_path,
                result(summary("Platinum")),
                stored_at=UTC_TIME,
            )
        self.assertFalse(missing_parent_path.parent.exists())


if __name__ == "__main__":
    unittest.main()
