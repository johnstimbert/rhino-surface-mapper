namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// A PML's (Planetary Mapping Location's) persisted identity: the free-text identifier players
/// assign (or the auto-allocated <c>JD</c><i>n</i> "John Doe" identifier) plus the geographic
/// centre used to rediscover the same PML on return. Ported from the <c>pml_id</c>,
/// <c>pml_center_lat</c> and <c>pml_center_lon</c> fields of a map document.
/// </summary>
/// <remarks>
/// <see cref="PmlId"/> is a business identifier chosen by the player or allocated by
/// <c>PmlRules.NextJohnDoeId</c> — it is unrelated to the runtime-only <see cref="System.Guid"/>
/// identities used by <see cref="Deposit"/>, <see cref="Rig"/> and <see cref="MapMark"/>.
/// The centre is deliberately distinct from <see cref="MapSession.CenterLat"/>/
/// <see cref="MapSession.CenterLon"/>: the map centre is a technical local-projection anchor,
/// while the PML centre is the stable geographic point <c>PmlRules.CorrespondsToMap</c> matches
/// against.
/// </remarks>
/// <param name="PmlId">Free-text PML identifier, or <see cref="string.Empty"/> until assigned.</param>
/// <param name="CenterLat">PML centre latitude in degrees, or <see langword="null"/> until known.</param>
/// <param name="CenterLon">PML centre longitude in degrees, or <see langword="null"/> until known.</param>
public sealed record PmlIdentity(string PmlId, double? CenterLat, double? CenterLon)
{
    /// <summary>A PML identity with no identifier and no known centre, matching a brand-new map.</summary>
    public static readonly PmlIdentity Empty = new(string.Empty, null, null);
}
