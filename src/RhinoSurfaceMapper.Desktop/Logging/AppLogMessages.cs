using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Domain.Constants;

namespace RhinoSurfaceMapper.Desktop.Logging;

/// <summary>
/// Source-generated, high-performance log call sites for the Desktop host's lifecycle and
/// global-exception-handler logging, per the design's "High-performance logging" rule
/// (<c>[LoggerMessage]</c> for every hot/host call site).
/// </summary>
internal static partial class AppLogMessages
{
    /// <summary>Logged once the generic host has finished building, before it is run.</summary>
    [LoggerMessage(
        EventId = LogEvents.ApplicationStarting,
        Level = LogLevel.Information,
        Message = "Starting {ApplicationName} {Version} on {Runtime}")]
    public static partial void ApplicationStarting(this ILogger logger, string applicationName, string version, string runtime);

    /// <summary>Logged once the host has started, recording the resolved portable paths (N1/N5).</summary>
    [LoggerMessage(
        EventId = LogEvents.ApplicationStarted,
        Level = LogLevel.Information,
        Message = "Started. BaseDirectory={BaseDirectory} MapsDirectory={MapsDirectory} LogsDirectory={LogsDirectory} OptionsPath={OptionsPath}")]
    public static partial void ApplicationStarted(this ILogger logger, string baseDirectory, string mapsDirectory, string logsDirectory, string optionsPath);

    /// <summary>Logged when the host begins a normal shutdown.</summary>
    [LoggerMessage(
        EventId = LogEvents.ApplicationStopping,
        Level = LogLevel.Information,
        Message = "Stopping")]
    public static partial void ApplicationStopping(this ILogger logger);

    /// <summary>Logged once the host has stopped and all services were disposed.</summary>
    [LoggerMessage(
        EventId = LogEvents.ApplicationStopped,
        Level = LogLevel.Information,
        Message = "Stopped")]
    public static partial void ApplicationStopped(this ILogger logger);

    /// <summary>
    /// Logged by each of the three global exception handlers installed in <c>App.xaml.cs</c>
    /// (<c>DispatcherUnhandledException</c>, <c>AppDomain.UnhandledException</c>,
    /// <c>TaskScheduler.UnobservedTaskException</c>) before the process reacts.
    /// </summary>
    [LoggerMessage(
        EventId = LogEvents.UnhandledException,
        Level = LogLevel.Critical,
        Message = "Unhandled exception reached the global handler from {Source}")]
    public static partial void UnhandledException(this ILogger logger, Exception exception, string source);
}
