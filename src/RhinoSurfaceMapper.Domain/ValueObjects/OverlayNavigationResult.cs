namespace RhinoSurfaceMapper.Domain.ValueObjects;

/// <summary>
/// Overlay guidance text/colours for the current navigation target, ported from the 5-tuple
/// <c>MapperState.overlay_navigation</c> returns
/// (<c>heading, distance, heading_color, distance_color, target_name</c>).
/// </summary>
/// <param name="Heading">
/// Bearing text with directional arrows, for example <c>"000°"</c>, <c>"&lt;&lt;&lt; 350°"</c>
/// or <c>"010° »»»"</c>; <c>"—"</c> when navigation is unavailable.
/// </param>
/// <param name="Distance">Distance text, for example <c>"3500 m"</c>; <c>"—"</c> when unavailable.</param>
/// <param name="HeadingColor">
/// Hex colour for the heading text: <c>#00cc44</c> within ±2°, <c>#ffd21c</c> within ±8°,
/// <c>#ff3030</c> beyond, <c>#888888</c> when navigation is unavailable.
/// </param>
/// <param name="DistanceColor">
/// Hex/named colour for the distance text: alternates <c>white</c>/<c>#202020</c> every
/// <see cref="Constants.MapperConstants.OverlayBlinkIntervalSeconds"/> while inside
/// <see cref="Constants.MapperConstants.OverlayBlinkDistanceMetres"/>, otherwise steady
/// <c>white</c>.
/// </param>
/// <param name="TargetName">Display name of the active target, or empty when none is set.</param>
public sealed record OverlayNavigationResult(
    string Heading,
    string Distance,
    string HeadingColor,
    string DistanceColor,
    string TargetName)
{
    /// <summary>The canonical "navigation unavailable" result for a named target with no bearing data.</summary>
    public static OverlayNavigationResult Unavailable(string targetName) =>
        new("—", "—", "#888888", "white", targetName);
}
