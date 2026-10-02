using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.Domain.Exceptions;

namespace RhinoSurfaceMapper.Domain.Services;

/// <summary>
/// Maps <see cref="RouteStatus"/> to and from the exact lowercase literals
/// <c>MapperState.update_next</c>/<c>skip_next</c> write and <c>validate_map</c> accepts
/// (<c>"reached"</c>, <c>"skipped"</c>).
/// </summary>
/// <remarks>The mapping is case-sensitive, matching Python's exact string comparison.</remarks>
public static class RouteStatusCodec
{
    /// <summary>Parses a persisted status literal.</summary>
    /// <exception cref="MapValidationException">
    /// <paramref name="literal"/> is not exactly <c>"reached"</c> or <c>"skipped"</c>.
    /// </exception>
    public static RouteStatus Parse(string? literal) => literal switch
    {
        "reached" => RouteStatus.Reached,
        "skipped" => RouteStatus.Skipped,
        _ => throw new MapValidationException("Invalid route status."),
    };

    /// <summary>Returns the exact literal a <see cref="RouteStatus"/> persists as.</summary>
    public static string ToLiteral(RouteStatus status) => status switch
    {
        RouteStatus.Reached => "reached",
        RouteStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown route status."),
    };
}
