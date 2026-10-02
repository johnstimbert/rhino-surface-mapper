namespace RhinoSurfaceMapper.UI.Components.Map;

/// <summary>
/// The authoritative .NET copy of the map canvas' world↔screen transform, ported from the
/// Python <c>MapView</c> painter's own transform. Both the Razor/.NET side and
/// <c>wwwroot/map-canvas.js</c> implement the identical formulas from the same constants (design:
/// "Map canvas" — "implemented in both places from the same constants"); this type is the side
/// used for hit-testing and automated tests, per the design's explicit call-out that ".NET copy
/// is the authority".
/// </summary>
/// <remarks>
/// All angles/metres here are already in the session's local projected metre space (see
/// <c>PlanetGeometry</c>); this type knows nothing about latitude/longitude.
/// </remarks>
public static class MapTransform
{
    /// <summary>Initial zoom scale (screen pixels per world metre) a freshly opened map starts at.</summary>
    public const double InitialScale = 0.08;

    /// <summary>Inclusive lower bound every zoom scale is clamped to.</summary>
    public const double MinScale = 0.001;

    /// <summary>Inclusive upper bound every zoom scale is clamped to.</summary>
    public const double MaxScale = 10.0;

    /// <summary>
    /// Base of the exponential mouse-wheel zoom factor: one wheel "notch" (120 delta units) scales
    /// by exactly this factor; fractional notches (precision trackpads/wheels) scale
    /// proportionally via <see cref="WheelZoomFactor"/>.
    /// </summary>
    public const double WheelZoomFactorBase = 1.15;

    /// <summary>
    /// Converts a world X coordinate (metres) to a screen X coordinate (pixels), ported from the
    /// design's <c>screenX = width/2 + (worldX - centerX) * scale</c>.
    /// </summary>
    /// <param name="canvasWidth">Canvas width in pixels.</param>
    /// <param name="worldX">World X coordinate, in metres.</param>
    /// <param name="centerX">World X coordinate currently centred on screen, in metres.</param>
    /// <param name="scale">Current zoom scale, in screen pixels per world metre.</param>
    public static double ToScreenX(double canvasWidth, double worldX, double centerX, double scale) =>
        (canvasWidth / 2.0) + ((worldX - centerX) * scale);

    /// <summary>
    /// Converts a world Y coordinate (metres) to a screen Y coordinate (pixels), ported from the
    /// design's <c>screenY = height/2 - (worldY - centerY) * scale</c>. The subtraction (rather
    /// than addition) flips the axis because screen Y increases downward while world Y
    /// (northing) increases upward.
    /// </summary>
    /// <param name="canvasHeight">Canvas height in pixels.</param>
    /// <param name="worldY">World Y coordinate, in metres.</param>
    /// <param name="centerY">World Y coordinate currently centred on screen, in metres.</param>
    /// <param name="scale">Current zoom scale, in screen pixels per world metre.</param>
    public static double ToScreenY(double canvasHeight, double worldY, double centerY, double scale) =>
        (canvasHeight / 2.0) - ((worldY - centerY) * scale);

    /// <summary>
    /// Converts a world point to a screen point in one call. Convenience wrapper over
    /// <see cref="ToScreenX"/>/<see cref="ToScreenY"/>.
    /// </summary>
    public static (double ScreenX, double ScreenY) ToScreen(
        double canvasWidth, double canvasHeight, double worldX, double worldY, double centerX, double centerY, double scale) =>
        (ToScreenX(canvasWidth, worldX, centerX, scale), ToScreenY(canvasHeight, worldY, centerY, scale));

    /// <summary>
    /// Converts a screen X coordinate back to a world X coordinate — the exact inverse of
    /// <see cref="ToScreenX"/>, used to resolve "the world point under the cursor" for
    /// hit-testing and anchored zoom.
    /// </summary>
    public static double ToWorldX(double canvasWidth, double screenX, double centerX, double scale) =>
        centerX + ((screenX - (canvasWidth / 2.0)) / scale);

