using System.Text.Json.Serialization;
using RhinoSurfaceMapper.Domain.Constants;

namespace RhinoSurfaceMapper.Infrastructure.Persistence;

/// <summary>
/// The exact on-disk shape of a map document: one property per persisted key, declared in the
/// fixed key order the design's "Data model and file compatibility" section specifies, so a
/// freshly-created map always writes that order regardless of field-assignment order elsewhere.
/// </summary>
/// <remarks>
/// Deliberately independent of <see cref="Domain.ValueObjects.RawMapDocument"/>: this type is
/// the Infrastructure JSON wire format (explicit <see cref="JsonPropertyNameAttribute"/> per
/// member, matching the abbreviated/underscored persisted key names); <c>RawMapDocument</c> is
/// the Domain's pre-validation shape (PascalCase CLR members, some deliberately boxed as
/// <see cref="object"/>?). <see cref="MapDocumentMapper"/> converts between the two. See the
/// design: "Reads/writes through DTO records ... so the on-disk shape is explicit and
/// independent of the domain model."
/// </remarks>
internal sealed class MapDocumentDto
{
    /// <summary>Star system name. Read default: <see cref="string.Empty"/>.</summary>
    [JsonPropertyName("system")]
    public string System { get; set; } = string.Empty;

    /// <summary>Body name. Read default: <see cref="string.Empty"/>.</summary>
    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    /// <summary>ISO-8601 creation timestamp, or <see langword="null"/>.</summary>
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    /// <summary>ISO-8601 last-saved timestamp, or <see langword="null"/>.</summary>
    [JsonPropertyName("last_saved_at")]
    public string? LastSavedAt { get; set; }

    /// <summary>Library favourite flag. Must be a real JSON boolean — see the design's "Exact-boolean semantics" note.</summary>
    [JsonPropertyName("favorite")]
    public bool Favorite { get; set; }

    /// <summary>Library protected (read-only) flag. Must be a real JSON boolean.</summary>
    [JsonPropertyName("protected")]
    public bool Protected { get; set; }

    /// <summary>PML identifier. Read default: <see cref="string.Empty"/>.</summary>
    [JsonPropertyName("pml_id")]
    public string PmlId { get; set; } = string.Empty;

    /// <summary>PML centre latitude in degrees, paired with <see cref="PmlCenterLon"/>.</summary>
    [JsonPropertyName("pml_center_lat")]
    public double? PmlCenterLat { get; set; }

    /// <summary>PML centre longitude in degrees, paired with <see cref="PmlCenterLat"/>.</summary>
    [JsonPropertyName("pml_center_lon")]
    public double? PmlCenterLon { get; set; }

    /// <summary>
    /// Local-projection map centre latitude in degrees. Required: Python's loader reads
    /// <c>data["center_lat"]</c> with no default and propagates a <c>KeyError</c> when absent;
    /// marking this member <see langword="required"/> reproduces that failure as a
    /// <see cref="System.Text.Json.JsonException"/> instead.
    /// </summary>
    [JsonPropertyName("center_lat")]
    public required double CenterLat { get; set; }

    /// <summary>Local-projection map centre longitude in degrees. Required; see <see cref="CenterLat"/>.</summary>
    [JsonPropertyName("center_lon")]
    public required double CenterLon { get; set; }

    /// <summary>Planet radius in metres. Read default: <see cref="MapperConstants.DefaultRadiusMetres"/>.</summary>
    [JsonPropertyName("planet_radius")]
    public double PlanetRadius { get; set; } = MapperConstants.DefaultRadiusMetres;

    /// <summary>
    /// Radar coverage disc draw width in metres. Read default <c>500.0</c> — deliberately
    /// different from the <c>2000.0</c> value a new map writes; see
    /// <see cref="Domain.ValueObjects.RawMapDocument.CoverageWidthMetres"/>'s remarks for why
    /// this asymmetry is intentional, not a bug.
    /// </summary>
    [JsonPropertyName("coverage_width_m")]
    public double CoverageWidthMetres { get; set; } = 500.0;

    /// <summary>Scanner range in metres. Read/write default: <c>2000.0</c>.</summary>
    [JsonPropertyName("scanner_range_m")]
    public double ScannerRangeMetres { get; set; } = 2_000.0;

    /// <summary>Whether a circular search route is active. Read default: <see langword="false"/>.</summary>
    [JsonPropertyName("search_started")]
    public bool SearchStarted { get; set; }

    /// <summary>Search Datum latitude in degrees, paired with <see cref="DatumLon"/>.</summary>
    [JsonPropertyName("datum_lat")]
    public double? DatumLat { get; set; }

    /// <summary>
    /// Search route starting bearing in degrees, boxed as <see cref="object"/>? via
    /// <see cref="RawValueJsonConverter"/> so <c>MapValidator</c> can reject a non-integer value
    /// (a <see cref="bool"/>, a non-integral number, or a string) exactly as Python's
    /// <c>type(x) is not int</c> check does. Read default: boxed <c>0</c>.
    /// </summary>
    [JsonPropertyName("search_azimuth")]
    public object? SearchAzimuth { get; set; } = 0;

    /// <summary>Search Datum longitude in degrees, paired with <see cref="DatumLat"/>.</summary>
    [JsonPropertyName("datum_lon")]
    public double? DatumLon { get; set; }

    /// <summary>Zero-based index of the next unreached search route point. Read default: <c>0</c>.</summary>
    [JsonPropertyName("route_index")]
    public int RouteIndex { get; set; }

    /// <summary>Completed search route point history.</summary>
    [JsonPropertyName("route_history")]
    public List<RouteHistoryDto> RouteHistory { get; set; } = [];

