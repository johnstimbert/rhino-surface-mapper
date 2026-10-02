using FluentAssertions;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.Tests.TestSupport;

namespace RhinoSurfaceMapper.Domain.Tests.Services;

/// <summary>
/// Ports the overlay-guidance invariants from <c>test_mapper_core.py</c>'s overlay tests and the
/// entirety of <c>test_navigation_marker.py</c> (marker navigation, search-pause/return-to-pause
/// semantics, and <c>overlay_allowed</c>).
/// </summary>
public sealed class OverlayNavigationCalculatorTests
{
    private static MapSession NewSrvSession()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        return session;
    }

    [Fact]
    public void Overlay_navigation_matches_heading_thresholds()
    {
        var session = NewSrvSession();
        session.StartSearch();
        var clock = new FakeClock();

        var first = session.EvaluateOverlayNavigation(clock);
        first.Heading.Should().Be("000°");
        first.Distance.Should().Be("3500 m");
        first.HeadingColor.Should().Be("#00cc44");

        session.RhinoHeading = 358.0;
        var second = session.EvaluateOverlayNavigation(clock);
        second.Heading.Should().Be("000°");
        second.Distance.Should().Be("3500 m");
        second.HeadingColor.Should().Be("#00cc44");
    }

    [Theory]
    [InlineData(0, "#00cc44", 0)]
    [InlineData(2, "#00cc44", 0)]
    [InlineData(2.1, "#ffd21c", 3)]
    [InlineData(8, "#ffd21c", 3)]
    [InlineData(8.1, "#ff3030", 5)]
    [InlineData(179, "#ff3030", 5)]
    public void Overlay_colour_boundaries_and_arrow_counts_match_for_both_turn_directions(double deviation, string colour, int arrowCount)
    {
        var session = NewSrvSession();
        session.StartSearch();
        var clock = new FakeClock();

        foreach (int side in new[] { -1, 1 })
        {
            session.RhinoHeading = Modulo(-side * deviation, 360.0);
            var result = session.EvaluateOverlayNavigation(clock);

            result.HeadingColor.Should().Be(colour);
            char arrow = side > 0 ? '»' : '<';
            result.Heading.Count(c => c == arrow).Should().Be(arrowCount);
        }
    }

    [Fact]
    public void Overlay_shows_inactive_colour_once_search_stops()
    {
        var session = NewSrvSession();
        session.StartSearch();
        session.SearchStarted = false;
        var clock = new FakeClock();

        var result = session.EvaluateOverlayNavigation(clock);

        result.HeadingColor.Should().Be("#888888");
    }

    [Fact]
    public void Overlay_is_inactive_when_heading_is_missing()
    {
        var session = NewSrvSession();
        session.StartSearch();
        session.RhinoHeading = null;
        var clock = new FakeClock();

        var result = session.EvaluateOverlayNavigation(clock);

        result.Should().BeEquivalentTo(new
        {
            Heading = "—",
            Distance = "—",
            HeadingColor = "#888888",
            DistanceColor = "white",
            TargetName = "Search: Point 1",
        });
    }

    [Fact]
    public void Overlay_allowed_when_navigating_to_target_without_search()
    {
        var session = NewSrvSession();

        session.OverlayAllowed().Should().BeFalse();

        session.ActiveNavTarget = new NavigationTarget(NavigationTargetKind.Mark, "[Marca] Teste", 0.0, 500.0);
        session.OverlayAllowed().Should().BeTrue();

        session.ActiveNavTarget = null;
        session.OverlayAllowed().Should().BeFalse();
    }

    [Fact]
    public void Overlay_navigation_calculates_bearing_distance_and_target_name()
    {
        var session = NewSrvSession();
        // The target is 1000 m north (Y = +1000) of the Rhino at (0, 0).
        session.ActiveNavTarget = new NavigationTarget(NavigationTargetKind.Mark, "[Marca] Alfa", 0.0, 1000.0);
        var clock = new FakeClock();

        var result = session.EvaluateOverlayNavigation(clock);

        result.Heading.Should().Be("000°");
        result.Distance.Should().Be("1000 m");
        result.HeadingColor.Should().Be("#00cc44");
        result.TargetName.Should().Be("[Marca] Alfa");
    }

    [Fact]
    public void Search_pauses_when_navigating_and_stores_pause_point()
    {
        var session = NewSrvSession();
        session.StartSearch(0);
        session.SearchStarted.Should().BeTrue();
        session.SearchPaused.Should().BeFalse();
        session.SearchPausePoint.Should().BeNull();

        var (rhinoX, rhinoY) = session.LocalFromGeographic(session.RhinoLat!.Value, session.RhinoLon!.Value);
        session.SearchPaused = true;
        session.SearchPausePoint = (rhinoX, rhinoY);
        session.ActiveNavTarget = new NavigationTarget(NavigationTargetKind.Mark, "[Marca] Mina", 500.0, 500.0);

        session.SearchPaused.Should().BeTrue();
        session.SearchPausePoint.Should().Be((rhinoX, rhinoY));

        var result = session.EvaluateOverlayNavigation(new FakeClock());
        result.TargetName.Should().Be("[Marca] Mina");
    }

    [Fact]
    public void Arrival_within_100m_transitions_to_return_to_pause()
    {
        var session = NewSrvSession();
        session.SearchStarted = true;
        session.SearchPaused = true;
        session.SearchPausePoint = (0.0, 0.0);
        session.ActiveNavTarget = new NavigationTarget(NavigationTargetKind.Mark, "[Marca] Destino", 50.0, 50.0);

        // At 50 m from the target, the Rhino is inside the 100 m arrival radius.
        var (lat, lon) = session.GeographicFromLocal(50.0, 50.0);
        session.RhinoLat = lat;
        session.RhinoLon = lon;
        var clock = new FakeClock();
        session.EvaluateOverlayNavigation(clock);

        session.ActiveNavTarget.Should().BeNull();
        session.ReturnToPause.Should().BeTrue();

        var next = session.EvaluateOverlayNavigation(clock);
        next.TargetName.Should().Be("Pause Point ⏸");
    }

    [Fact]
    public void Arrival_at_pause_point_resumes_search()
    {
        var session = NewSrvSession();
        session.StartSearch(0);
        session.SearchPaused = true;
        session.ReturnToPause = true;
        session.SearchPausePoint = (0.0, 0.0);

        var (lat, lon) = session.GeographicFromLocal(0.0, 0.0);
        session.RhinoLat = lat;
        session.RhinoLon = lon;
        var clock = new FakeClock();
        session.EvaluateOverlayNavigation(clock);

        session.ReturnToPause.Should().BeFalse();
        session.SearchPaused.Should().BeFalse();
        session.SearchPausePoint.Should().BeNull();

        var next = session.EvaluateOverlayNavigation(clock);
        next.TargetName.Should().StartWith("Search: Point");
    }

    [Fact]
    public void Overlay_disallowed_when_commander_exits_rhino()
    {
        var session = NewSrvSession();
        session.StartSearch(0);
        session.OverlayAllowed().Should().BeTrue();

        session.ProcessStatus(TelemetrySamples.Status(flags: 0));
        session.InSrv.Should().BeFalse();
        session.OverlayAllowed().Should().BeFalse();

        session.ProcessStatus(TelemetrySamples.Status());
        session.InSrv.Should().BeTrue();
        session.OverlayAllowed().Should().BeTrue();
    }

    [Fact]
    public void Overlay_blink_toggles_every_point_four_five_seconds_inside_three_hundred_metres()
    {
        var session = NewSrvSession();
        session.ActiveNavTarget = new NavigationTarget(NavigationTargetKind.Mark, "Perto", 0.0, 200.0);
        var clock = new FakeClock { MonotonicSeconds = 0.0 };

        // Both overlay_blink_on and overlay_next_blink start at (true, 0.0), so the very first
        // evaluation at time 0.0 already reaches the "due to toggle" branch and flips to the
        // off/dark phase before returning.
        var first = session.EvaluateOverlayNavigation(clock);
        first.DistanceColor.Should().Be("#202020");

        clock.MonotonicSeconds = 0.5;
        var second = session.EvaluateOverlayNavigation(clock);
        second.DistanceColor.Should().Be("white");

        clock.MonotonicSeconds = 1.0;
        var third = session.EvaluateOverlayNavigation(clock);
        third.DistanceColor.Should().Be("#202020");
    }

    [Theory]
    [InlineData(299.0, true)]    // just inside the blink radius.
    [InlineData(300.0, true)]    // exactly at the boundary: the rule is <=, so blinking still applies.
    [InlineData(300.001, false)] // just outside: no blinking, distance text stays plain white.
    public void Overlay_blink_only_applies_within_the_inclusive_300_metre_boundary(double distanceMetres, bool blinkApplies)
    {
        var session = NewSrvSession();
        session.ActiveNavTarget = new NavigationTarget(NavigationTargetKind.Mark, "Alvo", 0.0, distanceMetres);
        var clock = new FakeClock { MonotonicSeconds = 10.0 };
        session.OverlayNextBlink = 10.0; // "due to toggle" exactly at this evaluation's clock time.
        bool blinkOnBefore = session.OverlayBlinkOn;

        var result = session.EvaluateOverlayNavigation(clock);

        if (blinkApplies)
        {
            session.OverlayBlinkOn.Should().Be(!blinkOnBefore);
            session.OverlayNextBlink.Should().Be(10.0 + MapperConstants.OverlayBlinkIntervalSeconds);
        }
        else
        {
            session.OverlayBlinkOn.Should().BeTrue();
            session.OverlayNextBlink.Should().Be(0.0);
            result.DistanceColor.Should().Be("white");
        }
    }

    private static double Modulo(double value, double modulus) => ((value % modulus) + modulus) % modulus;
}
