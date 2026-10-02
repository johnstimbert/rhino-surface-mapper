"""Caller-selected market observation filtering policy.

This Qt-free market module keeps business filtering separate from provider
acquisition and UI presentation. It applies explicit caller options for fleet
carrier exclusion, demand thresholds, and positive sell prices without changing
or ranking the input observations.

Carrier detection is heuristic by station name: four-character names and the
``AAA-999`` style six-character-with-hyphen form are treated as carriers.
Demand thresholds use a strict ``>`` comparison so equal demand is not enough.
"""

import re
from collections.abc import Iterable

from .models import MarketObservation

_CARRIER_NAME_PATTERN = re.compile(r"[A-Za-z0-9]{3}-[A-Za-z0-9]{3}")


def is_carrier_name(name: str) -> bool:
    """Return whether a stripped station name matches carrier-name heuristics.

Args:
    name: Station name to evaluate.

Returns:
    ``True`` for names that are exactly four characters after stripping or match
    the ``AAA-999`` alphanumeric carrier-code form; otherwise ``False``.
"""
    # Carrier names are matched after whitespace trimming but without case changes.
    normalized_name = name.strip()
    return len(normalized_name) == 4 or bool(
        _CARRIER_NAME_PATTERN.fullmatch(normalized_name)
    )


def filter_market_observations(
    observations: Iterable[MarketObservation],
    *,
    exclude_carriers: bool = True,
    demand_greater_than: int | None = None,
    require_positive_sell_price: bool = False,
) -> tuple[MarketObservation, ...]:
    """Filter observations using only the caller's selected criteria.

    Args:
        observations: Market observations to evaluate in their current order.
        exclude_carriers: When true, omit station names matching carrier
            heuristics.
        demand_greater_than: Optional strict demand threshold.
        require_positive_sell_price: When true, omit missing, zero, and negative
            sell prices.

    Returns:
        A tuple containing observations that pass all active filters, preserving
        their input order.

    A demand threshold is strict; equal or unknown demand does not meet an active
    threshold. Positive sell-price filtering is applied only when requested.
    """
    eligible = []
    for observation in observations:
        if exclude_carriers and is_carrier_name(observation.station.name):
            continue
        if demand_greater_than is not None and not (
            observation.demand is not None
            and observation.demand > demand_greater_than
        ):
            continue
        if require_positive_sell_price and not (
            observation.sell_price is not None
            and observation.sell_price > 0
        ):
            continue
        eligible.append(observation)
    return tuple(eligible)