    /// <summary>Recorded SRV trail samples, in recording order.</summary>
    [JsonPropertyName("points")]
    public List<TrailPointDto> Points { get; set; } = [];

    /// <summary>Accumulated radar coverage discs.</summary>
    [JsonPropertyName("radar_coverage")]
    public List<RadarPulseDto> RadarCoverage { get; set; } = [];

    /// <summary>Recorded mining deposits.</summary>
    [JsonPropertyName("deposits")]
    public List<DepositDto> Deposits { get; set; } = [];

    /// <summary>Placed rigs.</summary>
    [JsonPropertyName("rigs")]
    public List<PositionDto> Rigs { get; set; } = [];

    /// <summary>User-placed named marks.</summary>
    [JsonPropertyName("marks")]
    public List<MarkDto> Marks { get; set; } = [];
}

/// <summary>
/// Common persisted position fields shared by every marker-like entry. Used directly for a
/// <c>rigs</c> entry; the other marker kinds add their own fields.
/// </summary>
/// <remarks>
/// <see cref="X"/>/<see cref="Y"/>/<see cref="Lat"/>/<see cref="Lon"/> are boxed as
/// <see cref="object"/>? for the same reason as <see cref="Domain.ValueObjects.RawPositionDocument"/>:
/// a hand-edited file could carry a numeric string or a non-numeric value, and only
/// <c>MapValidator</c>'s coercion (<c>NumberCoercion.ToFiniteDouble</c>) decides whether that is
/// acceptable — this DTO must not pre-empt that decision by coercing or rejecting eagerly.
/// </remarks>
internal class PositionDto
{
    /// <summary>Raw local easting value in metres, not yet coerced.</summary>
    [JsonPropertyName("x")]
    public object? X { get; set; }

    /// <summary>Raw local northing value in metres, not yet coerced.</summary>
    [JsonPropertyName("y")]
    public object? Y { get; set; }

    /// <summary>Raw geographic latitude in degrees, or absent for a legacy record.</summary>
    [JsonPropertyName("lat")]
    public object? Lat { get; set; }

    /// <summary>Raw geographic longitude in degrees, with the same fallback rule as <see cref="Lat"/>.</summary>
    [JsonPropertyName("lon")]
    public object? Lon { get; set; }
}

/// <summary>A persisted trail point. Ported from one <c>points</c> entry.</summary>
internal sealed class TrailPointDto : PositionDto
{
    /// <summary>Sample timestamp as Unix epoch seconds, or <see langword="null"/> for a legacy record.</summary>
    [JsonPropertyName("t")]
    public double? T { get; set; }

    /// <summary>
    /// Whether this point starts a new trail segment. Omitted from the written JSON when
    /// <see langword="false"/> (<see cref="JsonIgnoreCondition.WhenWritingDefault"/>), exactly
    /// matching Python's <c>process_status</c>, which only ever adds the <c>break_before</c> key
    /// to a point's dict when the break actually occurred.
    /// </summary>
    [JsonPropertyName("break_before")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool BreakBefore { get; set; }
}

/// <summary>A persisted mining deposit. Ported from one <c>deposits</c> entry.</summary>
internal sealed class DepositDto : PositionDto
{
    /// <summary>Deposit label. Read default: <see cref="string.Empty"/>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; } = string.Empty;

    /// <summary>
    /// One of the eight recognised size literals (four current English, four legacy
    /// Portuguese) — see <c>Domain.Services.DepositSizeCodec</c>. Read default:
    /// <c>"Pequeno"</c>, matching Python's <c>deposit.setdefault("size", "Pequeno")</c>.
    /// </summary>
    [JsonPropertyName("size")]
    public string? Size { get; set; } = "Pequeno";

    /// <summary>
    /// Raw rig count, boxed as <see cref="object"/>? for the same reason as
    /// <see cref="PositionDto.X"/>. Read default: boxed <c>1</c>.
    /// </summary>
    [JsonPropertyName("rigs")]
    public object? Rigs { get; set; } = 1;
}

/// <summary>A persisted named marker. Ported from one <c>marks</c> entry.</summary>
internal sealed class MarkDto : PositionDto
{
    /// <summary>Marker name; must be a non-blank string once validated.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

/// <summary>A persisted search-route history entry. Ported from one <c>route_history</c> entry.</summary>
internal sealed class RouteHistoryDto
{
    /// <summary>Raw one-based route point number, boxed as <see cref="object"/>? for the same reason as <see cref="DepositDto.Rigs"/>.</summary>
    [JsonPropertyName("number")]
    public object? Number { get; set; }

    /// <summary>Raw local easting value in metres of the route point, not yet coerced.</summary>
    [JsonPropertyName("x")]
    public object? X { get; set; }

    /// <summary>Raw local northing value in metres of the route point, not yet coerced.</summary>
    [JsonPropertyName("y")]
    public object? Y { get; set; }

    /// <summary>Must be exactly <c>"reached"</c> or <c>"skipped"</c> (case-sensitive).</summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

/// <summary>A persisted radar coverage disc. Ported from one <c>radar_coverage</c> entry.</summary>
internal sealed class RadarPulseDto
{
    /// <summary>Raw local easting value in metres, not yet coerced.</summary>
    [JsonPropertyName("x")]
    public object? X { get; set; }

    /// <summary>Raw local northing value in metres, not yet coerced.</summary>
    [JsonPropertyName("y")]
    public object? Y { get; set; }

    /// <summary>Raw disc radius in metres, not yet coerced; validated to the inclusive 0–5000 range.</summary>
    [JsonPropertyName("radius")]
    public object? Radius { get; set; }
}
