using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RhinoSurfaceMapper.Infrastructure.Logging;
using RhinoSurfaceMapper.Infrastructure.Paths;
using RhinoSurfaceMapper.Infrastructure.Tests;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Logging;

/// <summary>
/// Covers <see cref="RollingFileLoggerProvider"/> end-to-end: logger creation, directory
/// resolution (relative vs. absolute), scope rendering through the real external scope
/// provider, per-category level filtering, the three flush triggers (<c>Critical</c>, the
/// 2-second periodic timer, and <see cref="RollingFileLoggerProvider.Dispose"/>), the
/// bounded-channel drop counter required by the design ("10 000 capacity, DropWrite,
/// dropped entries are themselves logged"), and <see cref="IDisposable.Dispose"/> idempotency.
/// </summary>
public sealed class RollingFileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private RollingFileLoggerProvider CreateProvider(RollingFileLoggerOptions? options = null)
    {
        options ??= new RollingFileLoggerOptions { Directory = _directory };
        return new RollingFileLoggerProvider(Options.Create(options), new AppPaths(_directory), _clock);
    }

    [Fact]
    public async Task A_logged_entry_is_eventually_written_to_the_active_file_on_disk()
    {
        using var provider = CreateProvider();
        var logger = provider.CreateLogger("My.Category");

        logger.LogInformation("Hello from the provider");

        var contents = await WaitForFileContentAsync(provider.CurrentFilePath, "Hello from the provider");

        contents.Should().Contain("Hello from the provider");
    }

    [Fact]
    public void CreateLogger_returns_a_working_ILogger_for_any_category_name()
    {
        using var provider = CreateProvider();

        var logger = provider.CreateLogger("Any.Category");

        logger.Should().NotBeNull();
        logger.IsEnabled(LogLevel.Information).Should().BeTrue();
    }

    [Fact]
    public void A_relative_configured_directory_resolves_against_IAppPaths_BaseDirectory()
    {
        // configured.Directory defaults to "logs" (relative); Path.IsPathRooted is false, so
        // the provider must combine it with IAppPaths.BaseDirectory rather than treat it as
        // relative to the process's current working directory (which would break portability).
        var appPaths = new AppPaths(_directory);
        using var provider = new RollingFileLoggerProvider(
            Options.Create(new RollingFileLoggerOptions { Directory = "logs" }), appPaths, _clock);

        provider.CreateLogger("Cat").LogInformation("hi");

        provider.CurrentFilePath.Should().StartWith(Path.Combine(_directory, "logs") + Path.DirectorySeparatorChar);
    }

    [Fact]
    public void An_absolute_configured_directory_is_honoured_as_is_and_not_combined_with_the_base_directory()
    {
        // appPaths' own base directory is deliberately a different, non-existent path here so
        // that a passing test can only mean the absolute Directory value was used verbatim.
        var unrelatedBase = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));
        using var provider = new RollingFileLoggerProvider(
            Options.Create(new RollingFileLoggerOptions { Directory = _directory }), new AppPaths(unrelatedBase), _clock);

        provider.CreateLogger("Cat").LogInformation("hi");

        provider.CurrentFilePath.Should().StartWith(_directory);
        Directory.Exists(unrelatedBase).Should().BeFalse("an absolute Directory must never fall back to the base directory");
    }

    [Fact]
    public async Task Logging_within_nested_scopes_renders_every_active_scopes_key_value_pairs_outermost_first()
    {
        // Exercises RollingFileLogger.CollectScopes end-to-end (not just LogLineFormatter in
        // isolation): a real ILoggerFactory attaches its external scope provider to the
        // provider via ISupportExternalScope, so BeginScope/ForEachScope run for real.
        using var provider = CreateProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("Scoped.Category");

        using (logger.BeginScope(new Dictionary<string, object?> { ["map"] = "Nervi 2 A [JD3]", ["gen"] = 7 }))
        using (logger.BeginScope(new Dictionary<string, object?> { ["assistState"] = "Active" }))
        {
            logger.LogInformation("Handled");
        }

        var contents = await WaitForFileContentAsync(provider.CurrentFilePath, "Handled");

        contents.Should().Contain("map=Nervi 2 A [JD3] gen=7 assistState=Active | Handled",
            "the outer scope's two key/value pairs must precede the inner scope's single pair");
    }

    [Fact]
    public async Task Configured_per_category_minimum_levels_suppress_entries_below_the_threshold()
    {
        // RollingFileLogger.IsEnabled always returns true (by design — filtering is the
        // standard Microsoft.Extensions.Logging pipeline's job, bound from "Logging:LogLevel"
        // configuration). This proves that pipeline actually suppresses the category here.
        using var provider = CreateProvider();
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(provider);
            builder.AddFilter("Noisy.Category", LogLevel.Warning);
        });
        var logger = factory.CreateLogger("Noisy.Category");

        logger.LogInformation("suppressed-info-message");
        logger.LogWarning("permitted-warning-message");

        var contents = await WaitForFileContentAsync(provider.CurrentFilePath, "permitted-warning-message");

        contents.Should().NotContain("suppressed-info-message");
        contents.Should().Contain("permitted-warning-message");
    }

    [Fact]
    public void Logging_at_Critical_makes_the_entry_durable_before_the_logging_call_returns()
    {
        // Supersedes an earlier, deliberately-weakened version of this test that found "flush
        // immediately for Critical" was not actually honoured: the entry only ever became
        // visible on the 2-second periodic tick, because the flush that used to run inside
        // Enqueue() raced the background writer rather than waiting for it. The fix makes the
        // writer flush immediately after writing a Critical entry specifically and signal
        // LogEntry.Completion only then; RollingFileLogger.Log blocks the calling thread on
        // that signal (bounded by CriticalFlushTimeout) before returning. This test therefore
        // deliberately does NOT poll or wait at all after the call below returns — a real
        // guarantee needs no polling, only the previous, weakened version did.
        using var provider = CreateProvider();
        var logger = provider.CreateLogger("Cat");

        logger.LogCritical("urgent-marker");

        TryReadAllText(provider.CurrentFilePath, out var contents).Should().BeTrue(
            "the file must already exist and be readable the instant LogCritical returns");
        contents.Should().Contain("urgent-marker");
    }

    [Fact]
    public void A_critical_log_call_returns_promptly_even_when_the_entry_is_dropped_rather_than_waiting_out_the_full_timeout()
    {
        // Models "the writer is not draining" without needing an internal seam to literally
        // pause the background task: flooding the channel from many threads first (the same
        // technique the overflow-accounting test below uses) reliably leaves a large backlog
        // queued ahead of the Critical entry logged afterwards, so _pendingCount's pre-check
        // in Enqueue() very likely rejects it outright — and Enqueue() signals a dropped
        // entry's Completion as false immediately, rather than leaving the caller to wait out
        // CriticalFlushTimeout for an entry that was never going to be written. Either way
        // (written fast, or dropped fast) the call must never approach the full timeout.
        using var provider = CreateProvider();
        var filler = new LogEntry(_clock.UtcNow, LogLevel.Information, "Filler", new EventId(0), "filler", null, []);
        Parallel.For(0, 60_000, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, _ => provider.Enqueue(filler));

        var logger = provider.CreateLogger("Cat");
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.LogCritical("should-return-promptly-marker");
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1),
            "a Critical call must never block anywhere near its full bounded timeout just because the channel is backlogged");
    }

    [Fact]
    public async Task Non_critical_entries_are_not_visible_on_disk_until_the_periodic_flush_tick_runs()
    {
        // FlushInterval (2 seconds) is a private constant, not an injectable seam, so this is
        // the one genuinely real-time wait in this test class — kept to a single pass just
        // over the interval rather than polled repeatedly against the wall clock.
        using var provider = CreateProvider();
        var logger = provider.CreateLogger("Cat");

        logger.LogInformation("delayed-visibility-marker");

        await Task.Delay(TimeSpan.FromMilliseconds(300));
        TryReadAllText(provider.CurrentFilePath, out var early);
        early.Should().NotContain("delayed-visibility-marker",
            "nothing should force a flush for a non-Critical entry within the first 300 ms (FlushInterval is 2 s)");

        var contents = await WaitForFileContentAsync(provider.CurrentFilePath, "delayed-visibility-marker", TimeSpan.FromSeconds(5));
        contents.Should().Contain("delayed-visibility-marker");
    }

    [Fact]
    public async Task Dispose_flushes_pending_entries_before_returning()
    {
        var provider = CreateProvider();
        var logger = provider.CreateLogger("My.Category");
        logger.LogInformation("Flushed on dispose");

        // Give the background writer a brief window to drain under normal (non-full-channel)
        // conditions, then Dispose() must guarantee the rest is flushed regardless.
        await Task.Delay(50);
        provider.Dispose();

        File.ReadAllText(provider.CurrentFilePath).Should().Contain("Flushed on dispose");
    }

    [Fact]
    public void Dispose_is_idempotent_when_called_more_than_once()
    {
        // The provider is registered in DI under two service types resolving the same
        // singleton instance (see ConfigureServices), and the container disposes each
        // registration independently — so Dispose() must tolerate being called twice.
        var provider = CreateProvider();
        provider.Dispose();

        var act = () => provider.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Enqueuing_fewer_entries_than_the_channel_capacity_loses_none_of_them()
    {
        // The complement of the overflow test below: proves the "no loss under capacity" half
        // of the design's "10 000 capacity, DropWrite" contract.
        const int count = 200; // comfortably under the 10 000-entry channel capacity
        using var provider = CreateProvider();
        var logger = provider.CreateLogger("My.Category");

        for (var i = 0; i < count; i++)
        {
            logger.LogInformation("under-capacity-marker-{Index}", i);
        }

        provider.Dispose();

        var contents = await File.ReadAllTextAsync(provider.CurrentFilePath);
        contents.Should().NotContain("Dropped", "no entry should have been dropped well under channel capacity");
        for (var i = 0; i < count; i++)
        {
            contents.Should().Contain($"under-capacity-marker-{i}");
        }
    }

    [Fact]
    public async Task Overflowing_the_bounded_channel_drops_entries_and_the_written_plus_dropped_counts_exactly_account_for_every_attempt()
    {
        // This is the "genuinely proves it" version of the drop-accounting test: rather than
        // only asserting the file *contains* a "Dropped" line, it reconciles every single one
        // of `attempts` enqueue calls as either (a) written to disk with its marker intact or
        // (b) counted in the cumulative "total dropped" figure the provider itself reports —
        // proving the Interlocked pending-count workaround (documented on Enqueue) neither
        // loses an entry silently nor double-counts one.
        //
        // Enqueue() (internal, exercised directly rather than through ILogger.Log to avoid
        // message-formatting overhead at this volume) is called from many threads at once so
        // the aggregate production rate reliably exceeds what the single background writer
        // can drain concurrently: a single-threaded producer loop cannot reliably outrun the
        // writer (observed empirically — the writer keeps up even at very high sequential
        // volumes), but a burst from dozens of threads simultaneously reliably overflows the
        // 10 000-capacity bounded channel. This remains a probabilistic trigger for *how many*
        // entries drop (that part genuinely cannot be pinned to an exact number without an
        // internal seam to pause the writer), but the accounting identity asserted below holds
        // unconditionally regardless of how the race resolves on any given run.
        const int attempts = 60_000; // several times the 10 000-entry channel capacity
        const string marker = "PROBE-OVERFLOW-MARKER";

        using var provider = CreateProvider();
        var entry = new LogEntry(_clock.UtcNow, LogLevel.Information, "My.Category", new EventId(0), marker, null, []);

        Parallel.For(0, attempts, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 }, _ => provider.Enqueue(entry));

        // Dispose() deterministically forces a final drop report and flush, rather than
        // relying on a real-time wait for the periodic timer.
        provider.Dispose();

        var contents = await File.ReadAllTextAsync(provider.CurrentFilePath);

        var writtenCount = CountOccurrences(contents, marker);
        var totalDroppedMatches = Regex.Matches(contents, @"total dropped: (\d+)\)");
        totalDroppedMatches.Should().NotBeEmpty(
            "60 000 attempts across many threads must overflow a 10 000-capacity channel at least once");

        var totalDropped = long.Parse(totalDroppedMatches[^1].Groups[1].Value);

        totalDropped.Should().BeGreaterThan(0);
        (writtenCount + totalDropped).Should().Be(attempts,
            "every enqueue attempt must be accounted for as either written or dropped — none may vanish without a trace");
    }

    [Fact]
    public async Task A_scope_value_whose_ToString_throws_does_not_lose_the_entry_or_its_successors()
    {
        // Reproduces the blocking defect end-to-end through a real ILoggerFactory/AddProvider
        // pipeline (not a directly-constructed RollingFileLogger): before the fix,
        // LogLineFormatter.Format called ToString() on a caller-supplied scope value with no
        // protection, an uncaught exception there propagated out of the await foreach body in
        // WriteLoopAsync, and that silently and permanently killed the single background
        // writer task for the remainder of the process — every entry logged afterwards,
        // including ones with nothing to do with the offending scope, was lost without any
        // warning. The regression asserted here is specifically that the entry immediately
        // *after* the bad one survives and becomes visible.
        using var provider = CreateProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("Cat");

        using (logger.BeginScope(new Dictionary<string, object> { ["bad"] = new ThrowingToString() }))
        {
            logger.LogInformation("entry-with-bad-scope-marker");
        }

        logger.LogInformation("entry-after-bad-scope-marker");

        // Dispose() deterministically forces a final flush rather than relying on a real-time
        // wait for the 2-second periodic tick.
        provider.Dispose();

        var contents = await File.ReadAllTextAsync(provider.CurrentFilePath);

        contents.Should().Contain("entry-after-bad-scope-marker",
            "the writer must keep processing entries after one entry's scope value throws while formatting");
    }

    [Fact]
    public async Task A_scope_value_whose_ToString_throws_is_counted_and_reported_as_a_corrupted_entry()
    {
        using var provider = CreateProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("Cat");

        using (logger.BeginScope(new Dictionary<string, object> { ["bad"] = new ThrowingToString() }))
        {
            logger.LogInformation("entry-triggering-corruption-report");
        }

        // Dispose() deterministically forces the final corrupted-entry report, rather than
        // relying on a real-time wait for the periodic timer.
        provider.Dispose();

        var contents = await File.ReadAllTextAsync(provider.CurrentFilePath);

        contents.Should().Contain("Corrupted", "a formatting failure must be surfaced through the same observable reporting mechanism as drops, never silently");
    }

    [Fact]
    public async Task The_writer_survives_repeated_bad_entries_and_keeps_writing_good_ones_afterwards()
    {
        using var provider = CreateProvider();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(provider));
        var logger = factory.CreateLogger("Cat");

        for (var i = 0; i < 25; i++)
        {
            using (logger.BeginScope(new Dictionary<string, object> { ["bad"] = new ThrowingToString() }))
            {
                logger.LogInformation("repeated-bad-entry-{Index}", i);
            }
        }

        logger.LogInformation("final-good-entry-marker");

        // Dispose() deterministically forces a final flush rather than relying on a real-time
        // wait for the 2-second periodic tick.
        provider.Dispose();

        var contents = await File.ReadAllTextAsync(provider.CurrentFilePath);

        contents.Should().Contain("final-good-entry-marker",
            "the writer task must still be alive and draining the channel after many consecutive formatting failures");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static Task<string> WaitForFileContentAsync(string path, string expectedSubstring) =>
        WaitForFileContentAsync(path, expectedSubstring, TimeSpan.FromSeconds(5));

    private static async Task<string> WaitForFileContentAsync(string path, string expectedSubstring, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && TryReadAllText(path, out var content) && content.Contains(expectedSubstring, StringComparison.Ordinal))
            {
                return content;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"'{expectedSubstring}' was not written to '{path}' within {timeout}.");
    }

    /// <summary>
    /// Reads the file with a share mode that tolerates the roller's write handle still being
    /// open, and tolerates a transient sharing violation (the roller briefly holds an
    /// exclusive lock while rolling to a new segment) by treating it as "not ready yet"
    /// rather than failing the poll loop.
    /// </summary>
    private static bool TryReadAllText(string path, out string content)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            content = reader.ReadToEnd();
            return true;
        }
        catch (IOException)
        {
            content = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// A scope value whose <see cref="ToString"/> override always throws, modelling a caller
    /// that attaches malformed domain data via <c>BeginScope</c> (the design's "structured
    /// context" scopes — <c>{ map, system, body, generation }</c> — are exactly this kind of
    /// caller-supplied value).
    /// </summary>
    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("Deliberately broken ToString for test purposes.");
    }
}

