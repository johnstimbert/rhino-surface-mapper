using RhinoSurfaceMapper.Domain.Enums;

namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// One completed circular-search route point, ported from the <c>route_history</c> entries
/// <c>MapperState.update_next</c>/<c>skip_next</c> append.
/// </summary>
/// <remarks>
/// Identified positionally within <see cref="MapSession.RouteHistory"/>, like
/// <see cref="TrailPoint"/>; it carries no <see cref="System.Guid"/> because the file format
/// never serialises one.
/// </remarks>
/// <param name="Number">
/// One-based route point number (<c>route_index + 1</c> at the time it was reached/skipped),
/// validated to the inclusive 1..<c>SearchRouteCalculator.SearchTotalPoints</c> range.
/// </param>
/// <param name="X">Local easting in metres of the route point, relative to the map centre.</param>
/// <param name="Y">Local northing in metres of the route point, relative to the map centre.</param>
/// <param name="Status">Whether the point was reached by arrival or explicitly skipped.</param>
public sealed record RouteHistoryEntry(int Number, double X, double Y, RouteStatus Status);
