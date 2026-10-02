"""Immutable market value objects shared by provider and UI code.

This module is the Qt-free model boundary for the Elite Dangerous market domain.
It defines provider-independent stations, observations, summaries, issues, and
result containers used by Spansh, INARA, cache, filtering, and ranking modules.

All public dataclasses are frozen value objects. Collection fields must be tuples
so result snapshots remain immutable, hash-safe in intent, and cannot change
under consumers after provider parsing has completed.
"""

from dataclasses import dataclass
from datetime import datetime
from typing import Literal

from .commodities import canonical_commodity_name, normalize_name

LandingPad = Literal["L", "M", "S"]


def _commodity_key(name: str) -> str:
    """Return an alias-aware commodity comparison key.

Args:
    name: Commodity name or alias to normalize.

Returns:
    The canonical normalized commodity key when possible. If normalization would
    produce an empty key, a casefolded original spelling is used instead so two
    distinct non-alphanumeric names do not collapse into the same identity.
"""
    canonical = canonical_commodity_name(name)
    comparison_name = canonical if canonical is not None else name
    # Empty normalized keys keep distinct raw spellings separate for missing logic.
    normalized = normalize_name(comparison_name)
    return normalized if normalized else comparison_name.casefold()


def _require_tuple(value: object, field_name: str) -> None:
    """Ensure frozen result objects do not retain mutable collections.

Args:
    value: Field value to validate.
    field_name: Name used in the exception message.

Raises:
    TypeError: If ``value`` is not a tuple.
"""
    if not isinstance(value, tuple):
        raise TypeError(f"{field_name} must be a tuple")


@dataclass(frozen=True)
class Station:
    """Immutable station identity and landing capability.

``name`` and ``system_name`` identify where an observation was made.
``market_id`` is provider-specific and may be absent when discovery failed.
``is_planetary`` and ``max_landing_pad`` preserve provider knowledge without UI
interpretation. The frozen dataclass keeps station metadata stable inside
observations.
"""

    name: str
    market_id: str | None
    system_name: str
    is_planetary: bool | None
    max_landing_pad: LandingPad | None


@dataclass(frozen=True)
class MarketObservation:
    """Immutable commodity market observation for one station.

The value object combines station identity, commodity name, optional sell price,
demand, supply, and provider update timestamps. Numeric fields are optional
because providers can omit or reject individual values; missing values remain
``None`` rather than being converted to sentinel numbers.
"""

    station: Station
    commodity: str
    sell_price: int | None
    demand: int | None
    supply: int | None
    market_updated_at: datetime | None
    station_updated_at: datetime | None


@dataclass(frozen=True)
class MarketIssue:
    """Recoverable provider or parsing issue tied to a station-like source.

Issues explain why part of a result could not be trusted while allowing other
observations or summaries to remain usable. They are immutable so diagnostics
reported with a result cannot drift after creation.
"""

    station_name: str
    message: str


@dataclass(frozen=True)
class CommodityMarketResult:
    """Immutable result for one requested commodity.

``observations`` contains provider rows matching the requested commodity identity;
``issues`` contains recoverable defects encountered during collection. Both
collections must be tuples to preserve result immutability across consumers.
``missing`` and ``is_complete`` use normalized, alias-aware commodity identities.
"""

    requested_commodity: str
    observations: tuple[MarketObservation, ...]
    issues: tuple[MarketIssue, ...]

    def __post_init__(self) -> None:
        _require_tuple(self.observations, "observations")
        _require_tuple(self.issues, "issues")

    @property
    def missing(self) -> tuple[str, ...]:
        """Return the requested name if no observation matches its commodity identity.

Returns:
    An empty tuple when at least one observation matches the requested commodity
after canonical alias resolution and normalization; otherwise a one-item tuple
containing the originally requested display name.
"""
        requested_key = _commodity_key(self.requested_commodity)
        if any(
            _commodity_key(item.commodity) == requested_key
            for item in self.observations
        ):
            return ()
        return (self.requested_commodity,)

    @property
    def is_complete(self) -> bool:
        """Return whether the commodity has data and no recoverable issues.

A result is complete only when ``missing`` is empty and the provider reported no
``MarketIssue`` values. Alias-aware matching prevents alternate spellings from
being treated as missing.
"""
        return not self.missing and not self.issues


@dataclass(frozen=True)
class CommodityPriceSummary:
    """Immutable provider-independent commodity price summary.

The summary stores global average sell price, maximum sell price, and total
demand when known. Optional numeric fields use ``None`` for unavailable values so
callers can distinguish unknown data from a real zero.
"""

    commodity: str
    average_sell: int | None
    maximum_sell: int | None
    total_demand: int | None


@dataclass(frozen=True)
class CommoditySummaryResult:
    """Immutable result for a batch of requested commodity summaries.

``requested`` preserves the caller's order, ``summaries`` contains returned
provider-independent summaries, and ``issues`` lists recoverable parse/provider
problems. All collection fields must be tuples to keep the frozen object truly
immutable.
"""

    requested: tuple[str, ...]
    summaries: tuple[CommodityPriceSummary, ...]
    issues: tuple[MarketIssue, ...] = ()

    def __post_init__(self) -> None:
        _require_tuple(self.requested, "requested")
        _require_tuple(self.summaries, "summaries")
        _require_tuple(self.issues, "issues")

    @property
    def missing(self) -> tuple[str, ...]:
        """Return requested commodity names absent from the returned summaries.

Missing detection uses canonical aliases and normalized names. Duplicate request
identities are reported once in first-request order, while distinct names whose
normalized key would be empty remain distinct through ``_commodity_key``.
"""
        # Returned identities are compared through the same alias-aware key as requests.
        returned = {_commodity_key(summary.commodity) for summary in self.summaries}
        missing = []
        seen = set()
        for commodity in self.requested:
            key = _commodity_key(commodity)
            if key not in returned and key not in seen:
                missing.append(commodity)
                seen.add(key)
        return tuple(missing)

    @property
    def is_complete(self) -> bool:
        """Return whether every request has a summary and no issues occurred.

Completeness is intentionally stricter than having any summaries: every unique
requested identity must be present and the recoverable-issue list must be empty.
"""
        return not self.missing and not self.issues
