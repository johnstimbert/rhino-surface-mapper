"""Deterministic business ranking for market observations.

This Qt-free market module orders provider-independent observations for consumer
presentation without mutating or filtering them. The exact sort key is: known
sell price before unknown, higher sell price, known demand before unknown,
higher demand, then larger landing pad capability.

Python's stable sort is part of the contract: observations with identical keys
retain provider/input order, which avoids arbitrary churn in ties.
"""

from collections.abc import Iterable

from .models import MarketObservation

_PAD_RANK = {"L": 3, "M": 2, "S": 1}


def _ranking_key(observation: MarketObservation) -> tuple[bool, int, bool, int, int]:
    """Build the exact ascending sort key for business ranking.

Args:
    observation: Market observation to rank.

Returns:
    A tuple sorting known sell prices before unknown, higher sell prices before
    lower, known demand before unknown, higher demand before lower, and larger
    landing pads before smaller pads.
"""
    price = observation.sell_price
    demand = observation.demand
    pad_rank = _PAD_RANK.get(observation.station.max_landing_pad, 0)
    # Booleans place known values first; negated numbers make sorted() descending.
    return (
        price is None,
        -(price if price is not None else 0),
        demand is None,
        -(demand if demand is not None else 0),
        -pad_rank,
    )


def rank_market_observations(
    observations: Iterable[MarketObservation],
) -> tuple[MarketObservation, ...]:
    """Order observations by the market business ranking.

    Args:
        observations: Observations to rank.

    Returns:
        A tuple sorted by known/high sell price, known/high demand, then landing
        pad size from large to small.

    Unknown values rank after known values for their criterion. Python's stable
    sort preserves input order for observations with identical keys.
    """
    return tuple(sorted(observations, key=_ranking_key))
