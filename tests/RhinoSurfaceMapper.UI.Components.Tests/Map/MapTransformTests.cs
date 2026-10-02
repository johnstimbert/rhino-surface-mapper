using FluentAssertions;
using RhinoSurfaceMapper.UI.Components.Map;

namespace RhinoSurfaceMapper.UI.Components.Tests.Map;

/// <summary>
/// Unit tests for <see cref="MapTransform"/>: the authoritative world↔screen transform math,
/// covering the known-value formulas from the design ("screenX = width/2 + (worldX - centerX) *
/// scale", "screenY = height/2 - (worldY - centerY) * scale"), the zoom-clamp boundaries, and the
/// cursor-anchored zoom invariant.
/// </summary>
public sealed class MapTransformTests
{
    [Theory]
    [InlineData(800.0, 0.0, 0.0, 1.0, 400.0)]
    [InlineData(800.0, 100.0, 0.0, 1.0, 500.0)]
    [InlineData(800.0, 0.0, 100.0, 1.0, 300.0)]
    [InlineData(800.0, 100.0, 50.0, 2.0, 500.0)]
    [InlineData(1024.0, -200.0, 0.0, 0.08, 496.0)]
    public void ToScreenX_matches_the_known_value_formula(double canvasWidth, double worldX, double centerX, double scale, double expected)
    {
        double actual = MapTransform.ToScreenX(canvasWidth, worldX, centerX, scale);

        actual.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(600.0, 0.0, 0.0, 1.0, 300.0)]
    [InlineData(600.0, 100.0, 0.0, 1.0, 200.0)] // world Y increases upward -> screen Y decreases.
    [InlineData(600.0, 0.0, 100.0, 1.0, 400.0)]
    [InlineData(600.0, 100.0, 50.0, 2.0, 200.0)]
    public void ToScreenY_matches_the_known_value_formula_and_flips_the_axis(double canvasHeight, double worldY, double centerY, double scale, double expected)
    {
        double actual = MapTransform.ToScreenY(canvasHeight, worldY, centerY, scale);

        actual.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(800.0, 600.0, 123.0, 456.0, 10.0, -20.0, 1.7)]
    [InlineData(1024.0, 768.0, -50.0, 900.0, -1000.0, 2000.0, 0.08)]
    [InlineData(1280.0, 720.0, 0.0, 0.0, 0.0, 0.0, 10.0)]
    public void ToWorldX_and_ToWorldY_are_the_exact_inverse_of_ToScreenX_and_ToScreenY(
        double canvasWidth, double canvasHeight, double worldX, double worldY, double centerX, double centerY, double scale)
    {
        double screenX = MapTransform.ToScreenX(canvasWidth, worldX, centerX, scale);
        double screenY = MapTransform.ToScreenY(canvasHeight, worldY, centerY, scale);

        double roundTrippedWorldX = MapTransform.ToWorldX(canvasWidth, screenX, centerX, scale);
        double roundTrippedWorldY = MapTransform.ToWorldY(canvasHeight, screenY, centerY, scale);

        roundTrippedWorldX.Should().BeApproximately(worldX, 1e-6);
        roundTrippedWorldY.Should().BeApproximately(worldY, 1e-6);
    }

    [Fact]
    public void ClampScale_leaves_an_in_range_value_untouched()
    {
        MapTransform.ClampScale(MapTransform.InitialScale).Should().Be(MapTransform.InitialScale);
    }

    [Fact]
    public void ClampScale_clamps_values_below_MinScale_up_to_MinScale()
    {
        MapTransform.ClampScale(MapTransform.MinScale / 10.0).Should().Be(MapTransform.MinScale);
        MapTransform.ClampScale(-5.0).Should().Be(MapTransform.MinScale);
        MapTransform.ClampScale(0.0).Should().Be(MapTransform.MinScale);
    }

    [Fact]
    public void ClampScale_clamps_values_above_MaxScale_down_to_MaxScale()
    {
        MapTransform.ClampScale(MapTransform.MaxScale * 10.0).Should().Be(MapTransform.MaxScale);
        MapTransform.ClampScale(double.MaxValue).Should().Be(MapTransform.MaxScale);
    }

    [Fact]
    public void ClampScale_leaves_the_exact_boundary_values_unchanged()
    {
        MapTransform.ClampScale(MapTransform.MinScale).Should().Be(MapTransform.MinScale);
        MapTransform.ClampScale(MapTransform.MaxScale).Should().Be(MapTransform.MaxScale);
    }

