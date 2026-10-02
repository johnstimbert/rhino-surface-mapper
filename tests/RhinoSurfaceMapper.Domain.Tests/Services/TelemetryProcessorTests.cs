using FluentAssertions;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.Tests.TestSupport;

namespace RhinoSurfaceMapper.Domain.Tests.Services;

/// <summary>
/// Covers <see cref="TelemetryProcessor"/> invariants not already exercised end-to-end through
/// <c>MapSessionTests</c>: the SRV-flag trail-recording gate, the minimum trail spacing and
/// break-distance boundaries, heading normalisation's Python-style non-negative modulo, and the
/// documented Phase 2 boundary around a telemetry sample's absent-vs-invalid heading.
/// </summary>
public sealed class TelemetryProcessorTests
{
    private static MapSession SessionAtOrigin()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status(latitude: 38.0, longitude: -9.0));
        return session;
    }

    /// <summary>
    /// Finds a latitude degrees value whose forward local-projection conversion
    /// (<see cref="PlanetGeometry.LocalFromGeographic"/> with a <c>0</c> map centre, so
    /// <c>lat - centerLat == lat</c> with no addition rounding) lands on <paramref name="targetMetres"/>
    /// bit-for-bit, rather than a value merely close to it.
    /// </summary>
    /// <remarks>
    /// Composing <see cref="PlanetGeometry.GeographicFromLocal"/> then
    /// <see cref="PlanetGeometry.LocalFromGeographic"/> to construct a telemetry latitude for an
    /// "exact" boundary distance is not reliable: <c>radians(degrees(x))</c> is not an exact
    /// floating-point involution, so that round trip can overshoot a strict boundary by a few
    /// ULP (confirmed empirically while writing this test — feeding 100.0 m through that round
    /// trip at latitude 38° yields 100.00000000025..., which incorrectly trips the
    /// "strictly greater than 100" break rule). Walking the ULP neighbourhood of the obvious
    /// estimate against the real forward formula finds the exact preimage instead.
    /// </remarks>
    private static double FindLatitudeForExactLocalDistance(double radius, double targetMetres)
    {
        double ForwardY(double lat) => PlanetGeometry.LocalFromGeographic(0.0, 0.0, radius, lat, 0.0).Y;

        double estimate = PlanetGeometry.GeographicFromLocal(0.0, 0.0, radius, 0.0, targetMetres).Lat;
        double best = estimate;
        double bestDiff = Math.Abs(ForwardY(estimate) - targetMetres);
        double up = estimate;
        double down = estimate;
        for (int i = 0; i < 5000 && bestDiff > 0.0; i++)
        {
            up = Math.BitIncrement(up);
            double diffUp = Math.Abs(ForwardY(up) - targetMetres);
            if (diffUp < bestDiff)
            {
                bestDiff = diffUp;
                best = up;
            }

            down = Math.BitDecrement(down);
            double diffDown = Math.Abs(ForwardY(down) - targetMetres);
            if (diffDown < bestDiff)
            {
                bestDiff = diffDown;
                best = down;
            }
        }

        return best;
    }

    [Fact]
    public void Heading_normalizes_a_negative_value_with_python_style_non_negative_modulo()
    {
        // -10 % 360 is -10 under C#'s operator (it preserves the dividend's sign); Python's `%`
        // (and the ported NormalizeHeading) always returns a non-negative result for a positive
        // modulus, so the expected value is 350, not -10.
        var session = SessionAtOrigin();

        session.ProcessStatus(TelemetrySamples.Status(heading: -10.0));

        session.RhinoHeading.Should().Be(350.0);
    }

    [Fact]
    public void Heading_absent_preserves_the_previous_value()
    {
        // Documents the current, intentionally narrow Phase 1 scope: TelemetryStatusSample.Heading
        // is a plain `double?`, so "the Heading field was absent from Status.json" and "the field
        // was present but non-numeric" both arrive here as `null` and are handled identically (the
        // previous heading is kept). Python distinguishes the two: an absent field also keeps the
        // previous heading, but a present-and-invalid field clears it to None. Reproducing that
        // second sub-case requires the Phase 2 Status.json reader to perform its own float-parse
        // and explicitly clear RhinoHeading on failure; TelemetryProcessor itself cannot recover
        // the distinction once the value has already collapsed to null. This test pins today's
        // behaviour precisely so the gap is tracked rather than left to drift.
        var session = SessionAtOrigin();
        session.ProcessStatus(TelemetrySamples.Status(heading: 42.0));

        session.ProcessStatus(TelemetrySamples.Status(heading: null));

        session.RhinoHeading.Should().Be(42.0);
    }

    [Fact]
    public void Trail_recording_requires_the_srv_flag_bit()
    {
        var session = new MapSession();

        // Fuel-low (bit 19) set without the SRV bit (bit 26) must still be rejected outright.
        var result = session.ProcessStatus(TelemetrySamples.Status(flags: MapperConstants.FuelLowFlag));

        result.Accepted.Should().BeFalse();
        session.InSrv.Should().BeFalse();
        session.Points.Should().BeEmpty();
    }

    [Theory]
    [InlineData(9.999, 1)] // just under the 10 m minimum spacing: no new point recorded.
    [InlineData(10.0, 2)]  // exactly at the minimum spacing: still recorded (the rule is >=, not >).
    public void Trail_points_require_the_minimum_spacing_boundary(double distanceMetres, int expectedPointCount)
    {
        const double radius = 6_371_000.0;
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status(latitude: 0.0, longitude: 0.0, planetRadius: radius));
        double lat = FindLatitudeForExactLocalDistance(radius, distanceMetres);

        session.ProcessStatus(TelemetrySamples.Status(latitude: lat, longitude: 0.0, planetRadius: radius));

        session.Points.Should().HaveCount(expectedPointCount);
    }

    [Fact]
    public void Trail_break_is_not_flagged_at_exactly_the_boundary_distance()
    {
        const double radius = 6_371_000.0;
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status(latitude: 0.0, longitude: 0.0, planetRadius: radius));
        double lat = FindLatitudeForExactLocalDistance(radius, MapperConstants.TrailBreakDistanceMetres);

        session.ProcessStatus(TelemetrySamples.Status(latitude: lat, longitude: 0.0, planetRadius: radius));

        // break_before only fires for distances strictly greater than 100 m (Python: `> 100.0`).
        session.Points[^1].BreakBefore.Should().BeFalse();
    }

    [Fact]
    public void Trail_break_is_flagged_just_past_the_boundary_distance()
    {
        var session = SessionAtOrigin();
        var (lat, lon) = session.GeographicFromLocal(0.0, MapperConstants.TrailBreakDistanceMetres + 0.001);

        session.ProcessStatus(TelemetrySamples.Status(latitude: lat, longitude: lon));

        session.Points[^1].BreakBefore.Should().BeTrue();
    }
}

