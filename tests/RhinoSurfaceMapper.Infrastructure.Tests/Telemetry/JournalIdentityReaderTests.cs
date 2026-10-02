using FluentAssertions;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Infrastructure.Telemetry;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Telemetry;

/// <summary>
/// Covers <see cref="JournalIdentityReader"/> against <c>python/tests/test_elite_dangerous_journal.py</c>'s
/// acceptance spec: file-selection tie-break order, incremental byte-offset reads, a truncated
/// final-line retry, a file-shrink reset, the <c>Location</c>/<c>FSDJump</c>-only identity
/// update rule, and malformed-line skip+log behaviour.
/// </summary>
public sealed class JournalIdentityReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger<JournalIdentityReader> _logger = new();

    public JournalIdentityReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private JournalIdentityReader CreateReader() => new(_logger, _root);

    private static string JsonLine(string json) => json + "\n";

    [Fact]
    public void CurrentIdentity_returns_null_when_no_journal_file_exists()
    {
        var reader = CreateReader();

        reader.CurrentIdentity().Should().BeNull();
    }

    [Fact]
    public void CurrentIdentity_selects_the_highest_timestamp_when_timestamps_differ()
    {
        WriteJournal("Journal.2024-01-01T000000.01.log", JsonLine("""{"event":"Location","StarSystem":"Old"}"""));
        WriteJournal("Journal.2024-01-02T000000.01.log", JsonLine("""{"event":"Location","StarSystem":"New"}"""));

        var identity = CreateReader().CurrentIdentity();

        identity!.System.Should().Be("New");
    }

    [Fact]
    public void CurrentIdentity_Should_SelectTheNewerTimestampRegardlessOfSequence_When_FileNamesCompete()
    {
        WriteJournal("Journal.2024-01-01T235959.99.log", JsonLine("""{"event":"Location","StarSystem":"OlderHighSequence"}"""));
        WriteJournal("Journal.2024-01-02T000000.01.log", JsonLine("""{"event":"Location","StarSystem":"NewerLowSequence"}"""));

        var identity = CreateReader().CurrentIdentity();

        identity!.System.Should().Be("NewerLowSequence");
    }

    [Fact]
    public void CurrentIdentity_breaks_a_timestamp_tie_using_the_highest_sequence_number()
    {
        WriteJournal("Journal.2024-01-01T000000.01.log", JsonLine("""{"event":"Location","StarSystem":"SeqOne"}"""));
        WriteJournal("Journal.2024-01-01T000000.02.log", JsonLine("""{"event":"Location","StarSystem":"SeqTwo"}"""));

        var identity = CreateReader().CurrentIdentity();

        identity!.System.Should().Be("SeqTwo");
    }

    [Fact]
    public void CurrentIdentity_Should_OnlyProcessNewlyAppendedBytes_When_SubsequentReadsContinueFromTheSavedOffset()
    {
        string path = WriteJournal(
            "Journal.2024-01-01T000000.01.log",
            JsonLine("""{"event":"Location","StarSystem":"First","Body":"First A"}""") + "{ not json }\n");
        var reader = CreateReader();

        var first = reader.CurrentIdentity();

        first!.System.Should().Be("First");
        _logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("invalid JSON", StringComparison.Ordinal));

        File.AppendAllText(path, JsonLine("""{"event":"FSDJump","StarSystem":"Second","Body":"Second B"}"""));
        var second = reader.CurrentIdentity();

        second.Should().BeEquivalentTo(new JournalIdentity("Second", "Second B"));
        _logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Debug && entry.Message.Contains("invalid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void CurrentIdentity_reads_incrementally_across_multiple_calls()
    {
        string path = WriteJournal("Journal.2024-01-01T000000.01.log", JsonLine("""{"event":"Location","StarSystem":"First"}"""));
        var reader = CreateReader();
        reader.CurrentIdentity().Should().NotBeNull();

        File.AppendAllText(path, JsonLine("""{"event":"FSDJump","StarSystem":"Second","Body":"Second B"}"""));
        var identity = reader.CurrentIdentity();

        identity!.System.Should().Be("Second");
        identity.Body.Should().Be("Second B");
    }

    [Fact]
    public void CurrentIdentity_Should_ProcessTheBufferedFinalLineOnce_When_ALaterWriteCompletesIt()
    {
        string path = WriteJournal(
            "Journal.2024-01-01T000000.01.log",
            JsonLine("""{"event":"Location","StarSystem":"Start","Body":"Start A"}"""));
        var reader = CreateReader();

        reader.CurrentIdentity().Should().BeEquivalentTo(new JournalIdentity("Start", "Start A"));

        File.AppendAllText(path, "{\"event\":\"FSDJump\",\"StarSystem\":\"CompletedLater\",\"Body\":\"CompletedLater A\"");
        reader.CurrentIdentity().Should().BeEquivalentTo(
            new JournalIdentity("Start", "Start A"),
            "the incomplete final line must stay buffered until a later write finishes it");

        File.AppendAllText(path, "}\n");
        reader.CurrentIdentity().Should().BeEquivalentTo(new JournalIdentity("CompletedLater", "CompletedLater A"));
        reader.CurrentIdentity().Should().BeEquivalentTo(
            new JournalIdentity("CompletedLater", "CompletedLater A"),
            "once the buffered line has been consumed, another poll without new bytes must not reprocess it");
    }

    [Fact]
    public void CurrentIdentity_retries_a_truncated_final_line_on_the_next_call()
    {
        string path = WriteJournal("Journal.2024-01-01T000000.01.log", string.Empty);
        var reader = CreateReader();

        // A writer flush mid-line: no trailing newline yet.
        File.AppendAllText(path, "{\"event\":\"Location\",\"StarSystem\":\"Partial\""); // deliberately truncated, no closing brace/newline
        reader.CurrentIdentity().Should().BeNull("a line without its terminator must not be parsed prematurely");

        File.AppendAllText(path, "}\n");
        var identity = reader.CurrentIdentity();

        identity!.System.Should().Be("Partial");
    }

    [Fact]
    public void CurrentIdentity_resets_its_read_position_when_the_active_file_shrinks()
    {
        string path = WriteJournal(
            "Journal.2024-01-01T000000.01.log",
            JsonLine("""{"event":"Location","StarSystem":"First"}""") + JsonLine("""{"event":"Location","StarSystem":"Second"}"""));
        var reader = CreateReader();
        reader.CurrentIdentity()!.System.Should().Be("Second");

        // Truncate the file back down (log rotation/replacement), then write fresh content.
        File.WriteAllText(path, JsonLine("""{"event":"Location","StarSystem":"AfterShrink"}"""));
        var identity = reader.CurrentIdentity();

        identity!.System.Should().Be("AfterShrink");
    }

    [Fact]
    public void CurrentIdentity_ignores_events_other_than_Location_and_FSDJump()
    {
        WriteJournal(
            "Journal.2024-01-01T000000.01.log",
            JsonLine("""{"event":"Location","StarSystem":"Known"}""") + JsonLine("""{"event":"Docked","StarSystem":"ShouldNotApply"}"""));

        var identity = CreateReader().CurrentIdentity();

        identity!.System.Should().Be("Known");
    }

    [Fact]
    public void CurrentIdentity_Should_UpdateIdentityOnlyFromLocationAndFsdJump_When_OtherEventsAreInterleaved()
    {
        WriteJournal(
            "Journal.2024-01-01T000000.01.log",
            JsonLine("""{"event":"Location","StarSystem":"Alpha","Body":"Alpha A"}""")
                + JsonLine("""{"event":"Scan","StarSystem":"IgnoredOne","Body":"Ignored Body"}""")
                + JsonLine("""{"event":"Docked","StarSystem":"IgnoredTwo","Body":"Ignored Dock"}""")
                + JsonLine("""{"event":"FSDJump","StarSystem":"Beta","Body":"Beta B"}""")
                + JsonLine("""{"event":"Fuel","StarSystem":"IgnoredThree","Body":"Ignored Fuel"}"""));

        var identity = CreateReader().CurrentIdentity();

        identity.Should().BeEquivalentTo(new JournalIdentity("Beta", "Beta B"));
    }

    [Fact]
    public void CurrentIdentity_keeps_the_previous_body_when_a_new_event_omits_one()
    {
        WriteJournal(
            "Journal.2024-01-01T000000.01.log",
            JsonLine("""{"event":"Location","StarSystem":"Known","Body":"Known B"}""")
                + JsonLine("""{"event":"FSDJump","StarSystem":"Known"}"""));

        var identity = CreateReader().CurrentIdentity();

        identity!.Body.Should().Be("Known B");
    }

    [Fact]
    public void CurrentIdentity_Should_LogDebugAndSkipTheMalformedLine_When_ValidRecordsExistBeforeAndAfterIt()
    {
        WriteJournal(
            "Journal.2024-01-01T000000.01.log",
            JsonLine("""{"event":"Location","StarSystem":"Before","Body":"Before A"}""")
                + "{ not json }\n"
                + JsonLine("""{"event":"Docked","StarSystem":"Ignored","Body":"Ignored Body"}""")
                + JsonLine("""{"event":"FSDJump","StarSystem":"After","Body":"After B"}"""));

        var identity = CreateReader().CurrentIdentity();

        identity.Should().BeEquivalentTo(new JournalIdentity("After", "After B"));
        _logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Debug
            && entry.EventId.Id == LogEvents.JournalRecordSkipped
            && entry.Message.Contains("Skipped malformed Journal record", StringComparison.Ordinal)
            && entry.Message.Contains("invalid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void CurrentIdentity_skips_a_malformed_line_and_logs_it_at_Debug()
    {
        WriteJournal(
            "Journal.2024-01-01T000000.01.log",
            "{ not json }\n" + JsonLine("""{"event":"Location","StarSystem":"Known"}"""));

        var identity = CreateReader().CurrentIdentity();

        identity!.System.Should().Be("Known");
        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug);
    }

    [Fact]
    public void Reset_discards_the_active_file_offset_and_identity()
    {
        WriteJournal("Journal.2024-01-01T000000.01.log", JsonLine("""{"event":"Location","StarSystem":"Known"}"""));
        var reader = CreateReader();
        reader.CurrentIdentity()!.System.Should().Be("Known");

        reader.Reset();

        // After Reset, the same file is re-read from the beginning, producing the same identity
        // again rather than null — Reset clears position/identity, not the file on disk.
        reader.CurrentIdentity()!.System.Should().Be("Known");
    }

    private string WriteJournal(string fileName, string content)
    {
        string path = Path.Combine(_root, fileName);
        File.WriteAllText(path, content);
        return path;
    }
}