    [Theory]
    [InlineData(120.0, 1.15)]
    [InlineData(-120.0, 1.0 / 1.15)]
    [InlineData(0.0, 1.0)]
    [InlineData(240.0, 1.15 * 1.15)]
    public void WheelZoomFactor_matches_the_design_formula(double wheelDelta, double expected)
    {
        double actual = MapTransform.WheelZoomFactor(wheelDelta);

        actual.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(120.0)]
    [InlineData(-120.0)]
    [InlineData(360.0)]
    public void ZoomAnchoredAtCursor_keeps_the_world_point_under_the_cursor_stationary(double wheelDelta)
    {
        const double canvasWidth = 1024.0;
        const double canvasHeight = 768.0;
        const double cursorScreenX = 640.0;
        const double cursorScreenY = 200.0;
        const double centerX = 1500.0;
        const double centerY = -800.0;
        const double scale = 0.5;

        double worldXBefore = MapTransform.ToWorldX(canvasWidth, cursorScreenX, centerX, scale);
        double worldYBefore = MapTransform.ToWorldY(canvasHeight, cursorScreenY, centerY, scale);

        var (newScale, newCenterX, newCenterY) = MapTransform.ZoomAnchoredAtCursor(
            canvasWidth, canvasHeight, cursorScreenX, cursorScreenY, centerX, centerY, scale, wheelDelta);

        double worldXAfter = MapTransform.ToWorldX(canvasWidth, cursorScreenX, newCenterX, newScale);
        double worldYAfter = MapTransform.ToWorldY(canvasHeight, cursorScreenY, newCenterY, newScale);

        newScale.Should().BeApproximately(MapTransform.ClampScale(scale * MapTransform.WheelZoomFactor(wheelDelta)), 1e-9);
        worldXAfter.Should().BeApproximately(worldXBefore, 1e-6);
        worldYAfter.Should().BeApproximately(worldYBefore, 1e-6);
    }

    [Fact]
    public void ZoomAnchoredAtCursor_clamps_the_new_scale_at_the_MaxScale_boundary()
    {
        var (newScale, _, _) = MapTransform.ZoomAnchoredAtCursor(
            canvasWidth: 800, canvasHeight: 600, cursorScreenX: 400, cursorScreenY: 300,
            centerX: 0, centerY: 0, scale: MapTransform.MaxScale, wheelDelta: 120.0);

        newScale.Should().Be(MapTransform.MaxScale);
    }

    [Fact]
    public void ZoomAnchoredAtCursor_clamps_the_new_scale_at_the_MinScale_boundary()
    {
        var (newScale, _, _) = MapTransform.ZoomAnchoredAtCursor(
            canvasWidth: 800, canvasHeight: 600, cursorScreenX: 400, cursorScreenY: 300,
            centerX: 0, centerY: 0, scale: MapTransform.MinScale, wheelDelta: -120.0);

        newScale.Should().Be(MapTransform.MinScale);
    }

