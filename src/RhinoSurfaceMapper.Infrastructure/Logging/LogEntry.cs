using Microsoft.Extensions.Logging;

namespace RhinoSurfaceMapper.Infrastructure.Logging;

/// <summary>
/// A single queued log entry, captured by <see cref="RollingFileLogger"/> and drained by
/// <see cref="RollingFileLoggerProvider"/>'s background writer. Immutable so it can cross
/// the channel without further synchronisation.
/// </summary>
/// <param name="Timestamp">UTC instant the entry was logged, from the injected <see cref="Domain.Interfaces.IClock"/>.</param>
/// <param name="Level">The log level.</param>
/// <param name="Category">The logger category name (typically the fully-qualified type name of the class that logged).</param>
/// <param name="EventId">The structured event id, if any (see <c>Domain.Constants.LogEvents</c>).</param>
/// <param name="Message">The fully-formatted message text (template placeholders already substituted).</param>
/// <param name="Exception">The exception associated with the entry, if any.</param>
/// <param name="Scopes">Flattened <c>key=value</c> pairs collected from every active <c>BeginScope</c> at the time of logging.</param>
internal sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> Scopes)
{
    /// <summary>
    /// Completed by the background writer immediately after this specific entry has been
    /// written and (for <see cref="LogLevel.Critical"/>) flushed to disk — or, if the entry
    /// was dropped (bounded channel full) or could not be rendered/written, completed with
    /// <see langword="false"/> so a waiter is released promptly instead of consuming its full
    /// timeout budget for nothing. Only set for <see cref="LogLevel.Critical"/> entries, which
    /// is the only level <see cref="RollingFileLogger"/> waits on; every other level leaves
    /// this <see langword="null"/> to avoid the allocation on the hot path.
    /// </summary>
    public TaskCompletionSource<bool>? Completion { get; init; }
}
