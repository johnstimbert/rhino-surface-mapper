namespace RhinoSurfaceMapper.Domain.Enums;

/// <summary>
/// Which record kind an active navigation target was raised from. Drives the overlay/marker
/// icon and hit-testing rules in later phases; the circular search route uses
/// <see cref="RoutePoint"/> only implicitly (it has no <c>ActiveNavTarget</c> of its own — see
/// <c>OverlayNavigationCalculator</c>).
/// </summary>
public enum NavigationTargetKind
{
    /// <summary>A user-placed <c>MapMark</c>.</summary>
    Mark,

    /// <summary>A mining <c>Deposit</c>.</summary>
    Deposit,

    /// <summary>A placed <c>Rig</c>.</summary>
    Rig,

    /// <summary>A circular search-route point, reached through the search Datum, not a marker.</summary>
    RoutePoint,
}
