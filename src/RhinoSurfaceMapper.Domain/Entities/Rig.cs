namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// A placed drilling rig, ported from the <c>rigs</c> entries in a map document.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is a runtime-only <see cref="Guid"/> used for hit-testing and edit/delete
/// addressing; it is never serialised. See <see cref="Deposit"/>'s remarks for the full
/// rationale, which applies identically here.
/// </remarks>
/// <param name="Id">Runtime-only identity.</param>
/// <param name="X">Local easting in metres relative to the map centre.</param>
/// <param name="Y">Local northing in metres relative to the map centre.</param>
/// <param name="Lat">Geographic latitude in degrees.</param>
/// <param name="Lon">Geographic longitude in degrees.</param>
public sealed record Rig(Guid Id, double X, double Y, double Lat, double Lon);
