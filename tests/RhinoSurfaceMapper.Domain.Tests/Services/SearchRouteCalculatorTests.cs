using FluentAssertions;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.Tests.TestSupport;

namespace RhinoSurfaceMapper.Domain.Tests.Services;

/// <summary>
/// Ports the circular search-route invariants of <c>test_mapper_core.py</c>'s
/// <c>test_search_azimuth_rotates_route_and_survives_reload</c> and
/// <c>test_search_rejects_invalid_azimuth</c>.
/// </summary>
public sealed class SearchRouteCalculatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(359)]
    public void Search_azimuth_rotates_the_route_and_survives_a_document_round_trip(int azimuth)
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());

        session.StartSearch(azimuth);
        double angle = azimuth * Math.PI / 180.0;
        session.NextTargetXy!.Value.X.Should().BeApproximately(3500 * Math.Sin(angle), 1e-6);
        session.NextTargetXy!.Value.Y.Should().BeApproximately(3500 * Math.Cos(angle), 1e-6);

        var (lat, lon) = session.GeographicFromLocal(session.NextTargetXy.Value.X, session.NextTargetXy.Value.Y);
        session.RhinoLat = lat;
        session.RhinoLon = lon;
        session.UpdateNext();

        angle += 2 * Math.PI / SearchRouteCalculator.SearchTotalPoints;
        session.NextTargetXy!.Value.X.Should().BeApproximately(3500 * Math.Sin(angle), 1e-6);
        session.NextTargetXy!.Value.Y.Should().BeApproximately(3500 * Math.Cos(angle), 1e-6);

        // Persistence round-trip is modelled directly through ToDocument/LoadFromDocument rather
        // than a real file, since file I/O is a Phase 2 Infrastructure concern.
        var document = session.ToDocument();
        var loaded = new MapSession();
        loaded.LoadFromDocument(document);

        loaded.SearchAzimuth.Should().Be(azimuth);
        loaded.NextTargetXy.Should().Be(session.NextTargetXy);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(360)]
    public void Search_rejects_an_out_of_range_azimuth(int azimuth)
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());

        // Python additionally rejects 1.5, '90' and true (a bool, which is an int subclass in
        // Python) at runtime. Those sub-cases do not port: SearchRouteCalculator.StartSearch
        // takes a plain C# `int`, so a float/string/bool argument is already a compile-time type
        // error, making the runtime check unnecessary and unreachable for this signature.
        var act = () => session.StartSearch(azimuth);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Search_total_points_is_thirteen_for_the_configured_radius_and_spacing()
    {
        SearchRouteCalculator.SearchTotalPoints.Should().Be(13);
    }

    [Fact]
    public void Update_next_uses_radians_azimuth_plus_index_times_step_not_a_combined_then_converted_angle()
    {
        // Azimuth 0 / route index 1 is one of the (azimuth, index) pairs where the production
        // formula "radians(azimuth) + index*step" (step already in radians) and a
        // combined-then-converted alternative "radians(azimuth + index*stepDegrees)" — algebraically
        // equal but not bit-identical — diverge by a few ULP in Math.Sin. This test would fail if
        // UpdateNext were reverted to the combined-then-converted form.
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.StartSearch(azimuth: 0);

        // SkipNext advances RouteIndex from 0 to 1 and recomputes NextTargetXy for index 1.
        session.SkipNext().Should().BeTrue();

        double stepRadians = 2.0 * Math.PI / SearchRouteCalculator.SearchTotalPoints;
        double stepDegrees = 360.0 / SearchRouteCalculator.SearchTotalPoints;
        double correctAngle = PlanetGeometry.ToRadians(0.0) + (1 * stepRadians);
        double combinedThenConvertedAngle = PlanetGeometry.ToRadians(0.0 + (1 * stepDegrees));
        double expectedX = 3500.0 * Math.Sin(correctAngle);
        double wrongX = 3500.0 * Math.Sin(combinedThenConvertedAngle);

        expectedX.Should().NotBe(wrongX); // sanity: the two formulas really do diverge here.
        session.NextTargetXy!.Value.X.Should().Be(expectedX);
    }
}
