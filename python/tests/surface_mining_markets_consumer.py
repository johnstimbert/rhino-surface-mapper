"""Surface Mining Markets consumer tying the market subpackage together.

The consumer loads the 37-product surface commodity catalogue, batch-fetches a
local system from Spansh, applies the reusable market policy, ranks stations,
and renders at most three top products with their best local stations.

Local eligibility deliberately excludes fleet carriers, requires demand greater
than 100 tonnes, and requires a positive sell price before the shared ranking
chooses stations by price, demand, and pad capability.

Global INARA summaries are maintained separately in
``inara_summary_cache_v1.json`` with a one-hour TTL. A stale cache is preserved
when acquisition, parsing, or saving fails because older complete data is safer
than no data or partially trusted data. Partial or issue-bearing refreshes never
replace a good cache, so a transient INARA problem cannot degrade the stored
summary used by future runs.

INARA acquisition is a developer-facing path that launches an installed Chrome
or Edge browser with ``--headless=new --dump-dom`` against inara.cz, using a
temporary profile and a 60-second timeout. Importing this module must have no
side effects: no fetch, prompt, browser launch, or cache write may occur until
``main`` is called.
"""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path
from time import perf_counter


SCRIPT_DIRECTORY = Path(__file__).resolve().parent
REPOSITORY_ROOT = SCRIPT_DIRECTORY.parent
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))

from elite_dangerous.market import (  # noqa: E402
    CacheFormatError,
    CommoditySummaryResult,
    InaraParseError,
    MarketIssue,
    MarketObservation,
    SpanshError,
    SURFACE_COMMODITIES,
    fetch_commodity_markets,
    filter_market_observations,
    load_summary_cache,
    parse_inara_summaries,
    rank_market_observations,
    save_summary_cache,
)


INARA_COMMODITIES_URL = "https://inara.cz/elite/commodities-list/"
CACHE_FILENAME = "inara_summary_cache_v1.json"
CACHE_PATH = SCRIPT_DIRECTORY / CACHE_FILENAME
INARA_CACHE_MAX_AGE = timedelta(hours=1)
HEADLESS_TIMEOUT_SECONDS = 60

MIN_DEMAND = 100
TOP_PRODUCTS = 3
TOP_STATIONS = 3


def format_number(value: int | None) -> str:
    """Format a known integer, leaving unavailable values visibly unknown."""
    if value is None:
        return "—"
    return f"{value:,}".replace(",", " ")


def data_age(value: datetime | None, *, now: datetime | None = None) -> str:
    """Return a compact age for a UTC-aware market timestamp."""
    if value is None:
        return "?"
    if value.tzinfo is None or value.utcoffset() is None:
        return "?"

    current_time = datetime.now(timezone.utc) if now is None else now
    seconds = max(0, int((current_time - value.astimezone(timezone.utc)).total_seconds()))
    if seconds < 60:
        return f"{seconds}s"
    minutes = seconds // 60
    if minutes < 60:
        return f"{minutes}m"
    hours = minutes // 60
    if hours < 48:
        return f"{hours}h"
    return f"{hours // 24}d"


def find_headless_browser() -> Path | None:
    """Find an installed Chrome or Edge executable for the INARA page."""
    candidates = (
        Path(os.environ.get("PROGRAMFILES", ""))
        / "Google/Chrome/Application/chrome.exe",
        Path(os.environ.get("PROGRAMFILES(X86)", ""))
        / "Microsoft/Edge/Application/msedge.exe",
        Path(os.environ.get("PROGRAMFILES", ""))
        / "Microsoft/Edge/Application/msedge.exe",
        Path(os.environ.get("LOCALAPPDATA", ""))
        / "Microsoft/Edge/Application/msedge.exe",
    )
    return next((candidate for candidate in candidates if candidate.is_file()), None)


def browser_page_diagnostics(html_text: str) -> bool:
    """Detect common browser challenge and unavailable-page responses."""
    lower_html = html_text[:200_000].casefold()
    challenge_markers = (
        "captcha",
        "challenge-platform",
        "cf-chl-",
        "just a moment",
        "verify you are human",
        "checking your browser",
        "enable javascript and cookies",
        "service unavailable",
    )
    return any(marker in lower_html for marker in challenge_markers)


