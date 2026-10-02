using System.Collections.Immutable;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Domain.Services;

namespace RhinoSurfaceMapper.UI.Components.Map;

/// <summary>
/// One trail sample already projected into the scene, ported from <see cref="Domain.Entities.TrailPoint"/>.
/// Carries only what the canvas draws — world coordinates and the break flag — not the
/// geographic/timestamp fields a renderer never needs.
/// </summary>
/// <param name="X">World easting in metres, relative to the map centre.</param>
/// <param name="Y">World northing in metres, relative to the map centre.</param>
/// <param name="BreakBefore">When <see langword="true"/>, the renderer must not draw a line from the previous point to this one.</param>
public readonly record struct MapTrailPoint(double X, double Y, bool BreakBefore);

/// <summary>
/// The live SRV/Rhino marker position and heading, already projected into world metres.
/// <see langword="null"/> on the owning <see cref="MapScene"/> whenever no telemetry has
/// established a position yet (no map centre, or the commander is not currently in the SRV).
/// </summary>
/// <param name="X">World easting in metres, relative to the map centre.</param>
/// <param name="Y">World northing in metres, relative to the map centre.</param>
/// <param name="HeadingDegrees">Heading in degrees, 0–360, or <see langword="null"/> when telemetry never reported one.</param>
public readonly record struct MapRhinoMarker(double X, double Y, double? HeadingDegrees);

/// <summary>
/// The immutable, already-projected scene pushed to <c>map-canvas.js</c> once per presenter tick,
/// ported from the design's "Map canvas" section: "A render tick builds an immutable
/// <c>MapScene</c> DTO... already projected to world metres". Phase 3 (read-only map display)
/// only populates <see cref="Trail"/> and <see cref="Rhino"/>; <see cref="RadarCoverage"/>,
/// <see cref="Deposits"/>, <see cref="Rigs"/> and <see cref="Marks"/> are always empty here —
/// they become meaningful once the later-phase dialogs/radar/marker features exist — but the
/// properties already exist on this type so a later phase only has to start populating them,
/// never change this type's shape or any existing consumer.
/// </summary>
public sealed record MapScene
{
    /// <summary>
    /// Mirrors <see cref="MapSessionSnapshot.MapGeneration"/>: a renderer can detect "this is a
    /// different map than last tick" by comparing generations instead of diffing collections.
    /// </summary>
    public required int Generation { get; init; }

    /// <summary>Recorded trail samples, in recording order, already projected to world metres.</summary>
    public required ImmutableArray<MapTrailPoint> Trail { get; init; }

    /// <summary>The current SRV/Rhino marker, or <see langword="null"/> when there is none to draw.</summary>
    public required MapRhinoMarker? Rhino { get; init; }

    /// <summary>Accumulated radar coverage discs. Always empty before the radar feature (Phase 8) exists.</summary>
    public required ImmutableArray<(double X, double Y, double Radius)> RadarCoverage { get; init; }

    /// <summary>Recorded mining deposits. Always empty before the markers feature (Phase 4) exists.</summary>
    public required ImmutableArray<(double X, double Y, string Name)> Deposits { get; init; }

    /// <summary>Placed rigs. Always empty before the markers feature (Phase 4) exists.</summary>
    public required ImmutableArray<(double X, double Y)> Rigs { get; init; }

    /// <summary>User-placed named marks. Always empty before the markers feature (Phase 4) exists.</summary>
    public required ImmutableArray<(double X, double Y, string Name)> Marks { get; init; }

    /// <summary>The scene for a session with no map open yet: no generation, every collection empty, no Rhino marker.</summary>
    public static MapScene Empty { get; } = new()
    {
        Generation = 0,
        Trail = [],
        Rhino = null,
        RadarCoverage = [],
        Deposits = [],
        Rigs = [],
        Marks = [],
    };

    /// <summary>
    /// Projects a <see cref="MapSessionSnapshot"/> into a <see cref="MapScene"/>. Trail points
    /// are already stored in world metres on <see cref="Domain.Entities.TrailPoint"/>, so they
    /// are copied verbatim; the Rhino marker is computed from the snapshot's raw
    /// latitude/longitude through <see cref="PlanetGeometry.LocalFromGeographic"/>, mirroring
    /// <see cref="Domain.Entities.MapSession.LocalFromGeographic"/> without needing a live
    /// <see cref="Domain.Entities.MapSession"/> instance.
    /// </summary>
    /// <param name="snapshot">The session snapshot to project.</param>
    /// <returns>
    /// <see cref="Empty"/> when <paramref name="snapshot"/> has no map centre yet; otherwise a
    /// populated scene with coverage/deposits/rigs/marks left empty per this phase's scope (see
    /// the type summary).
    /// </returns>
    public static MapScene FromSnapshot(MapSessionSnapshot snapshot)
    {
        if (snapshot.CenterLat is not double centerLat || snapshot.CenterLon is not double centerLon)
        {
            return Empty;
        }

        var trail = ImmutableArray.CreateBuilder<MapTrailPoint>(snapshot.Points.Length);
        foreach (var point in snapshot.Points)
        {
            trail.Add(new MapTrailPoint(point.X, point.Y, point.BreakBefore));
        }

        MapRhinoMarker? rhino = null;
        if (snapshot.InSrv && snapshot.RhinoLat is double rhinoLat && snapshot.RhinoLon is double rhinoLon)
        {
            var (x, y) = PlanetGeometry.LocalFromGeographic(centerLat, centerLon, snapshot.Radius, rhinoLat, rhinoLon);
            rhino = new MapRhinoMarker(x, y, snapshot.RhinoHeading);
        }

        return new MapScene
        {
            Generation = snapshot.MapGeneration,
            Trail = trail.MoveToImmutable(),
            Rhino = rhino,
            RadarCoverage = [],
            Deposits = [],
            Rigs = [],
            Marks = [],
        };
    }
}
