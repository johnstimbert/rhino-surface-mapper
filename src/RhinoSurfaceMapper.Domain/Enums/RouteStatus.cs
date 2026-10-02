namespace RhinoSurfaceMapper.Domain.Enums;

/// <summary>
/// Outcome of one circular-search route point, ported from the <c>'reached'</c>/<c>'skipped'</c>
/// string literals <c>MapperState.update_next</c>/<c>skip_next</c> write into
/// <c>route_history</c> entries.
/// </summary>
public enum RouteStatus
{
    /// <summary>
    /// The commander approached the point within <see cref="Constants.MapperConstants.RouteArrivalMetres"/>
    /// and the route auto-advanced. Persisted as <c>"reached"</c>.
    /// </summary>
    Reached,

    /// <summary>
    /// The point was abandoned explicitly via <c>SearchRouteCalculator.SkipNext</c> without the
    /// commander approaching it. Persisted as <c>"skipped"</c>.
    /// </summary>
    Skipped,
}