def fetch_inara_html() -> str:
    """Acquire the INARA commodity page through a developer-run browser path."""
    browser_path = find_headless_browser()
    if browser_path is None:
        raise RuntimeError("No installed Edge or Chrome browser was found.")

    # A throwaway profile avoids contaminating or depending on a user's browser state.
    temporary_profile = Path(tempfile.mkdtemp(prefix="inara-headless-"))
    command = [
        str(browser_path),
        "--headless=new",
        "--disable-gpu",
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-background-networking",
        f"--user-data-dir={temporary_profile}",
        "--virtual-time-budget=15000",
        "--dump-dom",
        INARA_COMMODITIES_URL,
    ]
    try:
        result = subprocess.run(
            command,
            capture_output=True,
            timeout=HEADLESS_TIMEOUT_SECONDS,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError(
            f"Browser exceeded the {HEADLESS_TIMEOUT_SECONDS}-second timeout."
        ) from exc
    finally:
        shutil.rmtree(temporary_profile, ignore_errors=True)

    html_text = result.stdout.decode("utf-8", errors="replace")
    has_commodity_content = bool(
        re.search(r"(?i)<table\b|\bhelium\b|\bplatinum\b|commodit", html_text)
    )
    if result.returncode != 0:
        raise RuntimeError(
            f"Browser exited with return code {result.returncode}."
        )
    if not html_text:
        raise RuntimeError("Browser returned no HTML.")
    if browser_page_diagnostics(html_text):
        raise RuntimeError("INARA returned a browser challenge or error page.")
    if not has_commodity_content:
        raise RuntimeError("Browser HTML does not appear to contain commodities.")
    return html_text


def query_system_markets(
    name: str,
    *,
    progress=print,
) -> tuple[str, dict[str, tuple[MarketObservation, ...]], tuple[MarketIssue, ...]]:
    """Fetch the full surface catalogue from Spansh and keep eligible markets."""
    progress(
        f"Querying {len(SURFACE_COMMODITIES)} commodities in {name} "
        "with one Spansh market traversal..."
    )
    results = fetch_commodity_markets(name, SURFACE_COMMODITIES)

    observations_by_commodity: dict[str, tuple[MarketObservation, ...]] = {}
    issues: list[MarketIssue] = []
    seen_issues: set[MarketIssue] = set()
    exact_name = name
    for result in results:
        if result.observations:
            exact_name = result.observations[0].station.system_name
        # The consumer policy mirrors the manual Surface Mining workflow.
        eligible = filter_market_observations(
            result.observations,
            exclude_carriers=True,
            demand_greater_than=MIN_DEMAND,
            require_positive_sell_price=True,
        )
        ranked = rank_market_observations(eligible)
        if ranked:
            observations_by_commodity[result.requested_commodity] = ranked

        for issue in result.issues:
            if issue not in seen_issues:
                seen_issues.add(issue)
                issues.append(issue)

    progress(
        f"Spansh returned qualifying markets for "
        f"{len(observations_by_commodity)} commodities; "
        f"{len(issues)} distinct issue(s)."
    )
    return exact_name, observations_by_commodity, tuple(issues)


def load_or_refresh_inara(
    cache_path: Path = CACHE_PATH,
    *,
    now: datetime | None = None,
) -> tuple[CommoditySummaryResult | None, str, datetime | None, str | None]:
    """Return fresh INARA summaries while preserving safe stale cache fallbacks."""
    requested = tuple(SURFACE_COMMODITIES)
    current_time = datetime.now(timezone.utc) if now is None else now
    cached = None
    cache_problem: str | None = None

    try:
        cached = load_summary_cache(cache_path, requested)
    except FileNotFoundError:
        cache_problem = "summary cache is missing"
    except (CacheFormatError, json.JSONDecodeError) as exc:
        cache_problem = f"summary cache is invalid: {exc}"
    except OSError as exc:
        cache_problem = f"summary cache could not be read: {exc}"

    # Freshness is intentionally delegated to the cache model's strict TTL boundary.
    if cached is not None and cached.is_fresh(INARA_CACHE_MAX_AGE, now=current_time):
        return cached.result, "fresh cache", cached.stored_at, None

    try:
        html_text = fetch_inara_html()
    except (OSError, RuntimeError) as exc:
        refresh_problem = f"INARA acquisition failed: {exc}"
        return _inara_fallback(cached, cache_problem, refresh_problem)

    try:
        refreshed = parse_inara_summaries(html_text, requested)
    except InaraParseError as exc:
        refresh_problem = f"INARA parsing failed: {exc}"
        return _inara_fallback(cached, cache_problem, refresh_problem)

    # Do not replace a complete cache with partial or suspect INARA data.
    if not refreshed.is_complete or refreshed.issues:
        refresh_problem = (
            "INARA refresh was incomplete "
            f"({len(refreshed.missing)} missing, {len(refreshed.issues)} issue(s)); "
            "partial values were not used."
        )
        return _inara_fallback(cached, cache_problem, refresh_problem)

    try:
        save_summary_cache(cache_path, refreshed, stored_at=current_time)
    except (OSError, ValueError) as exc:
        if cached is not None:
            return (
                cached.result,
                "stale cache fallback",
                cached.stored_at,
                f"INARA refresh was valid but the cache could not be saved: {exc}",
            )
        return (
            refreshed,
            "refreshed, cache save failed",
            current_time,
            f"INARA values are usable for this run, but the cache could not be saved: {exc}",
        )
    return refreshed, "refreshed", current_time, None


def _inara_fallback(
    cached,
    cache_problem: str | None,
    refresh_problem: str,
) -> tuple[CommoditySummaryResult | None, str, datetime | None, str]:
    """Prefer stale complete INARA data over failed or unsafe refresh attempts."""
    details = "; ".join(
        detail for detail in (cache_problem, refresh_problem) if detail
    )
    if cached is not None:
        return cached.result, "stale cache fallback", cached.stored_at, details
    return None, "unavailable", None, details


def select_top_products(
    results: dict[str, tuple[MarketObservation, ...]],
    limit: int = TOP_PRODUCTS,
) -> tuple[tuple[str, tuple[MarketObservation, ...]], ...]:
    """Select products by their best station using reusable ranking semantics."""
    if limit <= 0:
        return ()
    ordered_observations = rank_market_observations(
        observation
        for product_observations in results.values()
        for observation in product_observations
    )
    selected: list[tuple[str, tuple[MarketObservation, ...]]] = []
    seen_products: set[str] = set()
    for observation in ordered_observations:
        product = observation.commodity
        if product in seen_products or product not in results:
            continue
        seen_products.add(product)
        selected.append((product, results[product]))
        if len(selected) >= limit:
            break
    return tuple(selected)


def _cache_label(status: str, stored_at: datetime | None) -> str:
    """Build the display label for a fresh, stale, refreshed, or missing cache."""
    if stored_at is None:
        return f"INARA ({status})"
    age_seconds = max(
        0,
        int((datetime.now(timezone.utc) - stored_at).total_seconds()),
    )
    age_hours, remainder = divmod(age_seconds, 3600)
    return (
        f"INARA ({status}; stored {stored_at.astimezone(timezone.utc):%Y-%m-%d %H:%M} UTC, "
        f"{age_hours}h{remainder // 60:02d}m old)"
    )


def print_results(
    system_name: str,
    selected: tuple[tuple[str, tuple[MarketObservation, ...]], ...],
    summary_result: CommoditySummaryResult | None,
    cache_status: str,
    cache_time: datetime | None,
    cache_warning: str | None,
    issues: tuple[MarketIssue, ...],
    elapsed: float,
) -> None:
    """Present the selected local markets and available global summaries."""
    print()
    print("=" * 112)
    print(
        f"SURFACE MINING — {system_name.upper()} "
        f"| {len(SURFACE_COMMODITIES)} PRODUCTS "
        f"| LOCAL DEMAND > {format_number(MIN_DEMAND)} t"
    )
    print("=" * 112)

    if cache_warning:
        print(f"INARA warning: {cache_warning}")
    if not selected:
        print("No products met the local market criteria.")

    summaries = (
        {summary.commodity: summary for summary in summary_result.summaries}
        if summary_result is not None
        else {}
    )
    source_label = _cache_label(cache_status, cache_time)
    for number, (product, stations) in enumerate(selected, 1):
        summary = summaries.get(product)
        print()
        print(f"{number}. {product.upper()}")
        if summary is not None and (
            summary.average_sell is not None or summary.maximum_sell is not None
        ):
            print(
                f"   {source_label} — global average: "
                f"{format_number(summary.average_sell)} Cr/t | global maximum: "
                f"{format_number(summary.maximum_sell)} Cr/t"
            )
        else:
            print(f"   {source_label} — global prices unavailable.")

        headers = ["Station", "Sell (Cr/t)", "Demand (t)", "Pad", "Market UTC", "Age"]
        rows = [
            [
                observation.station.name,
                format_number(observation.sell_price),
                format_number(observation.demand),
                observation.station.max_landing_pad or "?",
                observation.market_updated_at.isoformat()
                if observation.market_updated_at is not None
                else "Unknown",
                data_age(observation.market_updated_at),
            ]
            for observation in stations[:TOP_STATIONS]
        ]
        widths = [
            max(len(header), max((len(str(row[i])) for row in rows), default=0))
            for i, header in enumerate(headers)
        ]
        print(
            "   "
            + " | ".join(header.ljust(widths[i]) for i, header in enumerate(headers))
        )
        print("   " + "-+-".join("-" * width for width in widths))
        for row in rows:
            print("   " + " | ".join(str(value).ljust(widths[i]) for i, value in enumerate(row)))

    if summary_result is not None and summary_result.missing:
        print(
            f"INARA has no summary for {len(summary_result.missing)} requested product(s); "
            "those values remain unavailable."
        )
    if issues:
        print()
        print("Partial local result — station issues:")
        for issue in issues:
            print(f"  {issue.station_name}: {issue.message}")
    print()
    print(f"{len(selected)} products shown; {len(issues)} distinct station issue(s).")
    print(f"Elapsed: {elapsed:.2f} s")


def main() -> int:
    """Run the argv or prompt-driven consumer without import-time side effects."""
    system_name = (
        " ".join(sys.argv[1:]).strip()
        if len(sys.argv) > 1
        else input("System name: ").strip()
    )
    if not system_name:
        print("Enter a system name.")
        return 1

    started = perf_counter()
    try:
        exact_name, local_results, issues = query_system_markets(system_name)
    except SpanshError as exc:
        print(f"Could not query the system: {exc}")
        return 1

    summary_result, cache_status, cache_time, cache_warning = load_or_refresh_inara()
    selected = select_top_products(local_results)
    print_results(
        exact_name,
        selected,
        summary_result,
        cache_status,
        cache_time,
        cache_warning,
        issues,
        perf_counter() - started,
    )
    return 2 if issues else 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (KeyboardInterrupt, EOFError):
        print("\nQuery cancelled.")
        raise SystemExit(130)
