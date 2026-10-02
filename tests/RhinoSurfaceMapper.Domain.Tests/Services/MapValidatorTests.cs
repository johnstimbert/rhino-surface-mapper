using FluentAssertions;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.Tests.TestSupport;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Domain.Tests.Services;

/// <summary>
/// Ports <c>test_map_validation.py</c>'s invariants: a corrupt document must be rejected without
/// corrupting the session already in memory, and legacy marker-like records missing
/// <c>lat</c>/<c>lon</c> must have them reconstructed from <c>x</c>/<c>y</c>.
/// </summary>
public sealed class MapValidatorTests
{
    private static (MapSession Session, RawMapDocument Baseline) NewBaseline()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status(latitude: 38.0, longitude: -9.0, heading: 0.0));
        return (session, session.ToDocument());
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        var (_, baseline) = NewBaseline();

        yield return new object[] { "zero planet radius", baseline with { PlanetRadius = 0 } };
        yield return new object[] { "non-finite centre latitude", baseline with { CenterLat = double.NaN } };
        yield return new object[] { "search started without a datum", baseline with { SearchStarted = true, DatumLat = null, DatumLon = null } };
        yield return new object[] { "route index out of range", baseline with { RouteIndex = 999 } };
        yield return new object[]
        {
            "non-numeric deposit rig count",
            baseline with
            {
                Deposits = [new RawDepositDocument { X = 0.0, Y = 0.0, Lat = 38.0, Lon = -9.0, Name = "A", Size = "Grande", Rigs = "abc" }],
            },
        };
        yield return new object[]
        {
            "invalid route history status",
            baseline with
            {
                RouteHistory = [new RawRouteHistoryDocument { X = 0.0, Y = 0.0, Number = 1, Status = "invalid" }],
            },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void Invalid_maps_leave_existing_state_unchanged(string reason, RawMapDocument invalidDocument)
    {
        var (session, baseline) = NewBaseline();

        var act = () => session.LoadFromDocument(invalidDocument);

        act.Should().Throw<MapValidationException>(reason);
        session.ToDocument().Should().BeEquivalentTo(baseline);
    }

    [Fact]
    public void Legacy_marker_coordinates_are_reconstructed()
    {
        var (session, baseline) = NewBaseline();
        var document = baseline with
        {
            Deposits = [new RawDepositDocument { X = 0.0, Y = 0.0, Name = "Antigo" }],
        };

        session.LoadFromDocument(document);

        var deposit = session.Deposits.Single();
        (deposit.Lat, deposit.Lon).Should().Be((38.0, -9.0));
        deposit.Size.Should().Be(DepositSize.Pequeno);
        deposit.Rigs.Should().Be(1);
    }

    public static IEnumerable<object[]> InvalidDepositRigCounts()
    {
        // Python raises the same "Invalid rig count." message for both failure causes:
        // `not count.is_integer() or not 1 <= count <= 6` is a single combined check, not two.
        yield return new object[] { "non-integral", 3.5 };
        yield return new object[] { "integral but below range", 0 };
        yield return new object[] { "integral but above range", 7 };
    }

    [Theory]
    [MemberData(nameof(InvalidDepositRigCounts))]
    public void Deposit_rig_count_reports_the_combined_python_message(string reason, object rigs)
    {
        var (session, baseline) = NewBaseline();
        var document = baseline with
        {
            Deposits = [new RawDepositDocument { X = 0.0, Y = 0.0, Lat = 38.0, Lon = -9.0, Name = "A", Size = "Grande", Rigs = rigs }],
        };

        var act = () => session.LoadFromDocument(document);

        act.Should().Throw<MapValidationException>(reason).WithMessage("Invalid rig count.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(6.0)]
    public void Deposit_rig_count_accepts_in_range_integral_values(object rigs)
    {
        var (session, baseline) = NewBaseline();
        var document = baseline with
        {
            Deposits = [new RawDepositDocument { X = 0.0, Y = 0.0, Lat = 38.0, Lon = -9.0, Name = "A", Size = "Grande", Rigs = rigs }],
        };

        session.LoadFromDocument(document);

        session.Deposits.Single().Rigs.Should().Be((int)Math.Floor(Convert.ToDouble(rigs)));
    }

    public static IEnumerable<object[]> InvalidRouteHistoryNumbers()
    {
        // Same single combined message as the rig count: non-integral and out-of-range both
        // raise "Invalid route target number.", matching Python's one `raise ValueError(...)`.
        yield return new object[] { "non-integral", 1.5 };
        yield return new object[] { "integral but below range", 0 };
        yield return new object[] { "integral but above range", SearchRouteCalculator.SearchTotalPoints + 1 };
    }

    [Theory]
    [MemberData(nameof(InvalidRouteHistoryNumbers))]
    public void Route_history_number_reports_the_combined_python_message(string reason, object number)
    {
        var (session, baseline) = NewBaseline();
        var document = baseline with
        {
            RouteHistory = [new RawRouteHistoryDocument { X = 0.0, Y = 0.0, Number = number, Status = "reached" }],
        };

        var act = () => session.LoadFromDocument(document);

        act.Should().Throw<MapValidationException>(reason).WithMessage("Invalid route target number.");
    }

    [Fact]
    public void Route_history_number_accepts_in_range_integral_values()
    {
        var (session, baseline) = NewBaseline();
        var document = baseline with
        {
            RouteHistory = [new RawRouteHistoryDocument { X = 0.0, Y = 0.0, Number = SearchRouteCalculator.SearchTotalPoints, Status = "reached" }],
        };

        session.LoadFromDocument(document);

        session.RouteHistory.Single().Number.Should().Be(SearchRouteCalculator.SearchTotalPoints);
    }
}
