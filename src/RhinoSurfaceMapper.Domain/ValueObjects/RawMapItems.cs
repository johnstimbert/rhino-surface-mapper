namespace RhinoSurfaceMapper.Domain.ValueObjects;

/// <summary>
/// Common shape for a persisted marker's position fields before validation, ported from the
/// per-item loop in <c>MapperState.validate_map</c> that coerces <c>x</c>/<c>y</c>/<c>lat</c>/
/// <c>lon</c> with its local <c>number()</c> helper. Used directly for <c>rigs</c>; the other
/// marker kinds add their own fields (name, size, rig count, status).
/// </summary>
/// <remarks>
/// <see cref="X"/>/<see cref="Y"/>/<see cref="Lat"/>/<see cref="Lon"/> are boxed as
/// <see cref="object"/>? — not <see cref="double"/>? — because Python's per-item loop performs
/// its own lenient-but-strict numeric coercion at validation time (accepting an int, a float, or
/// a numeric string, but rejecting a genuinely non-numeric value such as the string
/// <c>"abc"</c>); boxing preserves the exact raw value so <c>MapValidator</c> can reproduce that
/// coercion instead of relying on a coercion Infrastructure may have already performed
/// differently.
/// </remarks>
public record RawPositionDocument
{
    /// <summary>Raw local easting value in metres, not yet coerced to <see cref="double"/>.</summary>
    public object? X { get; init; }

    /// <summary>Raw local northing value in metres, not yet coerced to <see cref="double"/>.</summary>
    public object? Y { get; init; }

    /// <summary>
    /// Raw geographic latitude in degrees, or <see langword="null"/> when the legacy record
    /// omitted it — <c>MapValidator</c> then reconstructs it from <see cref="X"/>/<see cref="Y"/>
    /// via <c>PlanetGeometry.GeographicFromLocal</c>, matching Python's <c>xyll</c> fallback.
    /// </summary>
    public object? Lat { get; init; }

    /// <summary>Raw geographic longitude in degrees, with the same fallback rule as <see cref="Lat"/>.</summary>
    public object? Lon { get; init; }
}

/// <summary>
/// A persisted trail point before validation, adding the fields <see cref="RawPositionDocument"/>
/// does not carry. Ported from one <c>points</c> entry.
/// </summary>
public sealed record RawTrailPointDocument : RawPositionDocument
{
    /// <summary>
    /// Sample timestamp as Unix epoch seconds, or <see langword="null"/> for a legacy record that
    /// predates timestamped trail points. Not range/type-validated by <c>validate_map</c> in
    /// Python (only <c>x</c>/<c>y</c> are coerced for trail points); <c>MapValidator</c> defaults
    /// a missing value to <c>0.0</c> rather than inventing a current timestamp.
    /// </summary>
    public double? T { get; init; }

    /// <summary>Whether this point starts a new trail segment (a teleport/stale-sample break).</summary>
    public bool BreakBefore { get; init; }
}

/// <summary>A persisted mining deposit before validation. Ported from one <c>deposits</c> entry.</summary>
public sealed record RawDepositDocument : RawPositionDocument
{
    /// <summary>Deposit label. Read default: <see cref="string.Empty"/>, matching Python's <c>deposit.get('name', '')</c>.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// One of the eight recognised size literals: the four current English literals
    /// (<c>"Small"</c>, <c>"Medium"</c>, <c>"Large"</c>, <c>"Huge"</c>) or, for backward
    /// compatibility with maps saved before decision D7's migration, the four legacy Portuguese
    /// literals (<c>"Pequeno"</c>, <c>"Médio"</c>, <c>"Grande"</c>, <c>"Enorme"</c>) — see
    /// <c>Domain.Services.DepositSizeCodec</c>. Read default: <c>"Pequeno"</c>, matching Python's
    /// <c>deposit.setdefault("size", "Pequeno")</c> legacy-record fallback.
    /// </summary>
    public string? Size { get; init; } = "Pequeno";

    /// <summary>
    /// Raw rig count, not yet coerced to <see cref="int"/>. Boxed as <see cref="object"/>? for the
    /// same reason as <see cref="RawPositionDocument"/>'s coordinates: a non-numeric value (for
    /// example the string <c>"abc"</c>) must be rejected by <c>MapValidator</c>, and a numeric but
    /// non-integral value (for example <c>3.5</c>) must also be rejected, while an integral float
    /// (<c>3.0</c>) is accepted and becomes the integer <c>3</c> — exactly Python's
    /// <c>count.is_integer()</c> rule. Read default: <c>1</c>, matching
    /// <c>deposit.setdefault("rigs", 1)</c>.
    /// </summary>
    public object? Rigs { get; init; } = 1;
}

/// <summary>A persisted named marker before validation. Ported from one <c>marks</c> entry.</summary>
public sealed record RawMarkDocument : RawPositionDocument
{
    /// <summary>Marker name; must be a non-blank string once validated.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// A persisted search-route history entry before validation. Ported from one
/// <c>route_history</c> entry.
/// </summary>
public sealed record RawRouteHistoryDocument
{
    /// <summary>
    /// Raw one-based route point number, not yet coerced. Boxed as <see cref="object"/>? for the
    /// same integral-value rule as <see cref="RawDepositDocument.Rigs"/>.
    /// </summary>
    public object? Number { get; init; }

    /// <summary>Raw local easting value in metres of the route point, not yet coerced.</summary>
    public object? X { get; init; }

    /// <summary>Raw local northing value in metres of the route point, not yet coerced.</summary>
    public object? Y { get; init; }

    /// <summary>Must be exactly <c>"reached"</c> or <c>"skipped"</c> (case-sensitive, matching Python).</summary>
    public string? Status { get; init; }
}

/// <summary>A persisted radar coverage disc before validation. Ported from one <c>radar_coverage</c> entry.</summary>
public sealed record RawRadarPulseDocument
{
    /// <summary>Raw local easting value in metres, not yet coerced.</summary>
    public object? X { get; init; }

    /// <summary>Raw local northing value in metres, not yet coerced.</summary>
    public object? Y { get; init; }

    /// <summary>Raw disc radius in metres, not yet coerced; validated to the inclusive 0–5000 range.</summary>
    public object? Radius { get; init; }
}
