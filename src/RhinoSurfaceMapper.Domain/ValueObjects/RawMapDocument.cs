using RhinoSurfaceMapper.Domain.Constants;

namespace RhinoSurfaceMapper.Domain.ValueObjects;

/// <summary>
/// The persisted map document shape, before <c>MapValidator</c> normalises it into a
/// <see cref="Entities.MapSession"/>. Ported from the dictionary <c>MapperState.to_dict</c>
/// writes and <c>MapperState._load_file</c>/<c>validate_map</c> reads.
/// </summary>
/// <remarks>
/// <para>
/// Fields whose raw JSON value can carry a type <c>MapValidator</c> must reject (notably
/// <see cref="SearchAzimuth"/>, which must be an exact integer and not a <see cref="bool"/>,
/// <see cref="double"/> or <see cref="string"/>, and each marker's numeric fields, which Python
/// coerces with a lenient <c>float()</c> call that still rejects non-numeric strings) are typed
/// <see cref="object"/>? so the exact runtime type survives from parsing through to validation,
/// exactly as it does in Python's dynamically-typed <c>dict</c>. Fields Python's loader already
/// coerces to a concrete type before <c>validate_map</c> ever runs (for example <c>center_lat</c>,
/// coerced with <c>float(data["center_lat"])</c> in <c>_load_file</c>) are typed concretely here
/// too — only finiteness/range remain to be checked.
/// </para>
/// <para>
/// This same type is also what <see cref="Entities.MapSession.ToDocument"/> produces: the
/// Infrastructure JSON writer (Phase 2) serialises it directly, and the JSON reader deserialises
/// into it before calling <see cref="Entities.MapSession.LoadFromDocument"/>, giving one shape for
/// both directions exactly as Python's single <c>to_dict</c>/<c>_load_file</c> pair does.
/// </para>
/// </remarks>
public sealed record RawMapDocument
{
    /// <summary>Star system name. Read default: <see cref="string.Empty"/>.</summary>
    public string System { get; init; } = string.Empty;

    /// <summary>Body name. Read default: <see cref="string.Empty"/>.</summary>
    public string Body { get; init; } = string.Empty;

    /// <summary>ISO-8601 creation timestamp, or <see langword="null"/> for a map never stamped.</summary>
    public string? CreatedAt { get; init; }

    /// <summary>ISO-8601 last-saved timestamp, or <see langword="null"/> for a map never saved with a stamp.</summary>
    public string? LastSavedAt { get; init; }

    /// <summary>Library favourite flag. Read default: <see langword="false"/>.</summary>
    public bool Favorite { get; init; }

    /// <summary>Library protected (read-only) flag. Read default: <see langword="false"/>.</summary>
    public bool Protected { get; init; }

    /// <summary>PML identifier. Read default: <see cref="string.Empty"/>.</summary>
    public string PmlId { get; init; } = string.Empty;

    /// <summary>PML centre latitude in degrees, paired with <see cref="PmlCenterLon"/>.</summary>
    public double? PmlCenterLat { get; init; }

    /// <summary>PML centre longitude in degrees, paired with <see cref="PmlCenterLat"/>.</summary>
    public double? PmlCenterLon { get; init; }

    /// <summary>Local-projection map centre latitude in degrees. Required; no read default.</summary>
    public double CenterLat { get; init; }

    /// <summary>Local-projection map centre longitude in degrees. Required; no read default.</summary>
    public double CenterLon { get; init; }

    /// <summary>Planet radius in metres. Read default: <see cref="MapperConstants.DefaultRadiusMetres"/>.</summary>
    public double PlanetRadius { get; init; } = MapperConstants.DefaultRadiusMetres;

    /// <summary>
    /// Radar coverage disc draw width in metres. Read default <c>500.0</c>, which intentionally
    /// differs from the <c>2000.0</c> value written for a new map (design: "Data model and file
    /// compatibility" — the asymmetric read/write default is deliberate, not a bug).
    /// </summary>
    public double CoverageWidthMetres { get; init; } = 500.0;

    /// <summary>Scanner range in metres. Read/write default: <c>2000.0</c>.</summary>
    public double ScannerRangeMetres { get; init; } = 2_000.0;

    /// <summary>Whether a circular search route is active. Read default: <see langword="false"/>.</summary>
    public bool SearchStarted { get; init; }

    /// <summary>Search Datum latitude in degrees, paired with <see cref="DatumLon"/>.</summary>
    public double? DatumLat { get; init; }

    /// <summary>Search Datum longitude in degrees, paired with <see cref="DatumLat"/>.</summary>
    public double? DatumLon { get; init; }

    /// <summary>
    /// Search route starting bearing in degrees. Boxed as <see cref="object"/> because
    /// <c>MapValidator</c> must reject any value that is not an exact <see cref="int"/>
    /// (including a <see cref="bool"/>, a non-integral <see cref="double"/>, or a
    /// <see cref="string"/>) — Python's loader passes the raw JSON value through unchanged, so
    /// only <c>validate_map</c>'s <c>type(x) is not int</c> check enforces this. Read default: <c>0</c>.
    /// </summary>
    public object? SearchAzimuth { get; init; } = 0;

    /// <summary>Zero-based index of the next unreached search route point. Read default: <c>0</c>.</summary>
    public int RouteIndex { get; init; }

    /// <summary>Recorded SRV trail samples, in recording order.</summary>
    public IReadOnlyList<RawTrailPointDocument> Points { get; init; } = Array.Empty<RawTrailPointDocument>();

    /// <summary>Recorded mining deposits.</summary>
    public IReadOnlyList<RawDepositDocument> Deposits { get; init; } = Array.Empty<RawDepositDocument>();

    /// <summary>Placed rigs.</summary>
    public IReadOnlyList<RawPositionDocument> Rigs { get; init; } = Array.Empty<RawPositionDocument>();

    /// <summary>User-placed named marks.</summary>
    public IReadOnlyList<RawMarkDocument> Marks { get; init; } = Array.Empty<RawMarkDocument>();

    /// <summary>Completed search route point history.</summary>
    public IReadOnlyList<RawRouteHistoryDocument> RouteHistory { get; init; } = Array.Empty<RawRouteHistoryDocument>();

    /// <summary>Accumulated radar coverage discs.</summary>
    public IReadOnlyList<RawRadarPulseDocument> RadarCoverage { get; init; } = Array.Empty<RawRadarPulseDocument>();
}
