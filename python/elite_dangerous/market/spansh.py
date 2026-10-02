"""Spansh acquisition and mapping for system commodity markets.

This Qt-free provider adapter belongs to the Elite Dangerous market domain. It
uses Spansh's system search, system detail, and station detail endpoints to build
provider-independent immutable market observations for requested commodities.

Fatal failures that make the system result untrustworthy raise ``SpanshError``.
Recoverable station-level, row-level, or discovery defects are returned as
``MarketIssue`` values so usable stations remain available to callers.
"""

from collections.abc import Iterable
from datetime import datetime, timezone
from typing import Any

import requests

from .commodities import canonical_commodity_name
from .models import (
    CommodityMarketResult,
    LandingPad,
    MarketIssue,
    MarketObservation,
    Station,
    _commodity_key,
)

_BASE_URL = "https://spansh.co.uk/api"
_REQUEST_TIMEOUT = 25
_SYSTEM_PAGE_SIZE = 100


class SpanshError(RuntimeError):
    """Raised when Spansh cannot provide a trustworthy system-level result.

This exception is reserved for failures in endpoint access, response shape,
pagination consistency, exact system identity, or system station discovery. A
bad individual station or market row is recoverable and becomes ``MarketIssue``
instead of this fatal exception.
"""


def fetch_commodity_market(
    system_name: str,
    commodity_name: str,
    *,
    session: requests.Session | None = None,
) -> CommodityMarketResult:
    """Fetch one commodity's observations across a Spansh system.

Args:
    system_name: Exact system name to search for, matched case-insensitively.
    commodity_name: Commodity name or supported alias to request.
    session: Optional caller-owned ``requests.Session``. Injected sessions are
        reused and never closed by this function.

Returns:
    A ``CommodityMarketResult`` for the requested commodity.

Raises:
    SpanshError: If system discovery or endpoint access fails fatally.
    TypeError: If the delegated commodity input validation fails.
"""
    return fetch_commodity_markets(
        system_name,
        (commodity_name,),
        session=session,
    )[0]


def fetch_commodity_markets(
    system_name: str,
    commodity_names: Iterable[str],
    *,
    session: requests.Session | None = None,
) -> tuple[CommodityMarketResult, ...]:
    """Fetch several commodity results in one Spansh system traversal.

    Args:
        system_name: Exact system name to search and validate.
        commodity_names: Iterable of commodity names or aliases. Strings and
            bytes are rejected because they are not intended as commodity lists.
        session: Optional caller-owned ``requests.Session``. Injected sessions
            are reused and never closed; internally created sessions are closed.

    Returns:
        One ``CommodityMarketResult`` per unique normalized requested commodity,
        preserving first requested order.

    Raises:
        TypeError: If ``commodity_names`` is not an iterable of strings.
        SpanshError: If the system-level Spansh contract cannot be trusted.

    An injected session is reused and remains caller-owned. Recoverable station
    failures are returned as issues; failures that prevent trustworthy system
    discovery raise :class:`SpanshError`. Duplicate commodity identities are
    returned once, in their first requested order.
    """
    if isinstance(commodity_names, (str, bytes)):
        raise TypeError("commodity_names must be an iterable of commodity names")
    requested_names = tuple(commodity_names)
    if any(not isinstance(name, str) for name in requested_names):
        raise TypeError("commodity names must be strings")

    requested_by_key: dict[str, str] = {}
    for name in requested_names:
        canonical = canonical_commodity_name(name)
        requested_by_key.setdefault(
            _commodity_key(canonical or name),
            canonical or name,
        )

    if not requested_by_key:
        return ()

    # Session ownership stays with the caller when dependency-injected.
    if session is not None:
        return _fetch_commodities_with_session(
            session,
            system_name,
            requested_by_key,
        )

    # Sessions created here are closed here; injected sessions are not.
    owned_session = requests.Session()
    try:
        return _fetch_commodities_with_session(
            owned_session,
            system_name,
            requested_by_key,
        )
    finally:
        owned_session.close()


