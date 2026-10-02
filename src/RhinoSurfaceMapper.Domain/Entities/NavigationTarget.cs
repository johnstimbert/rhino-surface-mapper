using RhinoSurfaceMapper.Domain.Enums;

namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// An explicit navigation target set outside the circular search route (for example, "navigate
/// to this deposit"), ported from the <c>active_nav_target</c> dictionary
/// (<c>{"type": ..., "name": ..., "x": ..., "y": ...}</c>) in <c>MapperState</c>.
/// </summary>
/// <remarks>
/// While a <see cref="MapSession.ActiveNavTarget"/> is set, <c>OverlayNavigationCalculator</c>
/// steers toward it in preference to the circular search route or the return-to-pause point
/// (see <c>MapperState.overlay_navigation</c>'s priority order).
/// </remarks>
/// <param name="Kind">Which record kind this target was raised from.</param>
/// <param name="Name">Display name shown by the overlay (for example <c>"[Marker] Alfa"</c>).</param>
/// <param name="X">Local easting in metres of the target, relative to the map centre.</param>
/// <param name="Y">Local northing in metres of the target, relative to the map centre.</param>
public sealed record NavigationTarget(NavigationTargetKind Kind, string Name, double X, double Y);
