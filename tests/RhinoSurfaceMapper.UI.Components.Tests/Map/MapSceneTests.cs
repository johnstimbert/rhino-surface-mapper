using System.Collections.Immutable;
using FluentAssertions;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.UI.Components.Map;

namespace RhinoSurfaceMapper.UI.Components.Tests.Map;

/// <summary>
/// Unit tests for <see cref="MapScene.FromSnapshot"/>: projecting a <see cref="MapSessionSnapshot"/>
/// into the immutable DTO the canvas renders, including the "no map centre yet" empty-scene path
/// and the Rhino marker's geographic-to-local projection.
/// </summary>
public sealed class MapSceneTests
{
    [Fact]
    public void FromSnapshot_returns_Empty_when_the_snapshot_has_no_map_centre()
    {
        MapScene scene = MapScene.FromSnapshot(MapSessionSnapshot.Empty);

        scene.Should().Be(MapScene.Empty);
        scene.Trail.Should().BeEmpty();
        scene.Rhino.Should().BeNull();
    }

    [Fact]
    public void FromSnapshot_copies_trail_points_verbatim_and_preserves_order_and_break_flags()
    {
        ImmutableArray<TrailPoint> points =
        [
            new TrailPoint(X: 10.0, Y: 20.0, Lat: 1.0, Lon: 2.0, T: 100.0, BreakBefore: false),
            new TrailPoint(X: 30.0, Y: -40.0, Lat: 1.1, Lon: 2.1, T: 101.0, BreakBefore: true),
        ];
        MapSessionSnapshot snapshot = MapSessionSnapshot.Empty with
        {
            MapGeneration = 3,
            CenterLat = 10.0,
            CenterLon = 20.0,
            Points = points,
        };

        MapScene scene = MapScene.FromSnapshot(snapshot);

        scene.Generation.Should().Be(3);
        scene.Trail.Should().HaveCount(2);
        scene.Trail[0].Should().Be(new MapTrailPoint(10.0, 20.0, false));
        scene.Trail[1].Should().Be(new MapTrailPoint(30.0, -40.0, true));
    }

    [Fact]
    public void FromSnapshot_leaves_coverage_deposits_rigs_and_marks_empty_for_this_phases_scope()
    {
        MapSessionSnapshot snapshot = MapSessionSnapshot.Empty with
        {
            CenterLat = 0.0,
            CenterLon = 0.0,
        };

        MapScene scene = MapScene.FromSnapshot(snapshot);

        scene.RadarCoverage.Should().BeEmpty();
        scene.Deposits.Should().BeEmpty();
        scene.Rigs.Should().BeEmpty();
        scene.Marks.Should().BeEmpty();
    }

    [Fact]
    public void FromSnapshot_omits_the_Rhino_marker_when_not_in_the_SRV()
    {
        MapSessionSnapshot snapshot = MapSessionSnapshot.Empty with
        {
            CenterLat = 10.0,
            CenterLon = 20.0,
            InSrv = false,
            RhinoLat = 10.5,
            RhinoLon = 20.5,
            RhinoHeading = 90.0,
        };

        MapScene scene = MapScene.FromSnapshot(snapshot);

        scene.Rhino.Should().BeNull();
    }

    [Fact]
    public void FromSnapshot_omits_the_Rhino_marker_when_in_the_SRV_but_no_position_was_ever_reported()
    {
        MapSessionSnapshot snapshot = MapSessionSnapshot.Empty with
        {
            CenterLat = 10.0,
            CenterLon = 20.0,
            InSrv = true,
            RhinoLat = null,
            RhinoLon = null,
        };

        MapScene scene = MapScene.FromSnapshot(snapshot);

        scene.Rhino.Should().BeNull();
    }

    [Fact]
    public void FromSnapshot_projects_the_Rhino_marker_identically_to_PlanetGeometry_LocalFromGeographic()
    {
        const double centerLat = 10.0;
        const double centerLon = 20.0;
        const double radius = 250_000.0;
        const double rhinoLat = 10.2;
        const double rhinoLon = 20.3;
        const double heading = 275.0;

        MapSessionSnapshot snapshot = MapSessionSnapshot.Empty with
        {
            CenterLat = centerLat,
            CenterLon = centerLon,
            Radius = radius,
            InSrv = true,
            RhinoLat = rhinoLat,
            RhinoLon = rhinoLon,
            RhinoHeading = heading,
        };

        MapScene scene = MapScene.FromSnapshot(snapshot);

        var (expectedX, expectedY) = PlanetGeometry.LocalFromGeographic(centerLat, centerLon, radius, rhinoLat, rhinoLon);

        scene.Rhino.Should().NotBeNull();
        scene.Rhino!.Value.X.Should().BeApproximately(expectedX, 1e-6);
        scene.Rhino.Value.Y.Should().BeApproximately(expectedY, 1e-6);
        scene.Rhino.Value.HeadingDegrees.Should().Be(heading);
    }
}