def _fetch_commodities_with_session(
    session: requests.Session,
    requested_system: str,
    requested_by_key: dict[str, str],
) -> tuple[CommodityMarketResult, ...]:
    """Fetch mapped commodity results using an already-owned session.

    The function first uses ``POST /systems/search`` to find an exact system
    match, then ``GET /system/{id64}`` to discover stations and nested bodies,
    then ``GET /station/{market_id}`` for each market-capable station. System
    contract failures are fatal; station and row failures are accumulated as
    recoverable issues.
    """
    system_id = _find_system_id(session, requested_system)
    if system_id is None:
        raise SpanshError(f"System {requested_system!r} was not found by exact name.")

    # GET /system/{id64} must echo the requested system name exactly by casefold.
    system_record = _fetch_record(session, "GET", f"/system/{system_id}")
    actual_system = system_record.get("name")
    if (
        not isinstance(actual_system, str)
        or actual_system.casefold() != requested_system.casefold()
    ):
        raise SpanshError("Spansh system detail did not match the requested system.")

    try:
        # Station discovery walks root stations and stations nested below bodies.
        stations, discovery_issues = _collect_stations(system_record)
    except ValueError as exc:
        raise SpanshError("Spansh returned an unusable system station listing.") from exc

    observations_by_key: dict[str, list[MarketObservation]] = {
        key: [] for key in requested_by_key
    }
    issues_by_key: dict[str, list[MarketIssue]] = {
        key: list(discovery_issues) for key in requested_by_key
    }

    def add_shared_issue(issue: MarketIssue) -> None:
        """Attach one recoverable station issue to every requested commodity."""
        for commodity_issues in issues_by_key.values():
            commodity_issues.append(issue)

    for market_id, discovery_record in stations.items():
        station_name = _station_name(discovery_record)
        if not _is_market_candidate(discovery_record):
            continue

        numeric_id = _usable_market_id(market_id)
        if numeric_id is None:
            add_shared_issue(
                MarketIssue(station_name, "Station has no usable market_id.")
            )
            continue

        try:
            # GET /station/{market_id} provides the market rows and update timestamps.
            record = _fetch_record(session, "GET", f"/station/{numeric_id}")
            detail_system = record.get("system_name")
            if (
                not isinstance(detail_system, str)
                or detail_system.casefold() != requested_system.casefold()
            ):
                raise ValueError("Station detail belongs to a different or unknown system.")
            if record.get("has_market") is False:
                continue

            station = _map_station(record, numeric_id)
            market = record.get("market")
            if not isinstance(market, list):
                raise ValueError("Station detail has no valid market list.")
            station_observations, row_issues, shared_row_issues = _map_requested_rows(
                market,
                station,
                requested_by_key,
                record,
            )
            for key, mapped in station_observations.items():
                observations_by_key[key].extend(mapped)
            for key, mapped_issues in row_issues.items():
                issues_by_key[key].extend(mapped_issues)
            for issue in shared_row_issues:
                add_shared_issue(issue)
        # Per-station failures are recoverable so other stations can still rank.
        except (SpanshError, ValueError, TypeError, KeyError) as exc:
            add_shared_issue(MarketIssue(station_name, str(exc)))

    return tuple(
        CommodityMarketResult(
            requested_commodity=commodity,
            observations=tuple(observations_by_key[key]),
            issues=tuple(issues_by_key[key]),
        )
        for key, commodity in requested_by_key.items()
    )


def _find_system_id(session: requests.Session, requested_name: str) -> str | int | None:
    """Return the id64 for an exact case-insensitive Spansh system match.

    ``POST /systems/search`` is paged with a fixed size. Pagination terminates
    when an exact name match is found, an empty page is consistent with the
    reported count, all reported ids have been seen, or a short count-less page
    indicates exhaustion.
    """
    page = 0
    seen_ids: set[str] = set()

    while True:
        payload = _request_json(
            session,
            "POST",
            # POST /systems/search accepts filters, size, and page in the JSON body.
            "/systems/search",
            json={
                "filters": {"name": {"value": requested_name}},
                "size": _SYSTEM_PAGE_SIZE,
                "page": page,
            },
        )
        results = payload.get("results")
        if not isinstance(results, list):
            raise SpanshError("Spansh system search response has no results list.")

        count = payload.get("count")
        if count is not None and (
            isinstance(count, bool) or not isinstance(count, int) or count < 0
        ):
            raise SpanshError("Spansh system search response has an invalid result count.")
        if count is not None and count < len(results):
            raise SpanshError("Spansh system search result count is inconsistent.")

        page_ids = set()
        exact_match: str | int | None = None
        for item in results:
            if not isinstance(item, dict):
                raise SpanshError("Spansh system search contains an invalid result.")
            name = item.get("name")
            identifier = item.get("id64")
            usable_identifier = (
                isinstance(identifier, int)
                and not isinstance(identifier, bool)
                and identifier >= 0
            ) or (isinstance(identifier, str) and identifier.isdecimal())
            if not isinstance(name, str) or not usable_identifier:
                raise SpanshError("Spansh system search result has no usable name or id64.")
            page_ids.add(str(identifier))
            # Spansh may return fuzzy names; only exact case-insensitive matches count.
            if name.casefold() == requested_name.casefold():
                exact_match = identifier
                break

        if exact_match is not None:
            return exact_match
        if not results:
            if count is not None and len(seen_ids) < count:
                raise SpanshError(
                    "Spansh system search ended before its reported result count."
                )
            return None
        # Repeated pages would otherwise create an infinite pagination loop.
        if page_ids.issubset(seen_ids):
            raise SpanshError("Spansh repeated a system search page before search completion.")

        seen_ids.update(page_ids)
        if count is not None and len(seen_ids) >= count:
            return None
        if count is None and len(results) < _SYSTEM_PAGE_SIZE:
            return None
        page += 1


