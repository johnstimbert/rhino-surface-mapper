using FluentAssertions;
using RhinoSurfaceMapper.Infrastructure.Logging;
using RhinoSurfaceMapper.Infrastructure.Tests;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Logging;

/// <summary>
/// Covers <see cref="LogFileRoller"/>: file naming, roll-on-date-change, roll-on-size,
/// retention pruning by age and by total size, and the "never throw" contract for an
/// unwritable directory.
/// </summary>
public sealed class LogFileRollerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>
    /// Reads a file's contents while tolerating the roller's own write handle still being
    /// open on it (the roller opens with <see cref="FileShare.Read"/>, but <see cref="File.ReadAllText(string)"/>
    /// requests a share mode stricter than a concurrent append-mode writer tolerates on
    /// Windows, so tests that read the currently-active file must open their own handle
    /// explicitly with <see cref="FileShare.ReadWrite"/>).
    /// </summary>
    private static string ReadActiveFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void First_write_of_the_day_creates_the_unsuffixed_file_name()
    {
        using var roller = new LogFileRoller(_directory, maxFileBytes: 1024 * 1024, retentionDays: 14, maxTotalBytes: 10 * 1024 * 1024, _clock);

        roller.Write("line 1" + Environment.NewLine);

        roller.CurrentFilePath.Should().Be(Path.Combine(_directory, "rhino-20260110.log"));
        File.Exists(roller.CurrentFilePath).Should().BeTrue();
    }

    [Fact]
    public void Rolling_on_date_change_opens_the_new_days_unsuffixed_file()
    {
        using var roller = new LogFileRoller(_directory, maxFileBytes: 1024 * 1024, retentionDays: 14, maxTotalBytes: 10 * 1024 * 1024, _clock);
        roller.Write("day one" + Environment.NewLine);

        _clock.UtcNow = _clock.UtcNow.AddDays(1);
        roller.Write("day two" + Environment.NewLine);

        roller.CurrentFilePath.Should().Be(Path.Combine(_directory, "rhino-20260111.log"));
        File.Exists(Path.Combine(_directory, "rhino-20260110.log")).Should().BeTrue("the previous day's file must be left intact, not overwritten");
    }

    [Fact]
    public void Rolling_on_size_overflow_creates_a_sequence_suffixed_segment()
    {
        // A tiny MaxFileBytes guarantees the second write overflows the first segment.
        using var roller = new LogFileRoller(_directory, maxFileBytes: 10, retentionDays: 14, maxTotalBytes: 10 * 1024 * 1024, _clock);

        roller.Write("0123456789"); // exactly fills the first segment
        roller.Write("overflow"); // must roll to .001

        roller.CurrentFilePath.Should().Be(Path.Combine(_directory, "rhino-20260110.001.log"));
        File.Exists(Path.Combine(_directory, "rhino-20260110.log")).Should().BeTrue();
    }

    [Fact]
    public void Rolling_on_size_overflow_twice_in_the_same_day_creates_sequential_segments_in_order()
    {
        // Proves the sequence suffix keeps incrementing (.001, .002, ...) rather than only
        // ever producing a single rolled segment.
        using var roller = new LogFileRoller(_directory, maxFileBytes: 10, retentionDays: 14, maxTotalBytes: 10 * 1024 * 1024, _clock);

        roller.Write("0123456789"); // exactly fills .log (10 bytes) — no roll yet (the check is strictly-greater-than)
        roller.Write("A"); // 10 + 1 > 10 -> rolls to .001, then writes "A" into it (size becomes 1)
        roller.CurrentFilePath.Should().Be(Path.Combine(_directory, "rhino-20260110.001.log"));

        roller.Write("0123456789"); // 1 + 10 > 10 -> rolls to .002

        roller.CurrentFilePath.Should().Be(Path.Combine(_directory, "rhino-20260110.002.log"));
        File.Exists(Path.Combine(_directory, "rhino-20260110.log")).Should().BeTrue();
        File.Exists(Path.Combine(_directory, "rhino-20260110.001.log")).Should().BeTrue();
    }

    [Fact]
    public void Retention_pruning_never_deletes_the_currently_open_active_file_even_when_the_total_byte_budget_is_impossible_to_satisfy()
    {
        // maxTotalBytes of -1 makes every prune pass want to delete every single retained
        // file, including the brand-new, zero-byte segment just opened by the roll that
        // triggers this very prune pass. This proves the active file survives because the
        // OS file lock (FileShare.Read only, no delete-sharing) makes File.Delete throw an
        // IOException that TryDelete swallows — not because PruneRetention contains an
        // explicit "skip the last/active file" check (it does not).
        using var roller = new LogFileRoller(_directory, maxFileBytes: 10, retentionDays: 14, maxTotalBytes: -1, _clock);

        roller.Write("0123456789"); // fills segment .log (10 bytes, still open/locked)
        roller.Write("more"); // rolls to .001: closes+unlocks .log, opens+locks .001, then prunes

        File.Exists(Path.Combine(_directory, "rhino-20260110.log")).Should().BeFalse(
            "the now-inactive first segment is unlocked and so is a legitimate prune target under an impossible budget");
        File.Exists(roller.CurrentFilePath).Should().BeTrue("the active, locked segment must survive even an impossible retention budget");

        // "ok" (2 bytes) keeps 4 + 2 = 6 within maxFileBytes (10), so this assertion exercises
        // the surviving file directly rather than triggering yet another roll.
        var act = () => roller.Write("ok");
        act.Should().NotThrow();
        roller.Flush(); // FileStream buffers internally; force the bytes out before reading via a second handle.
        ReadActiveFile(roller.CurrentFilePath).Should().Be("moreok");
    }

    [Fact]
    public void A_mid_write_failure_during_a_roll_is_swallowed_and_faults_the_roller_without_losing_the_prior_segment()
    {
        // Simulates a failure that happens *after* successful construction and a successful
        // first write — unlike Write_never_throws_when_the_target_directory_cannot_be_created,
        // which fails at construction time. Pre-creating a directory at the exact path the
        // next roll will try to open as a file makes FileStream's constructor throw
        // UnauthorizedAccessException mid-Write(), which must be swallowed.
        using var roller = new LogFileRoller(_directory, maxFileBytes: 10, retentionDays: 14, maxTotalBytes: 10 * 1024 * 1024, _clock);
        roller.Write("0123456789"); // fills segment .log; this segment must survive untouched

        var collisionPath = Path.Combine(_directory, "rhino-20260110.001.log");
        Directory.CreateDirectory(collisionPath);

        var act = () => roller.Write("this write triggers a roll that cannot open its target");

        act.Should().NotThrow();
        roller.FaultedWriteCount.Should().Be(1);
        File.ReadAllText(Path.Combine(_directory, "rhino-20260110.log")).Should().Be("0123456789",
            "the previously-completed segment must be left exactly as it was when the roll later failed");

        // Once faulted, the roller must keep degrading silently rather than retry and throw again.
        var secondAct = () => roller.Write("still must not throw");
        secondAct.Should().NotThrow();
        roller.FaultedWriteCount.Should().Be(2);
    }

    [Fact]
    public void Reopening_the_roller_on_the_same_day_resumes_the_highest_existing_sequence()
    {
        using (var first = new LogFileRoller(_directory, maxFileBytes: 10, retentionDays: 14, maxTotalBytes: 10 * 1024 * 1024, _clock))
        {
            first.Write("0123456789");
            first.Write("rolled"); // now on .001
        }

        using var second = new LogFileRoller(_directory, maxFileBytes: 10, retentionDays: 14, maxTotalBytes: 10 * 1024 * 1024, _clock);

        second.CurrentFilePath.Should().Be(Path.Combine(_directory, "rhino-20260110.001.log"));
    }

    [Fact]
    public void Retention_pruning_deletes_files_older_than_the_configured_number_of_days()
    {
        Directory.CreateDirectory(_directory);
        var oldFile = Path.Combine(_directory, "rhino-20251201.log");
        File.WriteAllText(oldFile, "old");

        // Constructing the roller prunes retention immediately (on today's date, 2026-01-10),
        // which is more than retentionDays (14) after 2025-12-01.
        using var roller = new LogFileRoller(_directory, maxFileBytes: 1024 * 1024, retentionDays: 14, maxTotalBytes: 10 * 1024 * 1024, _clock);

        File.Exists(oldFile).Should().BeFalse();
    }

    [Fact]
    public void Retention_pruning_deletes_the_oldest_files_first_once_the_total_size_budget_is_exceeded()
    {
        Directory.CreateDirectory(_directory);
        var oldest = Path.Combine(_directory, "rhino-20260101.log");
        var middle = Path.Combine(_directory, "rhino-20260105.log");
        File.WriteAllBytes(oldest, new byte[100]);
        File.WriteAllBytes(middle, new byte[100]);

        // Budget of 150 bytes cannot hold both 100-byte files plus whatever today's file grows
        // to, so the oldest dated file must be removed first.
        using var roller = new LogFileRoller(_directory, maxFileBytes: 1024 * 1024, retentionDays: 14, maxTotalBytes: 150, _clock);

        File.Exists(oldest).Should().BeFalse();
        File.Exists(middle).Should().BeTrue();
    }

    [Fact]
    public void Retention_pruning_at_construction_never_deletes_the_file_it_is_about_to_resume()
    {
        // Unlike the roll-path scenario above, a pre-existing same-day file resumed at
        // construction is *not yet open* when PruneRetention runs (OpenForDate runs after
        // it), so there is no OS file lock to fall back on here: this is the exact gap the
        // reviewer identified (LogFileRoller construction path, pre-fix pruned before the
        // file existed as a locked handle). maxTotalBytes: -1 makes the budget impossible to
        // satisfy, so the algorithm genuinely attempts to delete every retained file,
        // including the one about to be resumed — proving the explicit excludePath check,
        // not incidental lock timing, is what protects it here.
        Directory.CreateDirectory(_directory);
        var toResume = Path.Combine(_directory, "rhino-20260110.001.log");
        File.WriteAllText(toResume, "resume-me");

        using var roller = new LogFileRoller(_directory, maxFileBytes: 1024 * 1024, retentionDays: 14, maxTotalBytes: -1, _clock);

        roller.CurrentFilePath.Should().Be(toResume);
        File.Exists(toResume).Should().BeTrue("the file about to be resumed must survive construction-time pruning even under an impossible budget");
        ReadActiveFile(toResume).Should().StartWith("resume-me", "the pre-existing content must not be lost either");
    }

    [Fact]
    public void Write_never_throws_when_the_target_directory_cannot_be_created()
    {
        // A file (not a directory) occupying the path makes Directory.CreateDirectory throw
        // IOException, which the roller must swallow and degrade to a no-op sink for.
        var blockedPath = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(blockedPath)!);
        File.WriteAllText(blockedPath, "this is a file, not a directory");

        try
        {
            using var roller = new LogFileRoller(blockedPath, maxFileBytes: 1024, retentionDays: 14, maxTotalBytes: 1024, _clock);

            var act = () => roller.Write("should not throw");

            act.Should().NotThrow();
            roller.CurrentFilePath.Should().BeEmpty("a faulted roller never resolves an active file path");
        }
        finally
        {
            File.Delete(blockedPath);
        }
    }
}
