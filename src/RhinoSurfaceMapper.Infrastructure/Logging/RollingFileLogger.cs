using Microsoft.Extensions.Logging;

namespace RhinoSurfaceMapper.Infrastructure.Logging;

/// <summary>
/// <see cref="ILogger"/> implementation handed out by <see cref="RollingFileLoggerProvider"/>.
/// Captures the active scope chain as flattened <c>key=value</c> pairs and enqueues a
/// <see cref="LogEntry"/> onto the provider's bounded channel; it never touches disk itself.
/// </summary>
internal sealed class RollingFileLogger(string category, RollingFileLoggerProvider provider) : ILogger
{
    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        provider.ScopeProvider?.Push(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) =>
        // Level filtering is applied upstream by the Microsoft.Extensions.Logging filter
        // pipeline (bound from the "Logging:LogLevel" configuration section), so the
        // provider itself accepts every level it is asked to write.
        logLevel != LogLevel.None;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = formatter(state, exception);
        var scopes = CollectScopes();

        // A Critical entry gets a completion handle so this call can wait, with a bounded
        // timeout, for the background writer to actually persist (and flush) it before
        // returning — see the remarks on LogEntry.Completion and RollingFileLoggerProvider's
        // writer loop. This is what makes "flush immediately on Critical" (design §"Logging
        // design") a real guarantee for the caller rather than best-effort: a global exception
        // handler logging Critical immediately before the process might terminate can rely on
        // this call not returning until the record is durable, or until CriticalFlushTimeout
        // has elapsed — whichever comes first, so a wedged writer can never hang the caller.
        var completion = logLevel == LogLevel.Critical
            ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        var entry = new LogEntry(provider.Clock.UtcNow, logLevel, category, eventId, message, exception, scopes)
        {
            Completion = completion,
        };

        provider.Enqueue(entry);

        if (completion is not null)
        {
            completion.Task.Wait(RollingFileLoggerProvider.CriticalFlushTimeout);
        }
    }

    /// <summary>
    /// Flattens every active <c>BeginScope</c> state into <c>key=value</c> pairs, outermost
    /// scope first. Only states that expose structured values (anonymous objects, dictionaries,
    /// or anything implementing <see cref="IEnumerable{T}"/> of <see cref="KeyValuePair{TKey,TValue}"/>,
    /// which is what <c>ILogger.BeginScope(new { Map = ..., Generation = ... })</c> produces via
    /// the default formatted log values converter) contribute entries; a plain string scope is
    /// rendered as a single <c>scope=value</c> pair.
    /// </summary>
    private List<KeyValuePair<string, object?>> CollectScopes()
    {
        var scopes = new List<KeyValuePair<string, object?>>();

        provider.ScopeProvider?.ForEachScope(
            (state, list) =>
            {
                switch (state)
                {
                    case IEnumerable<KeyValuePair<string, object?>> pairs:
                        list.AddRange(pairs.Where(p => !string.Equals(p.Key, "{OriginalFormat}", StringComparison.Ordinal)));
                        break;
                    case null:
                        break;
                    default:
                        list.Add(new KeyValuePair<string, object?>("scope", state));
                        break;
                }
            },
            scopes);

        return scopes;
    }
}
