"""Tests for Spansh system discovery and commodity market acquisition."""

import unittest
from datetime import datetime, timezone
from unittest.mock import patch

import requests

from elite_dangerous.market import (
    SpanshError,
    fetch_commodity_market,
    fetch_commodity_markets,
)

BASE_URL = "https://spansh.co.uk/api"


class FakeResponse:
    """Minimal response double exposing the requests methods used by Spansh code."""

    def __init__(self, payload=None, error=None):
        self.payload = payload
        self.error = error

    def raise_for_status(self):
        if self.error is not None:
            raise self.error

    def json(self):
        return self.payload


class FakeSession:
    """Route-based session double that records calls and ownership behavior."""

    def __init__(self, routes):
        self.routes = {key: list(value) for key, value in routes.items()}
        self.calls = []
        self.closed = False
        self.close_count = 0

    def request(self, method, url, *, timeout, **kwargs):
        path = url.removeprefix(BASE_URL)
        self.calls.append((method, path, timeout, kwargs))
        result = self.routes[(method, path)].pop(0)
        if isinstance(result, Exception):
            raise result
        return result

    def close(self):
        self.closed = True
        self.close_count += 1


def response(payload):
    """Wrap a JSON payload in the fake response object."""
    return FakeResponse(payload)


def search_page(results, count):
    """Build a Spansh search page response."""
    return response({"results": results, "count": count})


def search_result(name="Kappa", identifier=42):
    """Build a Spansh system search result."""
    return {"name": name, "id64": identifier}


def station(name, market_id, **fields):
    """Build a system-detail station candidate."""
    return {"name": name, "market_id": market_id, **fields}


def station_detail(name, *, market=None, **fields):
    """Build a station-detail payload with an optional market list."""
    result = {
        "name": name,
        "market_id": 123,
        "system_name": "Kappa",
        "is_planetary": False,
        "has_market": True,
        "market": [] if market is None else market,
        "updated_at": None,
        "market_updated_at": None,
        **fields,
    }
    return {"record": result}


def setup_session(
    *,
    system_record=None,
    pages=None,
    details=None,
):
    """Create a fake session with default or caller-supplied Spansh routes."""
    pages = pages if pages is not None else [search_page([search_result()], 1)]
    system_record = system_record if system_record is not None else {
        "name": "Kappa",
        "stations": [],
        "bodies": [],
    }
    routes = {
        ("POST", "/systems/search"): pages,
        ("GET", "/system/42"): [response({"record": system_record})],
    }
    for market_id, detail in (details or {}).items():
        values = detail if isinstance(detail, list) else [detail]
        routes[("GET", f"/station/{market_id}")] = [
            value if isinstance(value, Exception) else response(value)
            for value in values
        ]
    return FakeSession(routes)


