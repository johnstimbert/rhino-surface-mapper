namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// A user-placed named marker, ported from the <c>marks</c> entries in a map document. Marks are
/// the only marker kind that requires a non-empty name (<c>validate_map</c>'s
/// "Invalid mark name" rule).
/// </summary>
/// <remarks>
/// <see cref="Id"/> is a runtime-only <see cref="Guid"/> used for hit-testing and edit/delete
/// addressing; it is never serialised. See <see cref="Deposit"/>'s remarks for the full
/// rationale, which applies identically here. Legacy PML "Centro [id]" markers are also stored
/// as <see cref="MapMark"/> entries, and are recovered by <c>PmlRules.InferLegacyPml</c> before a
/// legacy map is validated.
/// </remarks>
/// <param name="Id">Runtime-only identity.</param>
/// <param name="Name">Non-empty, whitespace-trimmed-non-blank marker name.</param>
/// <param name="X">Local easting in metres relative to the map centre.</param>
/// <param name="Y">Local northing in metres relative to the map centre.</param>
/// <param name="Lat">Geographic latitude in degrees.</param>
/// <param name="Lon">Geographic longitude in degrees.</param>
public sealed record MapMark(Guid Id, string Name, double X, double Y, double Lat, double Lon);
