using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Domain.Services;

/// <summary>
/// Updates a <see cref="MapSession"/> from one telemetry sample, ported from
/// <c>MapperState.process_status</c>. Stateless: every mutation is applied to the
/// <see cref="MapSession"/> passed in, not to any field of this type.
/// </summary>
public static class TelemetryProcessor
{
    /// <summary>
    /// Updates fuel, heading, current body identity, local projection centre, trail samples and
    /// the current search target from one telemetry sample.
    /// </summary>
    /// <param name="session">The session to update.</param>
    /// <param name="sample">The parsed telemetry sample.</param>
    /// <param name="recordPosition">
    /// When <see langword="false"/>, validates identity/coordinates without moving the live SRV
    /// position or appending a trail point — used to probe "does this telemetry belong to the
    /// current map" without side effects.
    /// </param>
    /// <returns>
    /// A <see cref="StatusUpdate"/> describing whether the sample was accepted and whether the
    /// caller should handle a body/location change.
    /// </returns>
    public static StatusUpdate Process(MapSession session, TelemetryStatusSample sample, bool recordPosition = true)
    {
        session.InSrv = (sample.Flags & MapperConstants.SrvFlag) != 0;
        if (!session.InSrv)
        {
            return StatusUpdate.Rejected;
        }

        if (sample.FuelReservoir is double reservoir)
        {
            session.FuelReservoir = reservoir;

            // Elite reports the SRV/Rhino reservoir as a 0.0-0.80 value; in game UI terms 0.80
            // represents a full 100% tank.
            session.FuelPercent = Math.Clamp(reservoir / 0.80 * 100.0, 0.0, 100.0);
            session.FuelLow = (sample.Flags & MapperConstants.FuelLowFlag) != 0;
        }

        if (sample.Heading is double heading)
        {
            session.RhinoHeading = NormalizeHeading(heading);
        }

        if (sample.Latitude is not double latitude || sample.Longitude is not double longitude)
        {
            return StatusUpdate.Rejected;
        }

        string system = sample.StarSystem ?? string.Empty;
        string body = sample.BodyName ?? string.Empty;

        // Status.json may omit StarSystem for several seconds. If the body is unchanged,
        // preserving the last valid system avoids treating each update as a new body and
        // clearing the map.
        if (system.Length == 0 && session.System.Length > 0 && body == session.Body)
        {
            system = session.System;
        }

        string bodyKey = $"{system}|{body}";
        if (system.Length > 0 && session.System.Length == 0 && session.Body.Length > 0 && body == session.Body)
        {
            session.System = system;
            session.BodyKey = bodyKey;
        }

        if (session.BodyKey is not null && session.BodyKey != bodyKey)
        {
            return new StatusUpdate(true, true, system, body, latitude, longitude);
        }

        if (recordPosition)
        {
            session.RhinoLat = latitude;
            session.RhinoLon = longitude;
        }

        if (session.ReadOnly && session.BodyKey != bodyKey)
        {
            // Inspecting another body must not clear a protected/read-only map.
            return new StatusUpdate(true, true, system, body, latitude, longitude);
        }

        if (session.BodyKey != bodyKey)
        {
            // A new body defines a new coordinate frame; trails from different bodies cannot
            // share the same local metre coordinates.
            session.BodyKey = bodyKey;
            session.System = system;
            session.Body = body;
            session.CenterLat = session.RhinoLat;
            session.CenterLon = session.RhinoLon;
            session.Radius = sample.PlanetRadius ?? MapperConstants.DefaultRadiusMetres;
            session.NewMap();
        }

        if (!recordPosition)
        {
            return new StatusUpdate(true, false, system, body, latitude, longitude);
        }

        var (x, y) = session.LocalFromGeographic(session.RhinoLat!.Value, session.RhinoLon!.Value);

        // Trail samples are kept at least TrailMinimumSpacingMetres apart to limit file size and
        // visual noise. Jumps over TrailBreakDistanceMetres mark BreakBefore so drawing code does
        // not invent a continuous line across teleports or stale samples.
        if (!session.ReadOnly && (session.LastXy is null || Hypot(x - session.LastXy.Value.X, y - session.LastXy.Value.Y) >= MapperConstants.TrailMinimumSpacingMetres))
        {
            bool breakBefore = session.LastXy is not null
                && Hypot(x - session.LastXy.Value.X, y - session.LastXy.Value.Y) > MapperConstants.TrailBreakDistanceMetres;
            session.AddPoint(new TrailPoint(x, y, session.RhinoLat.Value, session.RhinoLon.Value, sample.Timestamp, breakBefore));
            session.LastXy = (x, y);
        }

        session.UpdateNext();
        return new StatusUpdate(true, false, system, body, latitude, longitude);
    }

    /// <summary>
    /// Normalises a heading to the half-open range [0, 360), matching Python's
    /// <c>float(heading) % 360.0</c> (whose result is always non-negative for a positive
    /// modulus, unlike C#'s <c>%</c> operator for a negative dividend).
    /// </summary>
    private static double NormalizeHeading(double heading) => ((heading % 360.0) + 360.0) % 360.0;

    /// <summary>
    /// Straight-line distance, reproducing Python's <c>math.hypot(a, b)</c>. Uses the plain
    /// <c>sqrt(a² + b²)</c> form rather than an overflow-guarded hypot algorithm: at the metre
    /// magnitudes every map coordinate uses (at most a few hundred kilometres), the two produce
    /// identical <see cref="double"/> results, and .NET has no built-in two-argument hypot to
    /// call instead.
    /// </summary>
    private static double Hypot(double x, double y) => Math.Sqrt((x * x) + (y * y));
}
