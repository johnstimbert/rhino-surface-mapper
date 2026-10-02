namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// One accumulated radar coverage disc, ported from the <c>radar_coverage</c> entries in a map
/// document. Discs are produced by the radar pulse engine (Phase 8) and merely stored/validated
/// in Phase 1.
/// </summary>
/// <remarks>
/// Identified positionally within <see cref="MapSession.RadarCoverage"/>, like
/// <see cref="TrailPoint"/>; it carries no <see cref="System.Guid"/> because the file format
/// never serialises one for coverage discs either.
/// </remarks>
/// <param name="X">Local easting in metres relative to the map centre.</param>
/// <param name="Y">Local northing in metres relative to the map centre.</param>
/// <param name="Radius">Disc radius in metres, validated to the inclusive 0–5000 range.</param>
public sealed record RadarCoverageDisc(double X, double Y, double Radius);
