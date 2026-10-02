"""Parse supplied INARA commodity summary HTML into market summaries.

This Qt-free provider parser belongs to the Elite Dangerous market domain. It
performs no HTTP requests and has no network side effects; callers supply the
HTML document that should be parsed.

The parser targets the mature INARA all-commodities table contract: commodity at
column index 0, average sell at index 1, and maximum sell at index 4. Unusable
numeric values become ``None`` rather than zero so unknown prices are not
mistaken for real zero-credit market values.
"""

from html.parser import HTMLParser
import re

from .commodities import canonical_commodity_name
from .models import (
    CommodityPriceSummary,
    CommoditySummaryResult,
    MarketIssue,
    _commodity_key,
)


class InaraParseError(ValueError):
    """Raised when supplied HTML has no usable INARA commodity summary table.

This is a document-shape error, not a network error. The module parses only the
provided text and never fetches INARA itself.
"""


class _InaraTableParser(HTMLParser):
    """HTML parser that collects table rows and normalized cell text.

The parser tracks nested table depth, row state, and cell text fragments. It does
not decide which table is INARA-specific; higher-level mapping validates columns
and commodity identities after all tables are collected.
"""

    def __init__(self) -> None:
        super().__init__()
        self._table_depth = 0
        self._in_row = False
        self._in_cell = False
        self._cell_parts: list[str] = []
        self._row: list[str] = []
        self._table_rows: list[tuple[str, ...]] = []
        self.tables: list[tuple[tuple[str, ...], ...]] = []

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        """Enter table, row, and cell states while streaming HTML tokens."""
        tag = tag.lower()
        # Table depth lets nested tables finish without corrupting outer rows.
        if tag == "table":
            if self._table_depth == 0:
                self._table_rows = []
            self._table_depth += 1
        elif self._table_depth and tag == "tr":
            self._in_row = True
            self._row = []
        elif self._in_row and tag in ("td", "th"):
            self._in_cell = True
            self._cell_parts = []

    def handle_endtag(self, tag: str) -> None:
        """Close cell, row, and table states and persist completed rows."""
        tag = tag.lower()
        if tag in ("td", "th") and self._in_cell:
            # Collapse HTML whitespace so number parsing sees INARA display text.
            self._row.append(" ".join(" ".join(self._cell_parts).split()))
            self._cell_parts = []
            self._in_cell = False
        elif tag == "tr" and self._in_row:
            if self._row:
                self._table_rows.append(tuple(self._row))
            self._row = []
            self._in_row = False
        elif tag == "table" and self._table_depth:
            self._table_depth -= 1
            if self._table_depth == 0:
                self.tables.append(tuple(self._table_rows))
                self._table_rows = []

    def handle_data(self, data: str) -> None:
        """Collect raw text only while inside a table cell."""
        if self._in_cell:
            self._cell_parts.append(data)


def _parse_credit_value(text: str) -> int | None:
    """Parse integer credit text in the grouping forms used by INARA.

Accepted formats contain digits grouped by spaces, commas, periods, apostrophes,
or curly apostrophes, with an optional case-insensitive ``Cr`` suffix. Invalid,
blank, or non-numeric text returns ``None`` so unavailable prices stay distinct
from actual zero-credit values.
"""
    # Invalid or unavailable credit text intentionally maps to None, not zero.
    match = re.fullmatch(
        r"\s*([0-9]+(?:[\s,.'’][0-9]+)*)\s*(?:Cr)?\s*",
        text,
        flags=re.IGNORECASE,
    )
    if match is None:
        return None

    # Strip display grouping separators only after the strict format has matched.
    return int(re.sub(r"[^0-9]", "", match.group(1)))


def parse_inara_summaries(
    document: str,
    requested: tuple[str, ...],
) -> CommoditySummaryResult:
    """Map supplied INARA all-commodities HTML to requested summaries.

    Args:
        document: HTML text already obtained by the caller. This function never
            performs HTTP.
        requested: Commodity names or aliases to extract from supported rows.

    Returns:
        A ``CommoditySummaryResult`` containing matched summaries and recoverable
        row issues.

    Raises:
        InaraParseError: If ``document`` is empty or no supported commodity table
        row can be found.

    Rows use the mature table columns: commodity at index 0, average
    sell at index 1, and maximum sell at index 4. Malformed relevant rows are
    reported as recoverable issues while usable rows remain available.
    """
    if not isinstance(document, str) or not document.strip():
        raise InaraParseError("INARA document must be non-empty text")

    parser = _InaraTableParser()
    # Parse only the supplied HTML; fetching is deliberately outside this module.
    parser.feed(document)
    parser.close()

    requested_by_key: dict[str, str] = {}
    for name in requested:
        requested_by_key.setdefault(_commodity_key(name), name)

    # Flatten every parsed table, then identify supported commodity rows by content.
    rows = tuple(row for table in parser.tables for row in table)
    has_supported_row = any(
        len(row) >= 5
        and row[0]
        and (
            canonical_commodity_name(row[0]) is not None
            or _commodity_key(row[0]) in requested_by_key
        )
        and (
            _parse_credit_value(row[1]) is not None
            or _parse_credit_value(row[4]) is not None
        )
        for row in rows
    )
    if not has_supported_row:
        raise InaraParseError(
            "INARA document has no supported commodity table rows"
        )

    summaries_by_key: dict[str, CommodityPriceSummary] = {}
    issues: list[MarketIssue] = []
    for row in rows:
        if not row:
            continue

        raw_name = row[0]
        key = _commodity_key(raw_name)
        if key not in requested_by_key:
            continue

        canonical = canonical_commodity_name(raw_name)
        commodity = canonical if canonical is not None else raw_name
        if len(row) < 5:
            issues.append(
                MarketIssue("INARA", f"Incomplete market row for {commodity}")
            )
            continue

        # Mature INARA table: column 1 is average sell and column 4 is maximum sell.
        average_sell = _parse_credit_value(row[1])
        maximum_sell = _parse_credit_value(row[4])
        # Missing prices remain unknown values; zero would be misleading market data.
        if average_sell is None and maximum_sell is None:
            issues.append(
                MarketIssue("INARA", f"No usable prices for {commodity}")
            )
            continue
        if average_sell is None or maximum_sell is None:
            issues.append(
                MarketIssue("INARA", f"One price is unavailable for {commodity}")
            )

        summaries_by_key[key] = CommodityPriceSummary(
            commodity=commodity,
            average_sell=average_sell,
            maximum_sell=maximum_sell,
            total_demand=None,
        )

    return CommoditySummaryResult(
        requested=requested,
        summaries=tuple(summaries_by_key.values()),
        issues=tuple(issues),
    )
