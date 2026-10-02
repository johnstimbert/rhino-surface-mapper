"""Qt-free market domain API for Elite Dangerous surface mining data.

The market subpackage is a self-contained part of the Elite Dangerous domain
layer. It exposes immutable value models, commodity catalogue helpers, provider
adapters for Spansh and INARA, caller-selected filtering, deterministic ranking,
and JSON cache persistence.

The public exports preserve a small provider-independent API: collections are
returned as tuples, recoverable provider defects are represented as
``MarketIssue`` values, and fatal failures are raised by the module that cannot
produce a trustworthy result.
"""

from .commodities import (
    CatalogueFormatError,
    COMMODITY_ALIASES,
    SURFACE_COMMODITIES,
    canonical_commodity_name,
    normalize_name,
)
from .models import (
    CommodityMarketResult,
    CommodityPriceSummary,
    CommoditySummaryResult,
    LandingPad,
    MarketIssue,
    MarketObservation,
    Station,
)
from .policy import filter_market_observations, is_carrier_name
from .ranking import rank_market_observations
from .spansh import SpanshError, fetch_commodity_market, fetch_commodity_markets
from .inara import InaraParseError, parse_inara_summaries
from .cache import (
    CacheFormatError,
    SummaryCache,
    load_summary_cache,
    save_summary_cache,
)

__all__ = [
    "CatalogueFormatError",
    "COMMODITY_ALIASES",
    "SURFACE_COMMODITIES",
    "canonical_commodity_name",
    "normalize_name",
    "CommodityMarketResult",
    "CommodityPriceSummary",
    "CommoditySummaryResult",
    "LandingPad",
    "MarketIssue",
    "MarketObservation",
    "Station",
    "filter_market_observations",
    "is_carrier_name",
    "rank_market_observations",
    "SpanshError",
    "fetch_commodity_market",
    "fetch_commodity_markets",
    "InaraParseError",
    "parse_inara_summaries",
    "CacheFormatError",
    "SummaryCache",
    "load_summary_cache",
    "save_summary_cache",
]