def _collect_stations(
    system_record: dict[str, Any],
) -> tuple[dict[str, dict[str, Any]], tuple[MarketIssue, ...]]:
    """Collect market-id keyed station discovery records from a system tree.

    Spansh embeds stations both on the root system record and recursively within
    nested body records. Invalid body structure is fatal to discovery, while an
    individual invalid or market-capable station without a usable market id is a
    recoverable ``MarketIssue``.
    """
    stations: dict[str, dict[str, Any]] = {}
    issues = []

    def visit(node: dict[str, Any]) -> None:
        """Visit one system/body node and recurse through nested bodies."""
        station_records = node.get("stations", [])
        bodies = node.get("bodies", [])
        if not isinstance(station_records, list) or not isinstance(bodies, list):
            raise ValueError("System station or body listing is not a list.")
        for station in station_records:
            if not isinstance(station, dict):
                issues.append(MarketIssue("<unknown station>", "Invalid station discovery record."))
                continue
            market_id = station.get("market_id")
            if market_id is None:
                if _is_market_candidate(station):
                    issues.append(
                        MarketIssue(
                            _station_name(station),
                            "Station has no usable market_id.",
                        )
                    )
                continue
            key = str(market_id)
            stations[key] = station
        # Bodies may themselves contain stations and deeper child bodies.
        for body in bodies:
            if not isinstance(body, dict):
                raise ValueError("System body listing contains an invalid record.")
            visit(body)

    visit(system_record)
    return stations, tuple(issues)


def _is_market_candidate(record: dict[str, Any]) -> bool:
    """Return whether a discovery record claims that a market may exist."""
    services = record.get("services", [])
    return record.get("has_market") is True or (
        isinstance(services, list) and "Market" in services
    )


def _station_name(record: dict[str, Any]) -> str:
    """Return a displayable station name for issue reporting."""
    name = record.get("name")
    return name if isinstance(name, str) and name else "<unknown station>"


def _usable_market_id(market_id: str) -> int | None:
    """Convert a decimal market id string to an integer when possible."""
    value = market_id.strip()
    if not value.isdecimal():
        return None
    try:
        return int(value)
    except ValueError:
        return None


def _fetch_record(
    session: requests.Session,
    method: str,
    path: str,
) -> dict[str, Any]:
    """Fetch a Spansh endpoint and return its object ``record`` member.

    Raises:
        SpanshError: If the endpoint response is not an object with a usable
        object-valued ``record`` member.
    """
    payload = _request_json(session, method, path)
    record = payload.get("record")
    if not isinstance(record, dict):
        raise SpanshError(f"Spansh response for {path} has no usable record.")
    return record


def _request_json(
    session: requests.Session,
    method: str,
    path: str,
    **kwargs: Any,
) -> dict[str, Any]:
    """Call a Spansh API endpoint and return a JSON object payload.

    Args:
        session: Session used for the request.
        method: HTTP method, such as ``GET`` or ``POST``.
        path: API path relative to ``https://spansh.co.uk/api``.
        **kwargs: Additional ``requests`` keyword arguments.

    Raises:
        SpanshError: On transport errors, HTTP errors, invalid JSON, or a
        non-object top-level payload.
    """
    try:
        response = session.request(
            method,
            _BASE_URL + path,
            # Keep provider calls bounded so UI-facing polling cannot hang indefinitely.
            timeout=_REQUEST_TIMEOUT,
            **kwargs,
        )
        response.raise_for_status()
        payload = response.json()
    except (requests.RequestException, ValueError) as exc:
        raise SpanshError(f"Spansh request {method} {path} failed: {exc}") from exc
    if not isinstance(payload, dict):
        raise SpanshError(f"Spansh response for {path} is not an object.")
    return payload


