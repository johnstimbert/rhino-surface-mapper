"""Versioned JSON persistence for reusable commodity summary results.

This Qt-free market module is the persistence boundary for cached INARA-style
summary data. It validates a closed schema, normalizes stored commodity names,
and returns immutable result objects without involving UI code.

Cache timestamps are timezone-aware and normalized to UTC. Freshness uses the
strict rule ``age < max_age``. Writes are atomic within the destination directory
and fsync the temporary file before replacement to avoid partially written cache
files being observed as complete.
"""

from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
import json
import os
from os import PathLike
from pathlib import Path
import tempfile
from typing import Any

from .commodities import canonical_commodity_name
from .models import (
    CommodityPriceSummary,
    CommoditySummaryResult,
    _commodity_key,
)

_SCHEMA_VERSION = 1
_TOP_LEVEL_FIELDS = {"version", "stored_at", "summaries"}
_SUMMARY_FIELDS = {
    "commodity",
    "average_sell",
    "maximum_sell",
    "total_demand",
}


class CacheFormatError(ValueError):
    """Raised when valid JSON violates the supported cache schema.

Malformed JSON remains ``json.JSONDecodeError``. This error is reserved for
wrong fields, unsupported versions, invalid timestamps, duplicate commodity
identities, or invalid summary records after JSON decoding succeeds.
"""


@dataclass(frozen=True)
class SummaryCache:
    """Immutable cache value containing a result and its storage timestamp.

``result`` must be a ``CommoditySummaryResult`` and ``stored_at`` is normalized
to an aware UTC ``datetime``. The frozen dataclass keeps loaded cache metadata
stable while callers evaluate freshness.
"""

    result: CommoditySummaryResult
    stored_at: datetime

    def __post_init__(self) -> None:
        """Validate result type and normalize the storage timestamp to UTC."""
        if not isinstance(self.result, CommoditySummaryResult):
            raise TypeError("result must be a CommoditySummaryResult")
        object.__setattr__(self, "stored_at", _aware_utc(self.stored_at, "stored_at"))

    def is_fresh(
        self,
        max_age: timedelta,
        *,
        now: datetime | None = None,
    ) -> bool:
        """Return whether the cache age is strictly below ``max_age``.

Args:
    max_age: Non-negative maximum accepted cache age.
    now: Optional comparison time. Naive datetimes are rejected; aware values are
        normalized to UTC.

Returns:
    ``True`` only when ``age < max_age``. Equality is stale by design so callers
    never reuse entries exactly at the expiry boundary.

Raises:
    ValueError: If ``max_age`` is negative or not a ``timedelta``, or if ``now``
    is not an aware valid ``datetime``.
"""
        if not isinstance(max_age, timedelta) or max_age < timedelta(0):
            raise ValueError("max_age must be a non-negative timedelta")

        current_time = (
            datetime.now(timezone.utc) if now is None else _aware_utc(now, "now")
        )
        # Clamp clock skew so future stored_at values are considered age zero.
        age = max(current_time - self.stored_at, timedelta(0))
        # Freshness is strict: age equal to max_age is already stale.
        return age < max_age


def load_summary_cache(
    path: str | PathLike[str],
    requested: tuple[str, ...],
) -> SummaryCache:
    """Load a validated cache and return only the caller's requested summaries.

    Args:
        path: Cache JSON path to read.
        requested: Tuple of requested commodity names or aliases.

    Returns:
        A ``SummaryCache`` whose result contains summaries matching requested
        commodity identities and no issues.

    Raises:
        TypeError: If ``requested`` is not a tuple of strings.
        OSError: For missing files or I/O failures.
        json.JSONDecodeError: If the file is not valid JSON.
        CacheFormatError: If valid JSON violates the versioned schema.

    Missing files and I/O errors retain their standard exceptions. Malformed
    JSON raises :class:`json.JSONDecodeError`; valid JSON with an invalid cache
    schema raises :class:`CacheFormatError`.
    """
    _validate_requested(requested)
    cache_path = Path(path)
    try:
        with cache_path.open("r", encoding="utf-8") as cache_file:
            payload = json.load(cache_file)
    except UnicodeDecodeError as exc:
        raise CacheFormatError("cache is not valid UTF-8") from exc

    stored_at, all_summaries = _decode_payload(payload)
    # Filtering uses the same alias-aware identity key as provider results.
    requested_keys = {_commodity_key(name) for name in requested}
    summaries = tuple(
        summary
        for summary in all_summaries
        if _commodity_key(summary.commodity) in requested_keys
    )
    result = CommoditySummaryResult(
        requested=requested,
        summaries=summaries,
        issues=(),
    )
    return SummaryCache(result=result, stored_at=stored_at)


