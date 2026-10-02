namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// One recorded SRV trail sample, ported from the <c>points</c> entries
/// <c>MapperState.process_status</c> appends. Coordinates are in the local metre projection
/// anchored to the owning <see cref="MapSession"/>'s centre (see <c>PlanetGeometry</c>).
/// </summary>
/// <remarks>
/// Trail points are identified positionally within <see cref="MapSession.Points"/>, exactly as
/// the Python JSON array is; they intentionally carry no <see cref="System.Guid"/> identity
/// because the file format never serialises one (see the "Entities and the aggregate" note in
/// the design: "the Python map format identifies markers positionally").
/// </remarks>
/// <param name="X">Local easting in metres relative to the map centre.</param>
/// <param name="Y">Local northing in metres relative to the map centre.</param>
/// <param name="Lat">Geographic latitude in degrees, kept alongside <paramref name="X"/>/<paramref name="Y"/> so legacy readers never need the projection centre to render a point.</param>
/// <param name="Lon">Geographic longitude in degrees.</param>
/// <param name="T">
/// Sample timestamp as Unix epoch seconds (<c>status.get("timestamp", time.time())</c> in
/// Python). Stored as <see cref="double"/>, not a calendar type, because it is a raw telemetry
/// timestamp, not a map metadata date.
/// </param>
/// <param name="BreakBefore">
/// <see langword="true"/> when the gap from the previous trail point exceeded
/// <see cref="Constants.MapperConstants.TrailBreakDistanceMetres"/>, so renderers must not draw
/// a continuous line from the previous point to this one (a teleport or a stale sample).
/// </param>
public sealed record TrailPoint(double X, double Y, double Lat, double Lon, double T, bool BreakBefore = false);