class SpanshMarketTests(unittest.TestCase):
    """Covers singular and batched Spansh market queries and error handling."""

    def test_exact_case_insensitive_system_match_and_request_shape(self):
        session = setup_session(pages=[search_page([search_result("KAPPA")], 1)])
        result = fetch_commodity_market("Kappa", "Platinum", session=session)
        self.assertEqual(result.requested_commodity, "Platinum")
        method, path, timeout, kwargs = session.calls[0]
        self.assertEqual((method, path, timeout), ("POST", "/systems/search", 25))
        self.assertEqual(kwargs["json"], {
            "filters": {"name": {"value": "Kappa"}}, "size": 100, "page": 0,
        })

    def test_exact_system_match_on_later_page(self):
        session = setup_session(pages=[
            search_page([search_result("Kappa Reticuli", 41)], 2),
            search_page([search_result("Kappa", 42)], 2),
        ])
        fetch_commodity_market("Kappa", "Platinum", session=session)
        self.assertEqual([call[3]["json"]["page"] for call in session.calls[:2]], [0, 1])

    def test_search_without_count_continues_full_pages_until_empty(self):
        full_page = [search_result(f"System {index}", index) for index in range(100)]
        session = setup_session(pages=[
            response({"results": full_page}),
            response({"results": []}),
        ])
        with self.assertRaises(SpanshError):
            fetch_commodity_market("Kappa", "Platinum", session=session)
        self.assertEqual([call[3]["json"]["page"] for call in session.calls], [0, 1])

    def test_search_exhausted_without_exact_match_is_fatal(self):
        session = setup_session(pages=[search_page([search_result("Kappa Reticuli")], 1)])
        with self.assertRaises(SpanshError):
            fetch_commodity_market("Kappa", "Platinum", session=session)
        self.assertEqual(len(session.calls), 1)

    def test_empty_page_before_reported_count_is_fatal_instead_of_not_found(self):
        session = setup_session(pages=[
            search_page([search_result("Kappa Reticuli", 41)], 2),
            search_page([], 2),
        ])
        with self.assertRaisesRegex(SpanshError, "ended before its reported result count"):
            fetch_commodity_market("Kappa", "Platinum", session=session)
        self.assertEqual([call[3]["json"]["page"] for call in session.calls], [0, 1])

    def test_malformed_search_payload_is_fatal(self):
        session = setup_session(pages=[response({"results": "not a list", "count": 1})])
        with self.assertRaises(SpanshError):
            fetch_commodity_market("Kappa", "Platinum", session=session)

    def test_fatal_system_search_http_error_preserves_cause(self):
        failure = requests.ConnectionError("offline")
        session = setup_session(pages=[failure])
        with self.assertRaises(SpanshError) as raised:
            fetch_commodity_market("Kappa", "Platinum", session=session)
        self.assertIs(raised.exception.__cause__, failure)

    def test_repeated_page_is_fatal_before_search_completion(self):
        repeated = search_page([search_result("Kappa Reticuli", 41)], 2)
        session = setup_session(pages=[repeated, repeated])
        with self.assertRaises(SpanshError):
            fetch_commodity_market("Kappa", "Platinum", session=session)
        self.assertEqual(len(session.calls), 2)

    def test_unusable_system_detail_and_name_mismatch_are_fatal(self):
        for record in ({"name": "Other", "stations": [], "bodies": []}, []):
            with self.subTest(record=record):
                session = setup_session()
                session.routes[("GET", "/system/42")] = [response({"record": record})]
                with self.assertRaises(SpanshError):
                    fetch_commodity_market("Kappa", "Platinum", session=session)

    def test_discovery_recurses_and_last_duplicate_market_id_wins(self):
        system = {
            "name": "Kappa",
            "stations": [station("Old", 123, has_market=False)],
            "bodies": [{
                "stations": [station("Newest", "123", has_market=True)],
                "bodies": [{"stations": [station("Deep body", 124, has_market=True)]}],
            }],
        }
        session = setup_session(
            system_record=system,
            details={123: station_detail("Detail", market=[{
                "commodity": "Platinum", "sell_price": 5, "demand": 2, "supply": 1,
            }]), 124: station_detail("Deep body", market=[{"commodity": "Gold"}])},
        )
        result = fetch_commodity_market("Kappa", "Platinum", session=session)
        self.assertEqual(len(result.observations), 1)
        self.assertEqual(result.observations[0].station.name, "Detail")
        self.assertEqual([call[1] for call in session.calls].count("/station/123"), 1)
        self.assertIn("/station/124", [call[1] for call in session.calls])

    def test_system_and_planetary_candidates_by_market_flag_and_service(self):
        system = {
            "name": "Kappa",
            "stations": [station("Flag candidate", 123, has_market=True)],
            "bodies": [{"stations": [station("Service candidate", 124, services=["Market"])]}],
        }
        details = {
            123: station_detail("Flag candidate", market=[{"commodity": "Platinum"}]),
            124: station_detail("Service candidate", market=[{"commodity": "Platinum"}]),
        }
        session = setup_session(system_record=system, details=details)
        result = fetch_commodity_market("Kappa", "Platinum", session=session)
        self.assertEqual(
            [item.station.name for item in result.observations],
            ["Flag candidate", "Service candidate"],
        )

    def test_discovery_missing_or_unusable_market_id_is_an_issue_for_candidate(self):
        system = {
            "name": "Kappa",
            "stations": [
                station("No ID", None, has_market=True),
                station("Bad ID", "market-x", services=["Market"]),
            ],
            "bodies": [],
        }
        result = fetch_commodity_market(
            "Kappa", "Platinum", session=setup_session(system_record=system)
        )
        self.assertEqual([issue.station_name for issue in result.issues], ["No ID", "Bad ID"])
        self.assertTrue(all("market_id" in issue.message for issue in result.issues))

    def test_detail_without_market_capability_is_skipped(self):
        system = {
            "name": "Kappa",
            "stations": [station("Gone", 123, has_market=True)],
            "bodies": [],
        }
        detail = station_detail("Gone", market=[{"commodity": "Platinum"}], has_market=False)
        result = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
            system_record=system, details={123: detail}
        ))
        self.assertEqual(result.observations, ())
        self.assertEqual(result.issues, ())

    def test_unusable_station_detail_becomes_recoverable_issue(self):
        system = {
            "name": "Kappa",
            "stations": [station("Malformed", 123, has_market=True)],
            "bodies": [],
        }
        result = fetch_commodity_market(
            "Kappa",
            "Platinum",
            session=setup_session(system_record=system, details={123: {"invalid": True}}),
        )
        self.assertEqual(result.observations, ())
        self.assertEqual(result.issues[0].station_name, "Malformed")

    def test_station_system_mismatch_is_issue_and_other_results_survive(self):
        system = {"name": "Kappa", "stations": [
            station("Wrong system", 123, has_market=True),
            station("Good", 124, has_market=True),
        ], "bodies": []}
        details = {
            123: station_detail("Wrong system", system_name="Other"),
            124: station_detail("Good", market=[{"commodity": "Platinum", "sell_price": 4}]),
        }
        result = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
            system_record=system, details=details
        ))
        self.assertEqual([item.station.name for item in result.observations], ["Good"])
        self.assertEqual(result.issues[0].station_name, "Wrong system")
        self.assertFalse(result.is_complete)

    def test_station_http_failure_is_recoverable_and_preserves_observation(self):
        system = {"name": "Kappa", "stations": [
            station("Unavailable", 123, has_market=True),
            station("Available", 124, has_market=True),
        ], "bodies": []}
        details = {
            123: requests.ConnectionError("offline"),
            124: station_detail("Available", market=[{"commodity": "Platinum", "sell_price": 4}]),
        }
        result = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
            system_record=system, details=details
        ))
        self.assertEqual(len(result.observations), 1)
        self.assertEqual(result.issues[0].station_name, "Unavailable")
        self.assertFalse(result.is_complete)

    def test_matching_canonical_and_demonstrated_alias_use_canonical_name(self):
        products = (
            ("Bastnäsite", "Bastnasite"),
            ("Methanol Monohydrate Crystals", "Methanol Crystals"),
            ("Platinum", "PLATINUM"),
        )
        for requested, returned in products:
            with self.subTest(requested=requested, returned=returned):
                system = {
                    "name": "Kappa",
                    "stations": [station("Market", 123, has_market=True)],
                    "bodies": [],
                }
                detail = station_detail("Market", market=[{"commodity": returned, "sell_price": 9}])
                result = fetch_commodity_market("Kappa", requested, session=setup_session(
                    system_record=system, details={123: detail}
                ))
                expected = {
                    "Bastnäsite": "Bastnäsite",
                    "Methanol Monohydrate Crystals": "Methanol Crystals",
                    "Platinum": "Platinum",
                }[requested]
                self.assertEqual(result.requested_commodity, expected)
                self.assertEqual(result.observations[0].commodity, result.requested_commodity)

    def test_unknown_product_uses_normalized_matching_without_new_catalogue_entries(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        detail = station_detail("Market", market=[{"commodity": "Test Commodity", "sell_price": 3}])
        result = fetch_commodity_market("Kappa", "test-commodity", session=setup_session(
            system_record=system, details={123: detail}
        ))
        self.assertEqual(result.observations[0].commodity, "Test Commodity")
        self.assertEqual(result.requested_commodity, "test-commodity")

    def test_empty_or_nonmatching_market_is_not_an_issue(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        for market in ([], [{"commodity": "Gold"}]):
            with self.subTest(market=market):
                result = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
                    system_record=system, details={123: station_detail("Market", market=market)}
                ))
                self.assertEqual(result.observations, ())
                self.assertEqual(result.issues, ())

    def test_missing_and_nonlist_market_payload_are_issues(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        for market, include_market in ((None, False), ("invalid", True)):
            detail = station_detail("Market")
            if include_market:
                detail["record"]["market"] = market
            else:
                del detail["record"]["market"]
            with self.subTest(market=market):
                result = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
                    system_record=system, details={123: detail}
                ))
                self.assertEqual(result.observations, ())
                self.assertEqual(len(result.issues), 1)

    def test_malformed_relevant_market_row_adds_issue_without_losing_valid_rows(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        detail = station_detail("Market", market=[
            {"commodity": "Platinum", "sell_price": 5, "demand": 1},
            {"commodity": "Platinum", "sell_price": "not-a-price"},
        ])
        result = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
            system_record=system, details={123: detail}
        ))
        self.assertEqual(len(result.observations), 1)
        self.assertEqual(len(result.issues), 1)
        self.assertFalse(result.is_complete)

    def test_malformed_unrelated_market_row_does_not_hide_requested_commodity(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        detail = station_detail("Market", market=[
            {"commodity": "Gold", "sell_price": "not-a-price"},
            {"commodity": "Platinum", "sell_price": 725, "demand": 3},
        ])
        result = fetch_commodity_market(
            "Kappa",
            "Platinum",
            session=setup_session(system_record=system, details={123: detail}),
        )
        self.assertEqual(len(result.observations), 1)
        self.assertEqual(result.observations[0].sell_price, 725)
        self.assertEqual(result.issues, ())

    def test_none_market_facts_are_preserved(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        detail = station_detail("Market", market=[{
            "commodity": "Platinum", "sell_price": None, "demand": None, "supply": None,
        }])
        observation = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
            system_record=system, details={123: detail}
        )).observations[0]
        self.assertIsNone(observation.sell_price)
        self.assertIsNone(observation.demand)
        self.assertIsNone(observation.supply)

    def test_pad_mapping_and_timestamps(self):
        system = {"name": "Kappa", "stations": [
            station(str(index), 123 + index, has_market=True) for index in range(4)
        ], "bodies": []}
        pads = (
            {"has_large_pad": True},
            {"medium_pads": 1},
            {"small_pads": 1},
            {},
        )
        details = {
            123 + index: station_detail(
                str(index), market=[{"commodity": "Platinum"}], **pad_fields,
                market_updated_at="2026-09-16T12:30:00Z",
                updated_at="2026-09-16T12:30:00",
            )
            for index, pad_fields in enumerate(pads)
        }
        result = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
            system_record=system, details=details
        ))
        self.assertEqual(
            [item.station.max_landing_pad for item in result.observations],
            ["L", "M", "S", None],
        )
        self.assertEqual(
            [item.station.market_id for item in result.observations],
            ["123", "124", "125", "126"],
        )
        for item in result.observations:
            expected = datetime(2026, 9, 16, 12, 30, tzinfo=timezone.utc)
            self.assertEqual(item.market_updated_at, expected)
            self.assertEqual(item.station_updated_at, expected)

    def test_invalid_or_missing_timestamps_map_to_none(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        detail = station_detail(
            "Market",
            market=[{"commodity": "Platinum"}],
            market_updated_at="bad",
        )
        del detail["record"]["updated_at"]
        observation = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
            system_record=system, details={123: detail}
        )).observations[0]
        self.assertIsNone(observation.market_updated_at)
        self.assertIsNone(observation.station_updated_at)

    def test_carriers_low_demand_and_zero_or_unknown_prices_are_not_filtered_or_ranked(self):
        names = ("ABC-123", "FC01", "Ordinary")
        system = {"name": "Kappa", "stations": [
            station(name, 123 + index, has_market=True) for index, name in enumerate(names)
        ], "bodies": []}
        prices = (0, None, 999)
        details = {
            123 + index: station_detail(name, market=[{
                "commodity": "Platinum", "sell_price": prices[index], "demand": 0,
            }])
            for index, name in enumerate(names)
        }
        result = fetch_commodity_market("Kappa", "Platinum", session=setup_session(
            system_record=system, details=details
        ))
        self.assertEqual([item.station.name for item in result.observations], list(names))
        self.assertEqual([item.sell_price for item in result.observations], list(prices))
        self.assertTrue(all(item.demand == 0 for item in result.observations))

    def test_injected_session_is_reused_and_not_closed(self):
        session = setup_session()
        fetch_commodity_market("Kappa", "Platinum", session=session)
        # Caller-owned sessions must remain open so batching code can reuse them safely.
        self.assertFalse(session.closed)
        self.assertEqual(session.calls[0][0:3], ("POST", "/systems/search", 25))

    def test_owned_session_closes_after_success_and_failure(self):
        for pages, succeeds in (
            ([search_page([search_result()], 1)], True),
            ([response({"results": []})], False),
        ):
            session = setup_session(pages=pages)
            with patch("elite_dangerous.market.spansh.requests.Session", return_value=session):
                if succeeds:
                    fetch_commodity_market("Kappa", "Platinum")
                else:
                    with self.assertRaises(SpanshError):
                        fetch_commodity_market("Kappa", "Platinum")
            self.assertTrue(session.closed)

    def test_batch_fetches_multiple_commodities_with_one_traversal(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        detail = station_detail("Market", market=[
            {"commodity": "Platinum", "sell_price": 10, "demand": 20},
            {"commodity": "Gold", "sell_price": 30, "demand": 40},
        ])
        session = setup_session(system_record=system, details={123: detail})

        results = fetch_commodity_markets(
            "Kappa",
            (name for name in ("Gold", "Platinum")),
            session=session,
        )

        self.assertEqual(
            tuple(result.requested_commodity for result in results),
            ("Gold", "Platinum"),
        )
        self.assertEqual(
            tuple(result.observations[0].sell_price for result in results),
            (30, 10),
        )
        paths = [call[1] for call in session.calls]
        self.assertEqual(paths.count("/systems/search"), 1)
        self.assertEqual(paths.count("/system/42"), 1)
        self.assertEqual(paths.count("/station/123"), 1)

    def test_batch_deduplicates_aliases_and_exact_requests_in_first_order(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        detail = station_detail("Market", market=[
            {"commodity": "Bastnasite", "sell_price": 1},
            {"commodity": "Gold", "sell_price": 2},
            {"commodity": "Methanol Monohydrate Crystals", "sell_price": 3},
            {"commodity": "Platinum", "sell_price": 4},
        ])
        results = fetch_commodity_markets(
            "Kappa",
            (name for name in (
                "Gold",
                "Bastnasite",
                "Bastnäsite",
                "Methanol Monohydrate Crystals",
                "Methanol Crystals",
                "Platinum",
                "Gold",
            )),
            session=setup_session(system_record=system, details={123: detail}),
        )

        self.assertEqual(
            tuple(result.requested_commodity for result in results),
            ("Gold", "Bastnäsite", "Methanol Crystals", "Platinum"),
        )
        self.assertEqual(results[1].observations[0].commodity, "Bastnäsite")
        self.assertEqual(results[2].observations[0].commodity, "Methanol Crystals")

    def test_batch_supports_unknown_normalized_names_and_distinct_empty_keys(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        detail = station_detail("Market", market=[
            {"commodity": "New-Community Product", "sell_price": 1},
            {"commodity": "???", "sell_price": 2},
            {"commodity": "!!!", "sell_price": 3},
        ])
        results = fetch_commodity_markets(
            "Kappa",
            ("new community product", "???", "!!!"),
            session=setup_session(system_record=system, details={123: detail}),
        )

        self.assertEqual(len(results), 3)
        self.assertEqual(
            tuple(result.observations[0].sell_price for result in results),
            (1, 2, 3),
        )
        self.assertEqual(results[0].observations[0].commodity, "New-Community Product")

    def test_empty_batch_does_not_create_or_use_a_session(self):
        supplied_session = setup_session()
        with patch("elite_dangerous.market.spansh.requests.Session") as make_session:
            self.assertEqual(fetch_commodity_markets("Kappa", ()), ())
            self.assertEqual(make_session.call_count, 0)

        # Empty work should not close an injected session it never used.
        self.assertEqual(
            fetch_commodity_markets("Kappa", (), session=supplied_session),
            (),
        )
        self.assertEqual(supplied_session.calls, [])
        self.assertFalse(supplied_session.closed)

    def test_absent_requested_commodity_has_no_synthetic_issue(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        result = fetch_commodity_markets(
            "Kappa",
            ("Platinum", "Gold"),
            session=setup_session(
                system_record=system,
                details={123: station_detail("Market", market=[{"commodity": "Platinum"}])},
            ),
        )[1]

        self.assertEqual(result.observations, ())
        self.assertEqual(result.issues, ())
        self.assertEqual(result.missing, ("Gold",))

    def test_malformed_requested_row_only_affects_its_commodity(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        results = fetch_commodity_markets(
            "Kappa",
            ("Platinum", "Gold"),
            session=setup_session(
                system_record=system,
                details={123: station_detail("Market", market=[
                    {"commodity": "Platinum", "sell_price": "bad"},
                    {"commodity": "Gold", "sell_price": 50},
                ])},
            ),
        )

        self.assertEqual(len(results[0].issues), 1)
        self.assertEqual(results[0].observations, ())
        self.assertEqual(results[1].issues, ())
        self.assertEqual(results[1].observations[0].sell_price, 50)

    def test_malformed_unrelated_row_is_ignored_and_later_products_survive(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        results = fetch_commodity_markets(
            "Kappa",
            ("Platinum", "Silver"),
            session=setup_session(
                system_record=system,
                details={123: station_detail("Market", market=[
                    {"commodity": "Gold", "sell_price": "bad"},
                    {"commodity": "Platinum", "sell_price": 75},
                    {"commodity": "Silver", "sell_price": 25},
                ])},
            ),
        )

        self.assertEqual(tuple(len(result.observations) for result in results), (1, 1))
        self.assertEqual(tuple(result.issues for result in results), ((), ()))

    def test_unidentifiable_malformed_row_is_shared_across_results(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        results = fetch_commodity_markets(
            "Kappa",
            ("Platinum", "Gold"),
            session=setup_session(
                system_record=system,
                details={123: station_detail("Market", market=[
                    {"sell_price": 12},
                    {"commodity": "Platinum", "sell_price": 75},
                ])},
            ),
        )

        self.assertEqual(results[0].issues, results[1].issues)
        self.assertEqual(len(results[0].issues), 1)
        self.assertEqual(len(results[0].observations), 1)

    def test_duplicate_valid_rows_remain_duplicate_observations(self):
        system = {
            "name": "Kappa",
            "stations": [station("Market", 123, has_market=True)],
            "bodies": [],
        }
        result = fetch_commodity_markets(
            "Kappa",
            ("Platinum",),
            session=setup_session(
                system_record=system,
                details={123: station_detail("Market", market=[
                    {"commodity": "Platinum", "sell_price": 5},
                    {"commodity": "Platinum", "sell_price": 8},
                ])},
            ),
        )[0]

        self.assertEqual(tuple(item.sell_price for item in result.observations), (5, 8))

    def test_station_failure_is_shared_and_other_station_products_survive(self):
        system = {
            "name": "Kappa",
            "stations": [
                station("Unavailable", 123, has_market=True),
                station("Available", 124, has_market=True),
            ],
            "bodies": [],
        }
        session = setup_session(
            system_record=system,
            details={
                123: requests.ConnectionError("offline"),
                124: station_detail("Available", market=[
                    {"commodity": "Platinum", "sell_price": 50},
                    {"commodity": "Gold", "sell_price": 25},
                ]),
            },
        )

        results = fetch_commodity_markets(
            "Kappa",
            ("Platinum", "Gold"),
            session=session,
        )

        self.assertEqual(results[0].issues, results[1].issues)
        self.assertEqual(results[0].issues[0].station_name, "Unavailable")
        self.assertEqual(results[0].observations[0].station.name, "Available")
        self.assertEqual(results[1].observations[0].station.name, "Available")
        self.assertEqual([call[1] for call in session.calls].count("/station/123"), 1)
        self.assertEqual([call[1] for call in session.calls].count("/station/124"), 1)

    def test_batch_preserves_mapping_and_does_not_apply_policy_or_ranking(self):
        names = ("FC01", "ABC-123", "Ordinary")
        system = {
            "name": "Kappa",
            "stations": [
                station(name, 123 + index, has_market=True)
                for index, name in enumerate(names)
            ],
            "bodies": [],
        }
        prices = (0, 200, None)
        pads = (
            {"has_large_pad": True},
            {"medium_pads": 1},
            {"small_pads": 1},
        )
        details = {
            123 + index: station_detail(
                name,
                market=[{
                    "commodity": "Platinum",
                    "sell_price": prices[index],
                    "demand": 0,
                    "supply": index,
                }],
                market_updated_at="2026-09-16T12:30:00Z",
                updated_at="2026-09-16T12:30:00",
                **pads[index],
            )
            for index, name in enumerate(names)
        }
        result = fetch_commodity_markets(
            "Kappa",
            ("Platinum",),
            session=setup_session(system_record=system, details=details),
        )[0]

        self.assertEqual(tuple(item.station.name for item in result.observations), names)
        self.assertEqual(tuple(item.sell_price for item in result.observations), prices)
        self.assertEqual(tuple(item.demand for item in result.observations), (0, 0, 0))
        self.assertEqual(tuple(item.supply for item in result.observations), (0, 1, 2))
        self.assertEqual(
            tuple(item.station.max_landing_pad for item in result.observations),
            ("L", "M", "S"),
        )
        self.assertTrue(all(
            item.market_updated_at == datetime(2026, 9, 16, 12, 30, tzinfo=timezone.utc)
            for item in result.observations
        ))

    def test_batch_fatal_system_failure_remains_spansh_error(self):
        session = setup_session(pages=[response({"results": "invalid"})])
        with self.assertRaises(SpanshError):
            fetch_commodity_markets("Kappa", ("Platinum", "Gold"), session=session)

    def test_batch_session_ownership_matches_singular_api(self):
        supplied = setup_session()
        fetch_commodity_markets("Kappa", ("Platinum", "Gold"), session=supplied)
        self.assertFalse(supplied.closed)

        owned = setup_session()
        with patch("elite_dangerous.market.spansh.requests.Session", return_value=owned):
            fetch_commodity_markets("Kappa", ("Platinum", "Gold"))
        self.assertTrue(owned.closed)
        self.assertEqual(owned.close_count, 1)


if __name__ == "__main__":
    unittest.main()
