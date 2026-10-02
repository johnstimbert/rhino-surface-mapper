using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Domain.Constants;

namespace RhinoSurfaceMapper.Desktop.Hosting;

/// <summary>
/// Source-generated, high-performance log call sites for <see cref="TelemetryHostedService"/>
/// and <see cref="EliteDangerousProcessCheck"/>, per the design's "High-performance logging"
/// rule — this is the 50 ms hot loop, so every call site here must stay allocation-free when its
/// level is disabled (the category defaults to <c>Warning</c>; see <c>appsettings.json</c>).
/// </summary>
internal static partial class TelemetryLogMessages
{
    /// <summary>Logged after one accepted telemetry sample was applied to the live <c>MapSession</c>.</summary>
    [LoggerMessage(
        EventId = LogEvents.TelemetryStatusProcessed,
        Level = LogLevel.Debug,
        Message = "Processed telemetry sample: System={System} Body={Body} LocationChanged={LocationChanged}")]
    public static partial void TelemetryStatusProcessed(this ILogger logger, string system, string body, bool locationChanged);

    /// <summary>Logged when reading or parsing <c>Status.json</c> failed on one poll; the loop retries on the next tick.</summary>
    [LoggerMessage(
        EventId = LogEvents.TelemetryStatusReadFailed,
        Level = LogLevel.Warning,
        Message = "Failed to read Status.json at '{Path}'")]
    public static partial void TelemetryStatusReadFailed(this ILogger logger, Exception exception, string path);

    /// <summary>Logged only on the Running/NotRunning transition, never on every throttled check.</summary>
    [LoggerMessage(
        EventId = LogEvents.TelemetryGameProcessStateChanged,
        Level = LogLevel.Information,
        Message = "Elite Dangerous process running state changed to {IsRunning}")]
    public static partial void GameProcessStateChanged(this ILogger logger, bool isRunning);

    /// <summary>
    /// Logged when applying an accepted sample (mutate/log-scope/notify) threw; the poll loop
    /// catches this at the call site and continues on the next tick rather than dying silently.
    /// </summary>
    [LoggerMessage(
        EventId = LogEvents.TelemetryApplyFailed,
        Level = LogLevel.Error,
        Message = "Failed to apply an accepted telemetry sample")]
    public static partial void TelemetryApplyFailed(this ILogger logger, Exception exception);

    /// <summary>
    /// Logged once when the game process stops running, after live-session telemetry and any
    /// pending map transition have been cleared.
    /// </summary>
    [LoggerMessage(
        EventId = LogEvents.TelemetryWentOffline,
        Level = LogLevel.Information,
        Message = "Elite Dangerous process stopped; cleared live telemetry and any pending map transition")]
    public static partial void TelemetryWentOffline(this ILogger logger);
}
