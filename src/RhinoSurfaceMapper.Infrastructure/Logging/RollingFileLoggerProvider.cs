using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Infrastructure.Logging;

/// <summary>
/// Durable rolling-file <see cref="ILoggerProvider"/>. Log calls never touch disk directly:
/// <see cref="RollingFileLogger"/> enqueues a <see cref="LogEntry"/> onto a bounded
/// <see cref="Channel{T}"/>, and a single background task drains it, so hot loops (the
/// telemetry/radar/steering threads described in the design's "Threading and concurrency
/// model") never block on I/O.
/// </summary>
/// <remarks>
/// <para>
/// The channel is bounded at 10 000 entries with <see cref="BoundedChannelFullMode.DropWrite"/>:
/// once full, new entries are discarded rather than blocking the caller or growing without
/// bound. Dropped entries are counted and reported as a <c>Warning</c> log line (event id
/// <see cref="LogEvents.LogEntryDropped"/>) the next time the periodic flush tick observes a
/// change in the counter.
/// </para>
/// <para>
/// <b>Why a separate <c>_pendingCount</c> counter exists:</b> under
/// <see cref="BoundedChannelFullMode.DropWrite"/>, <see cref="ChannelWriter{T}.TryWrite"/>
/// returns <see langword="true"/> even when the channel is full and the item is silently
/// discarded — the <see langword="bool"/> reports whether the <i>call</i> completed, not
/// whether the item was actually enqueued, so it cannot be used to detect a drop.
/// <see cref="Enqueue"/> instead mirrors the channel's own capacity with an
/// <see cref="Interlocked"/>-maintained pending count, incremented before handing an entry to
/// the channel and decremented by the background writer after it dequeues one; a pending
/// count that would exceed capacity is treated as a drop before the channel is touched at
/// all, with the channel's own <c>DropWrite</c> behaviour kept only as a defensive backstop.
/// </para>
/// <para>
/// Flushing happens on a 2-second timer; promptly (not merely "eventually, same as every
/// other entry") for any <see cref="LogLevel.Critical"/> entry — the background writer
/// flushes immediately after writing a Critical entry specifically, and
/// <see cref="RollingFileLogger"/> waits, with a bounded timeout
/// (<see cref="CriticalFlushTimeout"/>), on that entry's own <see cref="LogEntry.Completion"/>
/// before returning from the logging call, so a caller logging Critical immediately before a
/// possible crash can rely on the record being durable by the time the call returns (or on
/// the bounded timeout having elapsed, never an indefinite hang) — and once more during
/// <see cref="IHostApplicationLifetime.ApplicationStopping"/> —
/// wired up by <see cref="LoggingShutdownFlusherHostedService"/>, not by this class itself,
/// so no log is lost on a clean shutdown.
/// </para>
/// <para>
/// This provider deliberately does <b>not</b> take an <see cref="IHostApplicationLifetime"/>
/// constructor dependency. The default <c>ApplicationLifetime</c> implementation depends on
/// <c>ILogger&lt;ApplicationLifetime&gt;</c>, which depends on <see cref="ILoggerFactory"/>,
/// which depends on every registered <see cref="ILoggerProvider"/> — including this one. A
/// provider that depends on <see cref="IHostApplicationLifetime"/> therefore creates a
/// dependency cycle that the container resolves by repeatedly re-entering this constructor
/// instead of throwing, hanging <c>HostApplicationBuilder.Build()</c>. The
/// <see cref="LoggingShutdownFlusherHostedService"/> breaks the cycle: hosted services are
/// resolved after the logging pipeline is fully built, at <c>IHost.StartAsync</c> time.
/// </para>
/// </remarks>
[ProviderAlias("RollingFile")]
public sealed class RollingFileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private const int ChannelCapacity = 10_000;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Upper bound on how long <see cref="RollingFileLogger"/> will block a Critical-level
    /// caller waiting for its entry to be durably written and flushed (see
    /// <see cref="LogEntry.Completion"/>). Bounded deliberately: a global exception handler
    /// must never hang indefinitely even if the writer is itself wedged (e.g. on a stalled
    /// disk), since that would turn a logging best-effort guarantee into an application hang
    /// during the exact moment (a crash) where responsiveness matters most.
    /// </summary>
    internal static readonly TimeSpan CriticalFlushTimeout = TimeSpan.FromSeconds(2);

    private readonly Channel<LogEntry> _channel;
    private readonly LogFileRoller _roller;
    private readonly Task _writerTask;
    private readonly PeriodicTimer _flushTimer;
    private readonly Task _flushLoopTask;
    private readonly CancellationTokenSource _shutdown = new();

    private long _pendingCount;
    private long _droppedCount;
    private long _lastReportedDroppedCount;
    private long _corruptedCount;
    private long _lastReportedCorruptedCount;
    private bool _disposed;

    /// <summary>
    /// Creates the provider, opening (or resuming) today's log file immediately so a log
    /// line can be written as soon as the host starts.
    /// </summary>
    /// <param name="options">Rolling/retention configuration bound from <c>Logging:File</c>.</param>
    /// <param name="appPaths">Resolves the portable base directory for a relative <see cref="RollingFileLoggerOptions.Directory"/>.</param>
    /// <param name="clock">Clock used for file timestamps and roll-on-date-change detection.</param>
    public RollingFileLoggerProvider(
        IOptions<RollingFileLoggerOptions> options,
        IAppPaths appPaths,
        IClock clock)
    {
        Clock = clock;

        var configured = options.Value;
        var directory = Path.IsPathRooted(configured.Directory)
            ? configured.Directory
            : Path.Combine(appPaths.BaseDirectory, configured.Directory);

        _roller = new LogFileRoller(directory, configured.MaxFileBytes, configured.RetentionDays, configured.MaxTotalBytes, clock);

        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

        _writerTask = Task.Run(() => WriteLoopAsync(_shutdown.Token));
        _flushTimer = new PeriodicTimer(FlushInterval);
        _flushLoopTask = Task.Run(() => FlushLoopAsync(_shutdown.Token));
    }

    /// <summary>Gets the clock used to timestamp entries — exposed so <see cref="RollingFileLogger"/> shares the same instance.</summary>
    internal IClock Clock { get; }

    /// <summary>Gets the external scope provider supplied by the logging infrastructure, or <see langword="null"/> before one is attached.</summary>
    internal IExternalScopeProvider? ScopeProvider { get; private set; }

    /// <summary>Gets the full path of the file currently being written to. Exposed for diagnostics and tests.</summary>
    public string CurrentFilePath => _roller.CurrentFilePath;

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(categoryName, this);

    /// <inheritdoc />
    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => ScopeProvider = scopeProvider;

    /// <summary>
    /// Enqueues <paramref name="entry"/> for the background writer. Called only by
    /// <see cref="RollingFileLogger"/>. A full channel drops the entry and increments the
    /// drop counter rather than blocking the logging call site.
    /// </summary>
    /// <remarks>
    /// <see cref="BoundedChannelFullMode.DropWrite"/> is required by the design so the
    /// channel itself never blocks a producer, but <see cref="ChannelWriter{T}.TryWrite"/>
    /// returns <see langword="true"/> under that mode even when the item is silently
    /// discarded — the channel reports success for the <i>call</i>, not for whether the item
    /// was actually enqueued. Relying on that return value to detect a drop (as an earlier
    /// version of this method did) therefore can never detect a drop. A separate
    /// <see cref="_pendingCount"/> counter mirrors the channel's own capacity bookkeeping so
    /// a drop can be recognised and counted <i>before</i> handing the entry to the channel,
    /// while the channel's <c>DropWrite</c> mode remains the defensive backstop.
    /// </remarks>
    internal void Enqueue(LogEntry entry)
    {
        if (Interlocked.Increment(ref _pendingCount) > ChannelCapacity)
        {
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _droppedCount);
            entry.Completion?.TrySetResult(false);
            return;
        }

        if (!_channel.Writer.TryWrite(entry))
        {
            // Defensive only: with the pre-check above this should be unreachable in
            // practice, but if the channel ever rejects an entry we still must not lose the
            // drop from the counter or leave the pending count permanently inflated.
            Interlocked.Decrement(ref _pendingCount);
            Interlocked.Increment(ref _droppedCount);
            entry.Completion?.TrySetResult(false);
            return;
        }

        // Deliberately does NOT flush here for Critical entries: this runs on the *calling*
        // thread immediately after handing the entry to the channel, before the background
        // writer has necessarily dequeued it — flushing here would only flush whatever the
        // writer had already drained previously, not this entry. The real "flush this entry
        // promptly" guarantee lives in WriteLoopAsync (flush right after writing a Critical
        // entry) combined with RollingFileLogger.Log waiting, with a bounded timeout, on
        // entry.Completion.
    }

    /// <summary>
    /// Reports any newly dropped or corrupted entries and flushes the active file to disk.
    /// Called by <see cref="LoggingShutdownFlusherHostedService"/> on <c>ApplicationStopping</c>
    /// so logs are durable before the rest of the shutdown sequence runs, and internally by
    /// both <see cref="FlushLoopAsync"/> (every periodic tick) and <see cref="Dispose"/>, so
    /// the exact same sequence runs from every "must flush now" call site rather than being
    /// re-implemented at each one.
    /// </summary>
    internal void FlushAndReportDrops()
    {
        ReportDroppedEntries();
        ReportCorruptedEntries();
        _roller.Flush();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Idempotent by design: this provider is registered in DI under two service types
    /// (<c>RollingFileLoggerProvider</c> itself and <c>ILoggerProvider</c>, via a factory
    /// that resolves the same singleton instance), and the built-in DI container tracks each
    /// resolved service type for disposal independently even when both resolve to the same
    /// object — so the container can and does call <see cref="Dispose"/> on this instance
    /// twice during host shutdown. A guard flag makes the second call a no-op instead of
    /// throwing <see cref="ObjectDisposedException"/> from an already-disposed
    /// <see cref="CancellationTokenSource"/>.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _channel.Writer.TryComplete();
        _shutdown.Cancel();

        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(5));
            _flushLoopTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Both loops observe the cancellation token cooperatively; a timeout here means a
            // slow disk, not a bug. Proceed to flush/dispose rather than hang application shutdown.
        }

        FlushAndReportDrops();
        _flushTimer.Dispose();
        _roller.Dispose();
        _shutdown.Dispose();
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _pendingCount);
                WriteEntrySafely(entry);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown: Dispose() cancels _shutdown before completing the channel drain.
        }
    }

    /// <summary>
    /// Formats and writes a single entry — flushing immediately afterwards if it is
    /// <see cref="LogLevel.Critical"/>, then signalling <see cref="LogEntry.Completion"/> —
    /// with a single entry's failure degraded to a counted placeholder line rather than ever
    /// allowing an exception to escape and terminate this provider's one and only writer task.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is a deliberate, documented waiver of AGENTS.md §6 ("catch specific exception
    /// types, never a bare <c>catch</c>").</b> <see cref="LogLineFormatter.Format(LogEntry)"/> ultimately
    /// calls <see cref="object.ToString"/> on caller-supplied scope and formatted-state values
    /// (see the design's "Structured context" scopes, e.g. <c>{ map, system, body, generation }</c>
    /// built from domain data by callers this sink does not control) — from this sink's point
    /// of view that is untrusted code, since any type can override <c>ToString()</c> to throw.
    /// <see cref="WriteLoopAsync"/> runs on a single background task for the entire process's
    /// logging; if an unhandled exception terminated it, every subsequent log entry for the
    /// remainder of the process's lifetime — including ones logged long after the offending
    /// value was gone — would be silently and permanently lost with no indication whatsoever.
    /// That outcome is strictly worse than losing (or degrading) the one malformed entry, so a
    /// broad <c>catch (Exception)</c> is sanctioned at this single site as the sink's last line
    /// of defence. It is scoped as tightly as possible: it wraps only one entry's format-and-write,
    /// the failure is counted and reported (via <see cref="LogEvents.LogEntryCorrupted"/>,
    /// through the same periodic/Dispose reporting path as the drop counter) rather than
    /// swallowed silently, and the exception is never rethrown.
    /// </para>
    /// <para>
    /// The placeholder line built in the <c>catch</c> block deliberately never touches the
    /// failed entry's <see cref="LogEntry.Message"/> or <see cref="LogEntry.Scopes"/> again
    /// (re-invoking the same untrusted values could throw a second time) — only
    /// <see cref="LogEntry.Category"/> (a plain <see cref="string"/>) and
    /// <see cref="LogEntry.Level"/> (an <see langword="enum"/>), neither of which can throw
    /// from formatting, plus <c>ex.GetType().FullName</c> (metadata, not a virtual call). The
    /// placeholder is written via <see cref="WriteLineSafely"/> rather than a direct
    /// <c>_roller.Write</c> call: <see cref="LogFileRoller.Write"/> only degrades
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> internally, which
    /// does not cover an <see cref="ObjectDisposedException"/> or <see cref="NullReferenceException"/>
    /// reachable if <see cref="Dispose"/> has, in the meantime, joined this task's best-effort
    /// wait, timed out, and disposed the roller out from under it — an unguarded second write
    /// here would otherwise let exactly that corner case fault this writer task, defeating the
    /// very invariant this method exists to guarantee.
    /// </para>
    /// </remarks>
    private void WriteEntrySafely(LogEntry entry)
    {
        try
        {
            var text = LogLineFormatter.Format(entry, out var hadUnprintableValue);
            _roller.Write(text);

            if (hadUnprintableValue)
            {
                // The entry itself was not lost — LogLineFormatter.SafeToString already
                // degraded the one bad scope value to a placeholder token in place — but the
                // failure is still counted through the same mechanism as a fully-failed entry
                // so it remains observable rather than silently absorbed.
                Interlocked.Increment(ref _corruptedCount);
            }

            if (entry.Level == LogLevel.Critical)
            {
                _roller.Flush();
            }

            entry.Completion?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _corruptedCount);
            entry.Completion?.TrySetResult(false);

            var placeholder = new LogEntry(
                entry.Timestamp,
                LogLevel.Warning,
                nameof(RollingFileLoggerProvider),
                new EventId(LogEvents.LogEntryCorrupted, nameof(LogEvents.LogEntryCorrupted)),
                $"A log entry from category '{entry.Category}' at level {entry.Level} could not be formatted or written and was replaced with this placeholder (failure type: {ex.GetType().FullName}).",
                null,
                []);

            WriteLineSafely(LogLineFormatter.Format(placeholder));
        }
    }

    /// <summary>
    /// Writes pre-formatted text to the active file, swallowing — not rethrowing, not
    /// further reporting — any exception the write itself produces.
    /// </summary>
    /// <remarks>
    /// <b>A third, deliberately minimal waiver of AGENTS.md §6</b>, sibling to the one on
    /// <see cref="WriteEntrySafely"/>: every call site here is itself already a "something
    /// went wrong, report it" path (the corrupted-entry placeholder, and the dropped/corrupted
    /// counter reports), so there is nowhere safe left to report a failure of <i>this</i> write
    /// without risking unbounded recursion. <see cref="LogFileRoller.Write"/> already degrades
    /// its own <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> internally,
    /// but not every exception reachable through it — in particular an
    /// <see cref="ObjectDisposedException"/> or <see cref="NullReferenceException"/> if
    /// <see cref="Dispose"/> has concurrently torn down the roller — so this is the
    /// unconditional last line of defence for those call sites. The failure is swallowed
    /// silently and on purpose: there is no lower-risk alternative than simply not writing
    /// this one line.
    /// </remarks>
    private void WriteLineSafely(string text)
    {
        try
        {
            _roller.Write(text);
        }
        catch (Exception)
        {
            // Intentionally swallowed — see the remarks above.
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _flushTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                FlushAndReportDrops();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }

    /// <summary>
    /// Writes a single <c>Warning</c> entry naming how many log entries have been dropped
    /// since the last report, if the counter changed. Bypasses the channel (direct roller
    /// write, via <see cref="WriteLineSafely"/>) so the report itself can never be dropped by
    /// a full channel, and can never itself crash whichever "must flush now" path called it.
    /// </summary>
    private void ReportDroppedEntries()
    {
        var current = Interlocked.Read(ref _droppedCount);
        var previouslyReported = Interlocked.Exchange(ref _lastReportedDroppedCount, current);

        if (current == previouslyReported)
        {
            return;
        }

        var delta = current - previouslyReported;
        var entry = new LogEntry(
            Clock.UtcNow,
            LogLevel.Warning,
            nameof(RollingFileLoggerProvider),
            new EventId(LogEvents.LogEntryDropped, nameof(LogEvents.LogEntryDropped)),
            $"Dropped {delta} log entries since the last report (bounded channel full; total dropped: {current}).",
            null,
            []);

        WriteLineSafely(LogLineFormatter.Format(entry));
    }

    /// <summary>
    /// Writes a single <c>Warning</c> entry naming how many log entries could not be
    /// formatted/written and were replaced with a placeholder (see
    /// <see cref="WriteEntrySafely"/>) since the last report, if the counter changed. Bypasses
    /// the channel for the same reason <see cref="ReportDroppedEntries"/> does.
    /// </summary>
    private void ReportCorruptedEntries()
    {
        var current = Interlocked.Read(ref _corruptedCount);
        var previouslyReported = Interlocked.Exchange(ref _lastReportedCorruptedCount, current);

        if (current == previouslyReported)
        {
            return;
        }

        var delta = current - previouslyReported;
        var entry = new LogEntry(
            Clock.UtcNow,
            LogLevel.Warning,
            nameof(RollingFileLoggerProvider),
            new EventId(LogEvents.LogEntryCorrupted, nameof(LogEvents.LogEntryCorrupted)),
            $"Corrupted {delta} log entries since the last report (formatting or writing failed; total corrupted: {current}).",
            null,
            []);

        WriteLineSafely(LogLineFormatter.Format(entry));
    }
}
