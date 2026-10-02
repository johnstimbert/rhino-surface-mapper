using System.Globalization;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Domain.Services;

/// <summary>
/// Computes overlay heading/distance guidance text and colours for the active navigation
/// target, ported from <c>MapperState.overlay_navigation</c>. Stateless: every mutation (blink
/// toggling, arrival side effects) is applied to the <see cref="MapSession"/> passed in.
/// </summary>
public static class OverlayNavigationCalculator
{
    /// <summary>
    /// Returns overlay heading text, distance text, colours, and the active target's name.
    /// </summary>
    /// <param name="session">The session supplying live telemetry and navigation state.</param>
    /// <param name="clock">
    /// Source of monotonic time for the blink toggle, read only when the target is within
    /// <see cref="MapperConstants.OverlayBlinkDistanceMetres"/>. A domain service must never read
    /// the wall clock directly (see <see cref="IClock"/>'s remarks).
    /// </param>
    /// <returns>
    /// Guidance text/colours. The target is chosen in priority order: explicit marker navigation
    /// (<see cref="MapSession.ActiveNavTarget"/>), return-to-pause, then the circular search
    /// route. Placeholder dashes are returned when navigation is unavailable (missing heading,
    /// position, or SRV telemetry). Arrival within <see cref="MapperConstants.NavigationArrivalMetres"/>
    /// has side effects on marker/pause navigation: it clears the active target and either
    /// resumes the paused search route or begins returning to the pause point.
    /// </returns>
    public static OverlayNavigationResult Evaluate(MapSession session, IClock clock)
    {
        string targetName = string.Empty;
        double? tx = null;
        double? ty = null;

        if (session.ActiveNavTarget is { } active)
        {
            tx = active.X;
            ty = active.Y;
            targetName = active.Name;
        }
        else if (session.ReturnToPause && session.SearchPausePoint is { } pause)
        {
            (tx, ty) = pause;
            targetName = "Pause Point ⏸";
        }
        else if (session.SearchStarted && !session.SearchPaused && session.NextTargetXy is { } next)
        {
            (tx, ty) = next;
            targetName = $"Search: Point {session.RouteIndex + 1}";
        }

        if (!session.InSrv || tx is null || ty is null || session.RhinoLat is null
            || session.RhinoLon is null || session.RhinoHeading is null)
        {
            return OverlayNavigationResult.Unavailable(targetName);
        }

        var (rhinoX, rhinoY) = session.LocalFromGeographic(session.RhinoLat.Value, session.RhinoLon.Value);
        double distance = Hypot(tx.Value - rhinoX, ty.Value - rhinoY);
        double bearing = Modulo(PlanetGeometry.ToDegrees(Math.Atan2(tx.Value - rhinoX, ty.Value - rhinoY)), 360.0);
        double error = PlanetGeometry.HeadingError(session.RhinoHeading.Value, bearing);

        // Tight +/-2 degree alignment is green, the wider +/-8 degree correction band is yellow,
        // and larger errors are red so the overlay communicates urgency.
        string headingColor = Math.Abs(error) switch
        {
            <= 2.0 => "#00cc44",
            <= 8.0 => "#ffd21c",
            _ => "#ff3030",
        };

        // Three arrows are enough inside the correction band; five arrows make large turns more
        // visible without changing the target bearing text.
        int arrows = Math.Abs(error) <= 8.0 ? 3 : 5;

        // "000" reproduces Python's f"{bearing:03.0f}": zero-padded to 3 digits, rounded to the
        // nearest whole degree. Both .NET's fixed-point double formatting (since .NET Core 3.0)
        // and CPython's float formatting round half-to-even at the IEEE 754 level, so the two
        // agree even at an exact .5 degree tie; no bearing in the ported tests lands on one.
        string bearingText = bearing.ToString("000", CultureInfo.InvariantCulture) + "°";
        string headingText = error switch
        {
            < -2.0 => $"{new string('<', arrows)} {bearingText}",
            > 2.0 => $"{bearingText} {new string('»', arrows)}",
            _ => bearingText,
        };

        string distanceColor = UpdateBlinkAndGetDistanceColor(session, clock, distance);

        ApplyArrivalSideEffects(session, distance);

        string distanceText = distance.ToString("F0", CultureInfo.InvariantCulture) + " m";
        return new OverlayNavigationResult(headingText, distanceText, headingColor, distanceColor, targetName);
    }

    /// <summary>
    /// Updates the blink toggle state and returns the resulting distance-text colour, ported
    /// from the <c>time.monotonic()</c>-driven blink block in <c>overlay_navigation</c>.
    /// </summary>
    private static string UpdateBlinkAndGetDistanceColor(MapSession session, IClock clock, double distance)
    {
        if (distance <= MapperConstants.OverlayBlinkDistanceMetres)
        {
            double now = clock.MonotonicSeconds;
            if (now >= session.OverlayNextBlink)
            {
                session.OverlayBlinkOn = !session.OverlayBlinkOn;
                session.OverlayNextBlink = now + MapperConstants.OverlayBlinkIntervalSeconds;
            }

            return session.OverlayBlinkOn ? "white" : "#202020";
        }

        session.OverlayBlinkOn = true;
        session.OverlayNextBlink = 0.0;
        return "white";
    }

    /// <summary>
    /// Applies the arrival-at-100 m side effects for marker and pause navigation, ported from the
    /// tail of <c>overlay_navigation</c>. Circular search-route arrival is handled separately by
    /// <see cref="SearchRouteCalculator.UpdateNext"/>, which runs on every telemetry sample rather
    /// than only when the overlay happens to be evaluated.
    /// </summary>
    private static void ApplyArrivalSideEffects(MapSession session, double distance)
    {
        if (distance > MapperConstants.NavigationArrivalMetres)
        {
            return;
        }

        if (session.ActiveNavTarget is not null)
        {
            session.ActiveNavTarget = null;
            if (session.SearchPaused && session.SearchPausePoint is not null)
            {
                session.ReturnToPause = true;
            }
            else
            {
                session.SearchPaused = false;
            }
        }
        else if (session.ReturnToPause)
        {
            session.ReturnToPause = false;
            session.SearchPaused = false;
            session.SearchPausePoint = null;
        }
    }

    private static double Hypot(double x, double y) => Math.Sqrt((x * x) + (y * y));

    private static double Modulo(double value, double modulus) => ((value % modulus) + modulus) % modulus;
}