def _map_station(record: dict[str, Any], market_id: int) -> Station:
    """Map a station detail record to an immutable domain station.

    Raises:
        ValueError: If required station identity fields or optional typed fields
        are malformed.
    """
    name = record.get("name")
    system_name = record.get("system_name")
    if not isinstance(name, str) or not name or not isinstance(system_name, str):
        raise ValueError("Station detail has no usable station or system name.")
    is_planetary = record.get("is_planetary")
    if is_planetary is not None and not isinstance(is_planetary, bool):
        raise ValueError("Station detail has an invalid planetary flag.")

    return Station(
        name=name,
        market_id=str(market_id),
        system_name=system_name,
        is_planetary=is_planetary,
        max_landing_pad=_max_landing_pad(record),
    )


def _max_landing_pad(record: dict[str, Any]) -> LandingPad | None:
    """Return the largest supported landing pad from Spansh station fields.

    The precedence is explicit ``has_large_pad`` first, then positive pad counts
    from ``large_pads`` to ``medium_pads`` to ``small_pads``. Unknown or invalid
    counts produce ``None`` instead of inventing a capability.
    """
    if record.get("has_large_pad") is True:
        return "L"
    # Count fields are checked largest-to-smallest to preserve pad precedence.
    for field, size in (("large_pads", "L"), ("medium_pads", "M"), ("small_pads", "S")):
        count = record.get(field)
        if isinstance(count, int) and not isinstance(count, bool) and count > 0:
            return size
    return None


def _map_requested_rows(
    market: list[Any],
    station: Station,
    requested_by_key: dict[str, str],
    station_record: dict[str, Any],
) -> tuple[
    dict[str, list[MarketObservation]],
    dict[str, list[MarketIssue]],
    list[MarketIssue],
]:
    """Map requested commodity rows from one station market list.

    Returns per-commodity observations, per-commodity issues for malformed
    matching rows, and shared station issues for malformed rows whose commodity
    identity cannot be trusted.
    """
    observations: dict[str, list[MarketObservation]] = {
        key: [] for key in requested_by_key
    }
    issues: dict[str, list[MarketIssue]] = {key: [] for key in requested_by_key}
    shared_issues: list[MarketIssue] = []
    for row in market:
        if not isinstance(row, dict):
            shared_issues.append(
                MarketIssue(station.name, "Market contains a malformed row.")
            )
            continue
        raw_name = row.get("commodity")
        if not isinstance(raw_name, str) or not raw_name:
            shared_issues.append(
                MarketIssue(station.name, "Market row has no usable commodity name.")
            )
            continue
        # Commodity matching is canonical and alias-aware across providers.
        key = _commodity_key(raw_name)
        if key not in requested_by_key:
            continue

        try:
            sell_price = _optional_integer(row.get("sell_price"), "sell_price")
            demand = _optional_integer(row.get("demand"), "demand")
            supply = _optional_integer(row.get("supply"), "supply")
        except ValueError as exc:
            issues[key].append(
                MarketIssue(station.name, f"Malformed data for {raw_name!r}: {exc}")
            )
            continue
        canonical = canonical_commodity_name(raw_name)
        observations[key].append(
            MarketObservation(
                station=station,
                commodity=canonical or raw_name,
                sell_price=sell_price,
                demand=demand,
                supply=supply,
                # Spansh timestamps are normalized to aware UTC datetimes when parseable.
                market_updated_at=_parse_utc(station_record.get("market_updated_at")),
                station_updated_at=_parse_utc(station_record.get("updated_at")),
            )
        )
    return observations, issues, shared_issues


def _optional_integer(value: Any, field_name: str) -> int | None:
    """Return an optional integer from Spansh numeric fields.

    ``None`` is accepted as unknown, booleans are rejected despite being integer
    subclasses, integers are returned directly, and decimal strings are parsed.
    """
    if value is None:
        return None
    if isinstance(value, bool):
        raise ValueError(f"{field_name} must be an integer or None.")
    if isinstance(value, int):
        return value
    if isinstance(value, str):
        try:
            return int(value)
        except ValueError:
            pass
    raise ValueError(f"{field_name} must be an integer or None.")


def _parse_utc(value: Any) -> datetime | None:
    """Parse a provider timestamp as an aware UTC datetime when possible.

    Missing, blank, malformed, and overflowing timestamps return ``None``.
    Naive ISO values are assumed to already be UTC, and ``Z`` suffixes are
    converted to the ``+00:00`` form accepted by ``datetime.fromisoformat``.
    """
    if not isinstance(value, str) or not value.strip():
        return None
    text = value.strip()
    # Normalize the common UTC suffix before using the standard ISO parser.
    if text.endswith("Z"):
        text = text[:-1] + "+00:00"
    try:
        parsed = datetime.fromisoformat(text)
        if parsed.tzinfo is None:
            parsed = parsed.replace(tzinfo=timezone.utc)
        return parsed.astimezone(timezone.utc)
    except (ValueError, OverflowError):
        return None
