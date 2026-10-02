namespace RhinoSurfaceMapper.Domain.Constants;

/// <summary>
/// Stable numeric event-id catalogue for every log call site in the application,
/// organised in per-subsystem bands so that log analysis can key off a number instead of
/// parsing message text (design: "Logging design" §3, "EventId catalogue").
/// </summary>
/// <remarks>
/// <para>
/// Values are plain <see cref="int"/> constants rather than
/// <c>Microsoft.Extensions.Logging.EventId</c> values because <c>Domain</c> has no
/// third-party or framework dependencies by design (see the "Dependency rule" in
/// <c>DESIGN_DOTNET_PORT.md</c>). Call sites that use <c>[LoggerMessage]</c> pass these
/// constants directly to the attribute's <c>EventId</c> parameter, which is itself a plain
/// <see langword="int"/>; callers that need a <c>Microsoft.Extensions.Logging.EventId</c>
/// struct construct one at the call site with <c>new EventId(LogEvents.X, nameof(LogEvents.X))</c>.
/// </para>
/// <para>Only the Phase 0 lifecycle ids are defined so far. The remaining bands are reserved
/// here so later phases allocate ids from the correct range without renumbering:</para>
/// <list type="bullet">
///   <item><description>1000–1999: application lifecycle (startup, shutdown, host faults)</description></item>
///   <item><description>2000–2999: telemetry (status/journal reads, SRV detection)</description></item>
///   <item><description>3000–3999: map session (open/save/new, generation changes)</description></item>
///   <item><description>4000–4999: persistence (JSON map/preferences/cache I/O)</description></item>
///   <item><description>5000–5999: radar (pulse ticks, coverage accumulation)</description></item>
///   <item><description>6000–6999: steering / input injection</description></item>
///   <item><description>7000–7999: market (Spansh/INARA acquisition, ranking)</description></item>
///   <item><description>8000–8999: UI / native interop (overlay, WebView2, P/Invoke)</description></item>
/// </list>
/// </remarks>
public static class LogEvents
{
    /// <summary>The host finished building the generic host and is about to run.</summary>
    public const int ApplicationStarting = 1000;

    /// <summary>The host completed startup and logged the resolved application paths/version.</summary>
    public const int ApplicationStarted = 1001;

    /// <summary>The host is shutting down in response to a normal exit request.</summary>
    public const int ApplicationStopping = 1002;

    /// <summary>The host completed shutdown and all services were disposed.</summary>
    public const int ApplicationStopped = 1003;

    /// <summary>
    /// An exception reached one of the three global handlers
    /// (<c>DispatcherUnhandledException</c>, <c>AppDomain.UnhandledException</c>,
    /// <c>TaskScheduler.UnobservedTaskException</c>) instead of being handled locally.
    /// </summary>
    public const int UnhandledException = 1004;

    /// <summary>The rolling-file logger provider dropped a log entry because its bounded channel was full.</summary>
    public const int LogEntryDropped = 1005;

    /// <summary>
    /// The rolling-file logger provider's background writer could not format or write a log
    /// entry (for example, a scope or formatted-state value whose <c>ToString()</c> threw) and
    /// substituted a placeholder line instead of losing the entry silently or crashing the
    /// writer loop (AGENTS.md §6 waiver — see <c>RollingFileLoggerProvider.WriteLoopAsync</c>).
    /// </summary>
    public const int LogEntryCorrupted = 1006;
}
