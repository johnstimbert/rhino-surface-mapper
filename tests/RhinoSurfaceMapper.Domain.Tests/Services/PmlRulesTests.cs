using FluentAssertions;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Domain.Tests.Services;

/// <summary>
/// Ports <c>test_map_pml.py</c>'s invariants for PML identity, matching, legacy recovery, and
/// versioned filenames.
/// </summary>
public sealed class PmlRulesTests
{
    private static PmlCandidate MakeCandidate(string system = "Sol", string body = "Earth", double centerLon = 0.0, string pmlId = "1") => new()
    {
        System = system,
        Body = body,
        Radius = 6_371_000,
        PmlCenterLat = 0,
        PmlCenterLon = centerLon,
        PmlId = pmlId,
    };

    [Fact]
    public void Correspondence_uses_identity_and_inclusive_distance()
    {
        var candidate = MakeCandidate();
        // The 13,000 m PML radius is inclusive, so equality must still match.
        double degreesAtThreshold = MapperConstants.PmlMatchDistanceMetres / 6_371_000.0 * 180.0 / Math.PI;

        PmlRules.CorrespondsToMap(candidate, "Sol", "Earth", 0, 0.0).Should().BeTrue();
        PmlRules.CorrespondsToMap(candidate, "Sol", "Earth", 0, degreesAtThreshold).Should().BeTrue();
        PmlRules.CorrespondsToMap(candidate, "Sol", "Earth", 0, degreesAtThreshold + 0.001).Should().BeFalse();
        PmlRules.CorrespondsToMap(candidate, "Other", "Earth", 0, 0.0).Should().BeFalse();
        PmlRules.CorrespondsToMap(candidate, "Sol", "Mars", 0, 0.0).Should().BeFalse();
    }

    [Fact]
    public void Correspondence_distance_boundary_is_inclusive_at_exactly_13000_metres()
    {
        // On the equator (dLat = 0), surface_distance's haversine reduces exactly to R*dLon (no
        // small-angle approximation involved: asin(sin(z)) = z for |z| <= pi/2), so this longitude
        // offset reproduces the 13,000 m boundary to within a couple of ULP — far tighter than the
        // +0.001-degree (~111 m) margin the ported Python test above uses, which only proves
        // "comfortably inside" rather than "right at" the boundary.
        var candidate = MakeCandidate();
        double exactLon = MapperConstants.PmlMatchDistanceMetres / 6_371_000.0 * (180.0 / Math.PI);
        double distanceAtExactLon = PlanetGeometry.SurfaceDistance(6_371_000, 0, 0, 0, exactLon);

        // Sanity: the longitude offset really does reproduce the 13,000 m boundary distance.
        distanceAtExactLon.Should().BeApproximately(MapperConstants.PmlMatchDistanceMetres, 1e-6);

        PmlRules.CorrespondsToMap(candidate, "Sol", "Earth", 0, exactLon).Should().BeTrue();
        // One nanodegree further (~0.1 mm at this radius) pushes the distance measurably past
        // 13,000 m and must no longer match.
        PmlRules.CorrespondsToMap(candidate, "Sol", "Earth", 0, exactLon + 1e-9).Should().BeFalse();
    }

    [Fact]
    public void Pml_and_jd_identifiers_do_not_change_correspondence()
    {
        var pml = MakeCandidate(pmlId: "6");
        var jd = MakeCandidate(pmlId: "JD1");

        PmlRules.CorrespondsToMap(pml, "Sol", "Earth", 0, 0.05)
            .Should().Be(PmlRules.CorrespondsToMap(jd, "Sol", "Earth", 0, 0.05));
    }

    [Fact]
    public void Missing_centre_does_not_correspond()
    {
        // Python's test also covers `pml_center_lat = 'invalid'` (a string) on its duck-typed
        // SimpleNamespace fixture, relying on surface_distance's arithmetic raising a TypeError
        // that corresponds_to_map catches and turns into `false`. That sub-case does not port:
        // PmlCandidate.PmlCenterLat is a strongly-typed `double?`, so an incompatible value is
        // already a compile-time type error, making the runtime fallback unreachable here.
        var missing = MakeCandidate();
        missing.PmlCenterLat = null;

        PmlRules.CorrespondsToMap(missing, "Sol", "Earth", 0, 0).Should().BeFalse();
    }

    [Fact]
    public void Filename_and_distance_rules()
    {
        PmlRules.SafeFilenameComponent("A:/B. ").Should().Be("A__B");
        PlanetGeometry.SurfaceDistance(1, 0, 0, 0, 180).Should().BeApproximately(Math.PI, 1e-12);
        PlanetGeometry.SurfaceDistance(6_371_000, 0, 0, 0, 0.1).Should().BeLessThan(MapperConstants.PmlMatchDistanceMetres);
    }

    [Fact]
    public void Legacy_metadata_is_recovered_without_writing()
    {
        var candidate = new PmlCandidate
        {
            System = string.Empty,
            Body = string.Empty,
            PmlId = string.Empty,
            PmlCenterLat = null,
            PmlCenterLon = null,
            Marks = [new PmlCandidateMark("Centro [6]", 1, 2)],
        };

        PmlRules.InferLegacyPml(candidate, Path.Combine("Kappa", "Kappa 2 [6].json"), "Kappa", "wrong");

        (candidate.System, candidate.Body, candidate.PmlId).Should().Be(("Kappa", "Kappa 2", "6"));
        (candidate.PmlCenterLat, candidate.PmlCenterLon).Should().Be((1.0, 2.0));
    }

    [Fact]
    public void Matching_ignores_invalid_candidates_and_respects_the_strict_boundary()
    {
        const string validPath = "valid.json";
        const string invalidPath = "invalid.json";
        PmlCandidate Loader(string path) => path == invalidPath
            ? throw new Exceptions.MapValidationException("invalid")
            : new PmlCandidate { System = "Sol", Body = "Earth", Radius = 6_371_000, PmlCenterLat = 0, PmlCenterLon = 0, PmlId = "1" };

        var result = PmlRules.MatchingCandidates([validPath, invalidPath], "Sol", "Earth", 0, 0, Loader);

        result.Select(match => match.Path).Should().Equal(validPath);
    }

    [Fact]
    public void Next_version_does_not_treat_brackets_as_glob_patterns()
    {
        string path = PmlRules.NextVersionPath(
            "Kappa 2 [6].json",
            ["Kappa 2 [6].json", "Kappa 2 [6] v2.json", "other v9.json"]);

        Path.GetFileName(path).Should().Be("Kappa 2 [6] v3.json");
    }
}
