using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Domain.Constants;

namespace RhinoSurfaceMapper.UI.Components.Map;

/// <summary>
/// Source-generated, high-performance log call sites for <see cref="MapCanvas"/>'s JS interop
/// lifecycle, per the design's "High-performance logging" rule (<c>[LoggerMessage]</c> for every
/// hot/host call site) — kept consistent with <c>Desktop.Logging.AppLogMessages</c> and
/// <c>Desktop.Hosting.TelemetryLogMessages</c> rather than using the ad-hoc
/// <c>ILogger.LogDebug(EventId, ...)</c> overloads.
/// </summary>
internal static partial class MapCanvasLogMessages
{
    /// <summary>Logged once <see cref="MapCanvas"/>'s JS interop module has finished loading and is ready to receive scene pushes.</summary>
    [LoggerMessage(
        EventId = LogEvents.MapCanvasInitialized,
        Level = LogLevel.Debug,
        Message = "Map canvas JS module initialised.")]
    public static partial void MapCanvasInitialized(this ILogger logger);

    /// <summary>
    /// Logged when <see cref="MapCanvas"/>'s JS interop call failed (for example, the WebView2
    /// module was disposed concurrently with a pending push, or failed to load at all).
    /// </summary>
    [LoggerMessage(
        EventId = LogEvents.MapCanvasInteropFailed,
        Level = LogLevel.Error,
        Message = "Failed to initialise the map canvas JS module.")]
    public static partial void MapCanvasInteropFailed(this ILogger logger, Exception exception);
}
