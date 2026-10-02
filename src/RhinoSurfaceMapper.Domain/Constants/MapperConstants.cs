namespace RhinoSurfaceMapper.Domain.Constants;

/// <summary>
/// Behaviour-preserving numeric and bitmask constants ported verbatim from
/// <c>mapper_core.py</c>. Every value here gates or shapes a specific rule the Python test
/// suite depends on; nothing here is tunable without re-validating that suite (design:
/// "Code documentation standard" — "preserved constants must document their origin").
/// </summary>
public static class MapperConstants
{
    /// <summary>
    /// Bit 26 (<c>0x04000000</c>) of Elite Dangerous' <c>Status.json</c> flags field. Set only
    /// while the commander is driving the SRV/Rhino; <c>TelemetryProcessor</c> rejects every
    /// sample where this bit is clear, exactly as <c>MapperState.process_status</c> does.
    /// </summary>
    public const int SrvFlag = 0x04000000;

    /// <summary>
    /// Bit 19 (<c>0x00080000</c>) of the same flags field, set when the SRV fuel reservoir is
    /// low. Combined with the fuel reservoir reading to populate <c>FuelLow</c>.
    /// </summary>
    public const int FuelLowFlag = 0x00080000;

    /// <summary>
    /// Fallback planet radius in metres (Earth's mean radius) used whenever telemetry omits
    /// <c>PlanetRadius</c>, matching Python's <c>DEFAULT_RADIUS_M</c>. Also the haversine radius
    /// used wherever no body-specific radius is otherwise available.
    /// </summary>
    public const double DefaultRadiusMetres = 6_371_000.0;

    /// <summary>
    /// Radius, in metres, of the circular search route around the search Datum. Combined with
    /// <see cref="SearchSpacingMetres"/> this yields the historical 13-point route
    /// (<c>ceil(2·π·3500 / 1800) = 13</c>); changing either constant changes the point count.
    /// </summary>
    public const double SearchRadiusMetres = 3_500.0;

    /// <summary>
    /// Target great-circle spacing, in metres, between adjacent search route points. The actual
    /// route point count is derived (never hard-coded) from this value and
    /// <see cref="SearchRadiusMetres"/> by <c>SearchRouteCalculator.SearchTotalPoints</c>.
    /// </summary>
    public const double SearchSpacingMetres = 1_800.0;

    /// <summary>
    /// Minimum straight-line distance, in metres, between two consecutive trail samples. Closer
    /// samples are dropped to bound file size and rendering noise, matching the Python
    /// <c>process_status</c> trail-recording rule.
    /// </summary>
    public const double TrailMinimumSpacingMetres = 10.0;

    /// <summary>
    /// Distance, in metres, above which a new trail sample is flagged with
    /// <c>break_before</c> so renderers do not draw a continuous line across a teleport or a
    /// stale sample. Matches Python's unnamed <c>100.0</c> literal in <c>process_status</c>.
    /// </summary>
    public const double TrailBreakDistanceMetres = 100.0;

    /// <summary>
    /// Arrival radius, in metres, for a circular search route point. Matches the <c>100.0</c>
    /// literal in <c>MapperState.update_next</c>.
    /// </summary>
    public const double RouteArrivalMetres = 100.0;

    /// <summary>
    /// Arrival radius, in metres, for marker/pause-point navigation in the overlay. Matches the
    /// <c>100.0</c> literal in <c>MapperState.overlay_navigation</c>.
    /// </summary>
    public const double NavigationArrivalMetres = 100.0;

    /// <summary>
    /// Distance, in metres, inside which the overlay distance text starts blinking. Matches the
    /// <c>300.0</c> literal in <c>MapperState.overlay_navigation</c>.
    /// </summary>
    public const double OverlayBlinkDistanceMetres = 300.0;

    /// <summary>
    /// Blink half-period, in seconds, for the overlay distance text once inside
    /// <see cref="OverlayBlinkDistanceMetres"/>. Matches the <c>0.45</c> literal in
    /// <c>MapperState.overlay_navigation</c>; timed from <c>IClock.MonotonicSeconds</c>, never
    /// a wall-clock sleep.
    /// </summary>
    public const double OverlayBlinkIntervalSeconds = 0.45;

    /// <summary>
    /// Minimum separation, in metres, intended between distinct mining deposits. Reserved for the
    /// map-operations feature (Phase 4) that enforces it when placing a new deposit; recorded
    /// here now so the value is fixed by this phase and not re-derived later.
    /// </summary>
    public const double DepositMinimumSeparationMetres = 80.0;

    /// <summary>
    /// Inclusive great-circle distance, in metres, within which a position is considered to
    /// belong to a PML's geographic centre. Matches Python's <c>PML_MATCH_DISTANCE_M</c>; the
    /// boundary itself still matches (<c>distance &lt;= PmlMatchDistanceMetres</c>), not only
    /// distances strictly inside it.
    /// </summary>
    public const int PmlMatchDistanceMetres = 13_000;
}