    [Fact]
    public void WheelZoomFactor_returns_exactly_one_for_a_zero_delta()
    {
        // A zero-magnitude wheel event (for example a synthetic/coalesced event with no actual
        // motion) must be a true no-op multiplier, not merely "approximately" one.
        MapTransform.WheelZoomFactor(0.0).Should().Be(1.0);
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.001)]
    [InlineData(-10_000.0)]
    public void WheelZoomFactor_returns_a_value_strictly_less_than_one_for_any_negative_delta(double wheelDelta)
    {
        // Any negative delta (zoom out), however small in magnitude, must shrink the scale —
        // never leave it unchanged or amplify it.
        MapTransform.WheelZoomFactor(wheelDelta).Should().BeLessThan(1.0);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.001)]
    [InlineData(10_000.0)]
    public void WheelZoomFactor_returns_a_value_strictly_greater_than_one_for_any_positive_delta(double wheelDelta)
    {
        MapTransform.WheelZoomFactor(wheelDelta).Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void ZoomAnchoredAtCursor_with_zero_wheel_delta_leaves_scale_and_centre_unchanged()
    {
        const double canvasWidth = 1024.0;
        const double canvasHeight = 768.0;
        const double cursorScreenX = 700.0;
        const double cursorScreenY = 100.0;
        const double centerX = 42.0;
        const double centerY = -13.0;
        const double scale = 0.5;

        var (newScale, newCenterX, newCenterY) = MapTransform.ZoomAnchoredAtCursor(
            canvasWidth, canvasHeight, cursorScreenX, cursorScreenY, centerX, centerY, scale, wheelDelta: 0.0);

        newScale.Should().Be(scale);
        newCenterX.Should().BeApproximately(centerX, 1e-9);
        newCenterY.Should().BeApproximately(centerY, 1e-9);
    }

    [Theory]
    [InlineData(500.0)]
    [InlineData(-500.0)]
    [InlineData(10_000.0)]
    [InlineData(-10_000.0)]
    public void ZoomAnchoredAtCursor_keeps_the_cursor_anchored_even_with_extreme_wheel_deltas(double wheelDelta)
    {
        const double canvasWidth = 1024.0;
        const double canvasHeight = 768.0;
        const double cursorScreenX = 900.0;
        const double cursorScreenY = 50.0;
        const double centerX = 1_000_000.0;
        const double centerY = -500_000.0;
        const double scale = 1.0;

        double worldXBefore = MapTransform.ToWorldX(canvasWidth, cursorScreenX, centerX, scale);
        double worldYBefore = MapTransform.ToWorldY(canvasHeight, cursorScreenY, centerY, scale);

        var (newScale, newCenterX, newCenterY) = MapTransform.ZoomAnchoredAtCursor(
            canvasWidth, canvasHeight, cursorScreenX, cursorScreenY, centerX, centerY, scale, wheelDelta);

        double worldXAfter = MapTransform.ToWorldX(canvasWidth, cursorScreenX, newCenterX, newScale);
        double worldYAfter = MapTransform.ToWorldY(canvasHeight, cursorScreenY, newCenterY, newScale);

        // At these extreme deltas the raw (unclamped) multiplier would be astronomically large or
        // tiny, so the resulting scale is expected to be pinned at a clamp boundary — the
        // anchoring invariant must still hold exactly at the boundary, not only away from it.
        newScale.Should().Be(MapTransform.ClampScale(scale * MapTransform.WheelZoomFactor(wheelDelta)));
        worldXAfter.Should().BeApproximately(worldXBefore, 1e-6);
        worldYAfter.Should().BeApproximately(worldYBefore, 1e-6);
    }

    [Theory]
    [InlineData(120.0)]
    [InlineData(-120.0)]
    public void ZoomAnchoredAtCursor_keeps_the_cursor_anchored_when_already_at_the_MaxScale_boundary(double wheelDelta)
    {
        const double canvasWidth = 1024.0;
        const double canvasHeight = 768.0;
        const double cursorScreenX = 300.0;
        const double cursorScreenY = 600.0;
        const double centerX = 10.0;
        const double centerY = -5.0;

        double worldXBefore = MapTransform.ToWorldX(canvasWidth, cursorScreenX, centerX, MapTransform.MaxScale);
        double worldYBefore = MapTransform.ToWorldY(canvasHeight, cursorScreenY, centerY, MapTransform.MaxScale);

        var (newScale, newCenterX, newCenterY) = MapTransform.ZoomAnchoredAtCursor(
            canvasWidth, canvasHeight, cursorScreenX, cursorScreenY, centerX, centerY, MapTransform.MaxScale, wheelDelta);

        double worldXAfter = MapTransform.ToWorldX(canvasWidth, cursorScreenX, newCenterX, newScale);
        double worldYAfter = MapTransform.ToWorldY(canvasHeight, cursorScreenY, newCenterY, newScale);

        worldXAfter.Should().BeApproximately(worldXBefore, 1e-6);
        worldYAfter.Should().BeApproximately(worldYBefore, 1e-6);
    }

    [Theory]
    [InlineData(120.0)]
    [InlineData(-120.0)]
    public void ZoomAnchoredAtCursor_keeps_the_cursor_anchored_when_already_at_the_MinScale_boundary(double wheelDelta)
    {
        const double canvasWidth = 1024.0;
        const double canvasHeight = 768.0;
        const double cursorScreenX = 300.0;
        const double cursorScreenY = 600.0;
        const double centerX = 10.0;
        const double centerY = -5.0;

        double worldXBefore = MapTransform.ToWorldX(canvasWidth, cursorScreenX, centerX, MapTransform.MinScale);
        double worldYBefore = MapTransform.ToWorldY(canvasHeight, cursorScreenY, centerY, MapTransform.MinScale);

        var (newScale, newCenterX, newCenterY) = MapTransform.ZoomAnchoredAtCursor(
            canvasWidth, canvasHeight, cursorScreenX, cursorScreenY, centerX, centerY, MapTransform.MinScale, wheelDelta);

        double worldXAfter = MapTransform.ToWorldX(canvasWidth, cursorScreenX, newCenterX, newScale);
        double worldYAfter = MapTransform.ToWorldY(canvasHeight, cursorScreenY, newCenterY, newScale);

        worldXAfter.Should().BeApproximately(worldXBefore, 1e-6);
        worldYAfter.Should().BeApproximately(worldYBefore, 1e-6);
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(10.0)]
    public void ToWorldX_and_ToWorldY_round_trip_precisely_at_the_scale_clamp_boundaries(double scale)
    {
        const double canvasWidth = 1920.0;
        const double canvasHeight = 1080.0;
        const double worldX = 123_456.789;
        const double worldY = -987_654.321;
        const double centerX = 1_000.0;
        const double centerY = -2_000.0;

        double screenX = MapTransform.ToScreenX(canvasWidth, worldX, centerX, scale);
        double screenY = MapTransform.ToScreenY(canvasHeight, worldY, centerY, scale);

        double roundTrippedWorldX = MapTransform.ToWorldX(canvasWidth, screenX, centerX, scale);
        double roundTrippedWorldY = MapTransform.ToWorldY(canvasHeight, screenY, centerY, scale);

        // At MinScale (0.001), one world metre is only 0.001 screen pixels, so a screen-space
        // round trip loses precision proportional to 1/scale; the tolerance below reflects that
        // rather than asserting an unrealistically tight bound the floating-point math cannot meet.
        double tolerance = scale <= MapTransform.MinScale ? 1e-3 : 1e-6;
        roundTrippedWorldX.Should().BeApproximately(worldX, tolerance);
        roundTrippedWorldY.Should().BeApproximately(worldY, tolerance);
    }
}