def save_summary_cache(
    path: str | PathLike[str],
    result: CommoditySummaryResult,
    *,
    stored_at: datetime | None = None,
) -> None:
    """Validate and atomically save summary records as versioned JSON.

Args:
    path: Destination cache path.
    result: Complete issue-free summary result to cache.
    stored_at: Optional aware timestamp. ``None`` uses the current UTC time.

Raises:
    TypeError: If ``result`` is not a ``CommoditySummaryResult``.
    ValueError: If the result contains issues, has no summaries, contains invalid
        summary values, duplicate commodity identities, or an invalid timestamp.
    OSError: If the destination or temporary file cannot be written or replaced.
"""
    payload = _encode_result(result, stored_at)
    # A trailing newline keeps the cache human-readable and stable for diffs.
    serialized = json.dumps(payload, ensure_ascii=False, indent=2) + "\n"

    destination = Path(path)
    temporary_path: Path | None = None
    try:
        # Write beside the destination so os.replace remains atomic on the same volume.
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            newline="\n",
            prefix=f".{destination.name}.",
            suffix=".tmp",
            dir=destination.parent,
            delete=False,
        ) as temporary_file:
            temporary_path = Path(temporary_file.name)
            temporary_file.write(serialized)
            temporary_file.flush()
            # fsync the file contents before the atomic replace exposes the new cache.
            os.fsync(temporary_file.fileno())

        # Atomic replacement prevents readers from seeing a partially written cache.
        os.replace(temporary_path, destination)
        temporary_path = None
    finally:
        if temporary_path is not None:
            try:
                temporary_path.unlink()
            except FileNotFoundError:
                pass
            except OSError:
                pass


def _aware_utc(value: datetime, field_name: str) -> datetime:
    """Validate an aware ``datetime`` and normalize it to UTC.

Args:
    value: Timestamp to validate.
    field_name: Name used in exception messages.

Returns:
    The timestamp converted to ``timezone.utc``.

Raises:
    ValueError: If the value is not a datetime, is naive, or has an invalid
    timezone offset.
"""
    if not isinstance(value, datetime) or value.tzinfo is None:
        raise ValueError(f"{field_name} must be a timezone-aware datetime")
    try:
        offset = value.utcoffset()
    except (OverflowError, ValueError) as exc:
        raise ValueError(f"{field_name} has an invalid timezone") from exc
    if offset is None:
        raise ValueError(f"{field_name} must be a timezone-aware datetime")
    return value.astimezone(timezone.utc)


def _validate_requested(requested: tuple[str, ...]) -> None:
    """Validate that requested commodity names are an immutable string tuple."""
    if not isinstance(requested, tuple):
        raise TypeError("requested must be a tuple")
    if any(not isinstance(name, str) for name in requested):
        raise TypeError("requested commodity names must be strings")


def _decode_payload(payload: Any) -> tuple[datetime, tuple[CommodityPriceSummary, ...]]:
    """Decode and validate the complete versioned cache payload.

    The supported schema is closed: top-level fields must be exactly ``version``,
    ``stored_at``, and ``summaries``. The version must equal the module schema
    version before records are trusted.
    """
    if not isinstance(payload, dict):
        raise CacheFormatError("cache must be a JSON object")
    # Closed schema avoids silently accepting stale or future cache layouts.
    if set(payload) != _TOP_LEVEL_FIELDS:
        raise CacheFormatError("cache fields do not match the supported schema")
    version = payload["version"]
    # ``bool`` is excluded by exact type checking even though it subclasses int.
    if type(version) is not int or version != _SCHEMA_VERSION:
        raise CacheFormatError(f"unsupported cache version: {version!r}")

    stored_at = _parse_stored_at(payload["stored_at"])
    records = payload["summaries"]
    if not isinstance(records, list) or not records:
        raise CacheFormatError("cache summaries must be a nonempty list")

    summaries = []
    seen_keys = set()
    for index, record in enumerate(records):
        summary = _decode_summary(record, index)
        key = _commodity_key(summary.commodity)
        # Duplicate normalized identities would make request filtering ambiguous.
        if key in seen_keys:
            raise CacheFormatError(
                f"cache contains duplicate commodity identity: {summary.commodity!r}"
            )
        seen_keys.add(key)
        summaries.append(summary)

    return stored_at, tuple(summaries)


