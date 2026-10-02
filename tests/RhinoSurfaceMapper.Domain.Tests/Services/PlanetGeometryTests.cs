using FluentAssertions;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Services;

namespace RhinoSurfaceMapper.Domain.Tests.Services;

/// <summary>
/// Covers <see cref="PlanetGeometry"/>'s pure geometry functions: the local equirectangular
/// projection round-trip (ported from <c>test_coordinate_conversion_round_trip</c> in
/// <c>test_mapper_core.py</c>), haversine distance against known reference distances,
/// destination-point/surface-distance inverse consistency, signed heading error, and the precise
/// numeric semantics (precomputed degree/radian factors) the port must preserve bit-for-bit.
/// </summary>
public sealed class PlanetGeometryTests
{
    [Fact]
    public void Local_projection_round_trips_within_tolerance()
    {
        var (x, y) = PlanetGeometry.LocalFromGeographic(38.0, -9.0, 6_371_000.0, 38.01, -8.98);
        var (lat, lon) = PlanetGeometry.GeographicFromLocal(38.0, -9.0, 6_371_000.0, x, y);

        lat.Should().BeApproximately(38.01, 1e-10);
        lon.Should().BeApproximately(-8.98, 1e-10);
    }

    [Fact]
    public void Surface_distance_matches_known_values()
    {
        PlanetGeometry.SurfaceDistance(1, 0, 0, 0, 180).Should().BeApproximately(Math.PI, 1e-12);
    }

    [Fact]
    public void Surface_distance_matches_the_quarter_circumference_reference_distance()
    {
        // A 90-degree separation on the equator is a quarter of Earth's circumference: a
        // well-known reference distance (the metre's original 1/10,000,000-of-a-quadrant
        // definition is of the same order), here evaluated against the port's own
        // DefaultRadiusMetres constant.
        double quarterCircumference = Math.PI * MapperConstants.DefaultRadiusMetres / 2.0;

        PlanetGeometry.SurfaceDistance(MapperConstants.DefaultRadiusMetres, 0, 0, 0, 90)
            .Should().BeApproximately(quarterCircumference, 1e-6);
    }

    [Fact]
    public void Surface_distance_matches_a_known_real_world_city_pair_distance()
    {
        // London to Paris great-circle distance is commonly cited as ~343.5 km; this pins the
        // haversine implementation against that independently-known reference value.
        double distance = PlanetGeometry.SurfaceDistance(
            MapperConstants.DefaultRadiusMetres, 51.5074, -0.1278, 48.8566, 2.3522);

        distance.Should().BeApproximately(343_556.0, 50.0);
    }

    [Fact]
    public void Destination_point_is_consistent_with_surface_distance_and_bearing()
    {
        const double originLat = 38.0;
        const double originLon = -9.0;
        const double radius = MapperConstants.DefaultRadiusMetres;
        const double bearing = 50.0;
        const double distance = 2_000.0;

        var (x, y) = PlanetGeometry.DestinationPoint(0.0, 0.0, bearing, distance);
        var (destinationLat, destinationLon) = PlanetGeometry.GeographicFromLocal(originLat, originLon, radius, x, y);

        PlanetGeometry.SurfaceDistance(radius, originLat, originLon, destinationLat, destinationLon)
            .Should().BeApproximately(distance, 0.5);

        double recoveredBearing = Modulo(PlanetGeometry.ToDegrees(Math.Atan2(x, y)), 360.0);
        recoveredBearing.Should().BeApproximately(bearing, 1e-9);
    }

    [Theory]
    [InlineData(0.0, 1.0, 1.0)]
    [InlineData(359.0, 1.0, 2.0)]
    [InlineData(10.0, 0.0, -10.0)]
    [InlineData(0.0, 190.0, -170.0)]
    public void Heading_error_is_signed_and_crosses_the_000_359_boundary(double current, double target, double expected)
    {
        PlanetGeometry.HeadingError(current, target).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Heading_error_at_exactly_180_degrees_resolves_to_the_negative_boundary()
    {
        // The result range is the half-open [-180, 180): an exact 180-degree difference resolves
        // to -180, not +180, because the `+540 modulo 360 - 180` construction never produces +180.
        PlanetGeometry.HeadingError(0.0, 180.0).Should().Be(-180.0);
    }

    [Fact]
    public void Angle_delta_wraps_around_the_0_360_boundary_like_heading_error()
    {
        PlanetGeometry.AngleDelta(350.0, 10.0).Should().BeApproximately(20.0, 1e-9);
        PlanetGeometry.AngleDelta(10.0, 350.0).Should().BeApproximately(-20.0, 1e-9);
    }

    [Fact]
    public void To_radians_uses_the_precomputed_factor_not_a_reordered_division()
    {
        // 3 degrees is one of the double values where `degrees * Math.PI / 180.0` (left-to-right
        // evaluation) and `degrees * (Math.PI / 180.0)` (a precomputed factor, matching CPython's
        // internal degToRad constant) differ by one ULP. This pins the chosen operation order down
        // instead of merely re-deriving the same arithmetic the implementation performs.
        double naiveReordered = 3.0 * Math.PI / 180.0;
        double precomputedFactor = 3.0 * (Math.PI / 180.0);

        precomputedFactor.Should().NotBe(naiveReordered);
        PlanetGeometry.ToRadians(3.0).Should().Be(precomputedFactor);
    }

    [Fact]
    public void To_degrees_uses_the_precomputed_factor_not_a_reordered_division()
    {
        // 0.001 radians is one of the double values where `radians * 180.0 / Math.PI` and
        // `radians * (180.0 / Math.PI)` differ by one ULP; see To_radians_uses_the_precomputed_factor...
        double naiveReordered = 0.001 * 180.0 / Math.PI;
        double precomputedFactor = 0.001 * (180.0 / Math.PI);

        precomputedFactor.Should().NotBe(naiveReordered);
        PlanetGeometry.ToDegrees(0.001).Should().Be(precomputedFactor);
    }

    private static double Modulo(double value, double modulus) => ((value % modulus) + modulus) % modulus;
}