    /// <summary>
    /// Converts a screen Y coordinate back to a world Y coordinate — the exact inverse of
    /// <see cref="ToScreenY"/>.
    /// </summary>
    public static double ToWorldY(double canvasHeight, double screenY, double centerY, double scale) =>
        centerY - ((screenY - (canvasHeight / 2.0)) / scale);

    /// <summary>
    /// Clamps a zoom scale to the inclusive <see cref="MinScale"/>–<see cref="MaxScale"/> range.
    /// </summary>
    public static double ClampScale(double scale) => Math.Clamp(scale, MinScale, MaxScale);

    /// <summary>
    /// Computes the exponential zoom multiplier for one mouse-wheel event, ported from the
    /// design's <c>1.15^(delta/120)</c>. The caller must already have normalised its input to
    /// this convention: a positive <paramref name="normalizedDelta"/> zooms in, a negative value
    /// zooms out — <em>not</em> a raw browser <c>WheelEvent.deltaY</c>, which is negative for
    /// scroll-away-from-user (zoom in) in every major browser. <c>map-canvas.js</c> negates
    /// <c>deltaY</c> itself before applying its own independent copy of this formula; this
    /// method is never called with a raw <c>deltaY</c> today, but a future caller passing one
    /// through unnormalised would silently invert every zoom direction.
    /// </summary>
    /// <param name="normalizedDelta">
    /// Wheel delta already normalised to "positive = zoom in" and expressed in the browser's
    /// 120-units-per-notch convention; fractional values (precision trackpads/wheels) scale
    /// proportionally.
    /// </param>
    /// <returns>A multiplier to apply to the current scale, before clamping with <see cref="ClampScale"/>.</returns>
    public static double WheelZoomFactor(double normalizedDelta) => Math.Pow(WheelZoomFactorBase, normalizedDelta / 120.0);

    /// <summary>
    /// Computes the new scale and map centre that keep the world point currently under the
    /// cursor stationary on screen while zooming, ported from the design's "wheel factor...
    /// anchored under the cursor" rule.
    /// </summary>
    /// <param name="canvasWidth">Canvas width in pixels.</param>
    /// <param name="canvasHeight">Canvas height in pixels.</param>
    /// <param name="cursorScreenX">Cursor X position in pixels, relative to the canvas.</param>
    /// <param name="cursorScreenY">Cursor Y position in pixels, relative to the canvas.</param>
    /// <param name="centerX">World X coordinate currently centred on screen, in metres, before zooming.</param>
    /// <param name="centerY">World Y coordinate currently centred on screen, in metres, before zooming.</param>
    /// <param name="scale">Current zoom scale, before zooming.</param>
    /// <param name="wheelDelta">Raw wheel delta in the browser's 120-units-per-notch convention.</param>
    /// <returns>
    /// The new, clamped scale and the new map centre such that the world point under
    /// (<paramref name="cursorScreenX"/>, <paramref name="cursorScreenY"/>) is unchanged after
    /// applying both.
    /// </returns>
    public static (double Scale, double CenterX, double CenterY) ZoomAnchoredAtCursor(
        double canvasWidth,
        double canvasHeight,
        double cursorScreenX,
        double cursorScreenY,
        double centerX,
        double centerY,
        double scale,
        double wheelDelta)
    {
        double worldXUnderCursor = ToWorldX(canvasWidth, cursorScreenX, centerX, scale);
        double worldYUnderCursor = ToWorldY(canvasHeight, cursorScreenY, centerY, scale);

        double newScale = ClampScale(scale * WheelZoomFactor(wheelDelta));

        // Solve for the new centre that keeps worldXUnderCursor/worldYUnderCursor projecting
        // back onto the same cursor pixel at the new scale: invert ToScreenX/ToScreenY for
        // centerX/centerY instead of worldX/worldY.
        double newCenterX = worldXUnderCursor - ((cursorScreenX - (canvasWidth / 2.0)) / newScale);
        double newCenterY = worldYUnderCursor + ((cursorScreenY - (canvasHeight / 2.0)) / newScale);

        return (newScale, newCenterX, newCenterY);
    }
}
