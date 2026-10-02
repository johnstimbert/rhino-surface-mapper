namespace RhinoSurfaceMapper.Domain.ValueObjects;

/// <summary>
/// The minimal, loosely-typed surface <c>PmlRules</c> needs to match and recover PML identity
/// from a candidate map file, ported from the duck-typed <c>state: Any</c> parameter Python's
/// <c>map_pml.py</c> functions accept (tests pass a bare <c>SimpleNamespace</c>).
/// </summary>
/// <remarks>
/// Deliberately independent of <see cref="Entities.MapSession"/>/<see cref="RawMapDocument"/>:
/// candidate scanning (<c>PmlRules.MatchingCandidates</c>, <c>NextJohnDoeId</c>) must tolerate
/// legacy files that cannot yet be fully validated as a map (for example, legacy "Centro [id]"
/// markers recorded without <c>x</c>/<c>y</c>), so this type carries only the fields PML
/// matching and recovery actually read or write.
/// </remarks>
public sealed class PmlCandidate
{
    /// <summary>Star system name, mutated in place by <c>PmlRules.InferLegacyPml</c> when recovered from a legacy path.</summary>
    public required string System { get; set; }

    /// <summary>Body name, mutated in place by <c>PmlRules.InferLegacyPml</c> when recovered from a legacy filename.</summary>
    public required string Body { get; set; }

    /// <summary>Planet radius in metres, used by <c>PmlRules.CorrespondsToMap</c>'s haversine distance.</summary>
    public double Radius { get; set; } = Constants.MapperConstants.DefaultRadiusMetres;

    /// <summary>PML identifier, mutated in place by <c>PmlRules.InferLegacyPml</c> when recovered from a legacy "Centro [id]" marker.</summary>
    public string PmlId { get; set; } = string.Empty;

    /// <summary>PML centre latitude in degrees, or <see langword="null"/> until known/recovered.</summary>
    public double? PmlCenterLat { get; set; }

    /// <summary>PML centre longitude in degrees, or <see langword="null"/> until known/recovered.</summary>
    public double? PmlCenterLon { get; set; }

    /// <summary><c>"{System}|{Body}"</c>, recomputed by <c>PmlRules.InferLegacyPml</c> after recovery; otherwise unused by matching.</summary>
    public string? BodyKey { get; set; }

    /// <summary>
    /// Candidate's persisted marks, scanned by <c>PmlRules.InferLegacyPml</c> for a legacy
    /// "Centro [id]" marker carrying the old PML identifier and geographic centre.
    /// </summary>
    public IReadOnlyList<PmlCandidateMark> Marks { get; set; } = Array.Empty<PmlCandidateMark>();
}

/// <summary>
/// A candidate map's mark, reduced to only the fields <c>PmlRules.InferLegacyPml</c> inspects.
/// </summary>
/// <param name="Name">Marker name, matched against the <c>Centro [id]</c> legacy pattern.</param>
/// <param name="Lat">Marker latitude in degrees, or <see langword="null"/> if the legacy record omitted it.</param>
/// <param name="Lon">Marker longitude in degrees, or <see langword="null"/> if the legacy record omitted it.</param>
public sealed record PmlCandidateMark(string? Name, double? Lat, double? Lon);
