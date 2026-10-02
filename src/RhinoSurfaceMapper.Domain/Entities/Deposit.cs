using RhinoSurfaceMapper.Domain.Enums;

namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// A recorded mining deposit, ported from the <c>deposits</c> entries in a map document.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is a runtime-only <see cref="Guid"/> generated with
/// <see cref="Guid.NewGuid()"/> when the deposit is created or loaded; it exists solely for
/// component keys, hit-testing and edit/delete addressing in later phases and is never
/// serialised — the Python map format identifies deposits positionally within the
/// <c>deposits</c> JSON array, not by any stored identifier.
/// </remarks>
/// <param name="Id">Runtime-only identity; see the remarks above.</param>
/// <param name="Name">Free-text deposit label. May be empty, matching Python's <c>deposit.get('name', '')</c> default.</param>
/// <param name="Size">Deposit size tier; persisted as the current English literal, with the legacy Portuguese literal still accepted on read (see <see cref="DepositSize"/>).</param>
/// <param name="Rigs">Rig count for this deposit, validated to the inclusive 1–6 range.</param>
/// <param name="X">Local easting in metres relative to the map centre.</param>
/// <param name="Y">Local northing in metres relative to the map centre.</param>
/// <param name="Lat">Geographic latitude in degrees.</param>
/// <param name="Lon">Geographic longitude in degrees.</param>
public sealed record Deposit(Guid Id, string Name, DepositSize Size, int Rigs, double X, double Y, double Lat, double Lon);
