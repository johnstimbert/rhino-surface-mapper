using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Enums;

namespace RhinoSurfaceMapper.Domain.Services;

/// <summary>
/// Computes and advances the circular search route around a session's search Datum, ported from
/// <c>MapperState.search_total_points</c>/<c>start_search</c>/<c>update_next</c>/<c>skip_next</c>.
/// Stateless: every mutation is applied to the <see cref="MapSession"/> passed in.
/// </summary>
public static class SearchRouteCalculator
{
    /// <summary>
    /// How many targets cover the circular search route, ported from
    /// <c>MapperState.search_total_points</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="Math.Ceiling(double)"/> rounds up so the arc spacing never exceeds
    /// <see cref="MapperConstants.SearchSpacingMetres"/>. With the current 3 500 m radius and
    /// 1 800 m spacing this yields the historical 13-point route
    /// (<c>ceil(2·π·3500 / 1800) = 13</c>). Computed from constants only — it does not depend on
    /// any particular session — but kept as a member here (not on <c>MapperConstants</c>) because
    /// it is a derived rule, not an independently tunable value.
    /// </remarks>
    public static int SearchTotalPoints { get; } = Math.Max(
        1,
        (int)Math.Ceiling(2.0 * Math.PI * MapperConstants.SearchRadiusMetres / MapperConstants.SearchSpacingMetres));

    /// <summary>
    /// Sets the search Datum at the current SRV position and prepares the route, ported from
    /// <c>MapperState.start_search</c>.
    /// </summary>
    /// <param name="session">The session to start searching on.</param>
    /// <param name="azimuth">
    /// Initial search bearing in degrees, where 0 is north and values increase clockwise. Typed
    /// as a plain <see cref="int"/>, which already excludes the <c>bool</c>/<c>float</c>/
    /// <c>string</c> values Python's runtime type check additionally had to reject (see the
    /// ported test's remarks for the one sub-case this supersedes).
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the search could be started; <see langword="false"/> for a
    /// read-only session or incomplete telemetry (no known SRV position or map centre yet).
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="azimuth"/> is outside the inclusive 000–359 degree range.</exception>
    public static bool StartSearch(MapSession session, int azimuth = 0)
    {
        if (session.ReadOnly)
        {
            return false;
        }

        if (azimuth is < 0 or > 359)
        {
            throw new ArgumentOutOfRangeException(nameof(azimuth), azimuth, "Search azimuth must be an integer between 000 and 359.");
        }

        if (session.RhinoLat is null || session.RhinoLon is null || session.CenterLat is null)
        {
            return false;
        }

        session.SearchAzimuth = azimuth;
        session.DatumLat = session.RhinoLat;
        session.DatumLon = session.RhinoLon;
        session.SearchStarted = true;
        session.SearchPaused = false;
        session.SearchPausePoint = null;
        session.ReturnToPause = false;
        session.RouteIndex = 0;
        session.NextTargetXy = null;
        session.ClearRouteHistory();
        UpdateNext(session);
        return true;
    }

    /// <summary>
    /// Marks the current search target as skipped and advances to the next one, ported from
    /// <c>MapperState.skip_next</c>.
    /// </summary>
    /// <returns><see langword="true"/> when a target was skipped; <see langword="false"/> for a read-only session or no active target.</returns>
    public static bool SkipNext(MapSession session)
    {
        if (session.ReadOnly)
        {
            return false;
        }

        if (!session.SearchStarted || session.NextTargetXy is not { } target)
        {
            return false;
        }

        session.AddRouteHistory(new RouteHistoryEntry(session.RouteIndex + 1, target.X, target.Y, RouteStatus.Skipped));
        session.RouteIndex += 1;
        if (session.RouteIndex >= SearchTotalPoints)
        {
            session.NextTargetXy = null;
        }
        else
        {
            UpdateNext(session);
        }

        return true;
    }

    /// <summary>
    /// Refreshes the circular search target and auto-advances on arrival, ported from
    /// <c>MapperState.update_next</c>.
    /// </summary>
    /// <remarks>
    /// The route is a ring around the Datum. The first point starts at the configured azimuth,
    /// with 0° pointing north and subsequent points moving clockwise
    /// (<c>x = datum_x + radius·sin(angle)</c>, <c>y = datum_y + radius·cos(angle)</c>). A target
    /// is considered reached inside <see cref="MapperConstants.RouteArrivalMetres"/>, matching
    /// the overlay arrival rule.
    /// </remarks>
    public static void UpdateNext(MapSession session)
    {
        if (session.ReadOnly)
        {
            session.NextTargetXy = null;
            return;
        }

        if (!session.SearchStarted || session.DatumLat is null || session.DatumLon is null
            || session.RouteIndex >= SearchTotalPoints)
        {
            session.NextTargetXy = null;
            return;
        }

        var (datumX, datumY) = session.LocalFromGeographic(session.DatumLat.Value, session.DatumLon.Value);

        // Operation order mirrors Python exactly: `angle = radians(azimuth) + route_index *
        // step_angle` (radians() applied once to azimuth alone, not to the whole sum) so the
        // two ports stay bit-identical rather than merely algebraically equivalent.
        double stepAngleRadians = (2.0 * Math.PI) / SearchTotalPoints;
        double angle = PlanetGeometry.ToRadians(session.SearchAzimuth) + (session.RouteIndex * stepAngleRadians);
        double targetX = datumX + (MapperConstants.SearchRadiusMetres * Math.Sin(angle));
        double targetY = datumY + (MapperConstants.SearchRadiusMetres * Math.Cos(angle));
        session.NextTargetXy = (targetX, targetY);

        if (session.RhinoLat is null || session.RhinoLon is null)
        {
            return;
        }

        var (rhinoX, rhinoY) = session.LocalFromGeographic(session.RhinoLat.Value, session.RhinoLon.Value);
        if (Hypot(targetX - rhinoX, targetY - rhinoY) > MapperConstants.RouteArrivalMetres)
        {
            return;
        }

        session.AddRouteHistory(new RouteHistoryEntry(session.RouteIndex + 1, targetX, targetY, RouteStatus.Reached));
        session.RouteIndex += 1;
        if (session.RouteIndex >= SearchTotalPoints)
        {
            session.NextTargetXy = null;
            return;
        }

        angle = PlanetGeometry.ToRadians(session.SearchAzimuth) + (session.RouteIndex * stepAngleRadians);
        session.NextTargetXy = (datumX + (MapperConstants.SearchRadiusMetres * Math.Sin(angle)), datumY + (MapperConstants.SearchRadiusMetres * Math.Cos(angle)));
    }

    private static double Hypot(double x, double y) => Math.Sqrt((x * x) + (y * y));
}