def _parse_stored_at(value: Any) -> datetime:
    """Parse a cache timestamp and require an aware UTC-normalizable value."""
    if not isinstance(value, str):
        raise CacheFormatError("stored_at must be an ISO timestamp string")
    try:
        parsed = datetime.fromisoformat(value)
        return _aware_utc(parsed, "stored_at")
    except (OverflowError, ValueError) as exc:
        raise CacheFormatError("stored_at must be a valid aware timestamp") from exc


def _decode_summary(record: Any, index: int) -> CommodityPriceSummary:
    """Decode one summary record from the cache schema.

    Raises:
        CacheFormatError: If the record has wrong fields, an invalid commodity,
        or non-integer numeric values.
    """
    if not isinstance(record, dict) or set(record) != _SUMMARY_FIELDS:
        raise CacheFormatError(f"summary record {index} has invalid fields")

    raw_name = record["commodity"]
    if not isinstance(raw_name, str) or not raw_name.strip():
        raise CacheFormatError(f"summary record {index} has an invalid commodity")
    commodity = raw_name.strip()
    # Store and load canonical spellings when the commodity is recognized.
    commodity = canonical_commodity_name(commodity) or commodity

    numeric_values = {}
    for field_name in ("average_sell", "maximum_sell", "total_demand"):
        value = record[field_name]
        if value is not None and type(value) is not int:
            raise CacheFormatError(
                f"summary record {index} has an invalid {field_name}"
            )
        numeric_values[field_name] = value

    return CommodityPriceSummary(commodity=commodity, **numeric_values)


def _encode_result(
    result: CommoditySummaryResult,
    stored_at: datetime | None,
) -> dict[str, Any]:
    """Encode a complete issue-free summary result as the current schema.

    The output includes the schema version, an aware UTC ISO timestamp, and
    canonicalized summary records. Invalid or ambiguous cache data is rejected
    before any file is written.
    """
    if not isinstance(result, CommoditySummaryResult):
        raise TypeError("result must be a CommoditySummaryResult")
    if result.issues:
        raise ValueError("cannot cache a result that contains issues")
    if not result.summaries:
        raise ValueError("cannot save a cache with no summaries")

    timestamp = (
        # Persist generated timestamps as aware UTC datetimes.
        datetime.now(timezone.utc)
        if stored_at is None
        else _aware_utc(stored_at, "stored_at")
    )
    records = []
    seen_keys = set()
    for index, summary in enumerate(result.summaries):
        if not isinstance(summary, CommodityPriceSummary):
            raise ValueError(f"summary {index} is not a CommodityPriceSummary")
        if not isinstance(summary.commodity, str) or not summary.commodity.strip():
            raise ValueError(f"summary {index} has an invalid commodity")

        commodity = summary.commodity.strip()
        commodity = canonical_commodity_name(commodity) or commodity
        key = _commodity_key(commodity)
        # Duplicate identities would make later cache reads order-dependent.
        if key in seen_keys:
            raise ValueError(f"duplicate commodity identity: {commodity!r}")
        seen_keys.add(key)

        values = {}
        for field_name in ("average_sell", "maximum_sell", "total_demand"):
            value = getattr(summary, field_name)
            if value is not None and type(value) is not int:
                raise ValueError(f"summary {index} has an invalid {field_name}")
            values[field_name] = value
        records.append({"commodity": commodity, **values})

    return {
        "version": _SCHEMA_VERSION,
        "stored_at": timestamp.isoformat(),
        "summaries": records,
    }
