using FluentAssertions;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Infrastructure.Logging;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Logging;

/// <summary>
/// Covers <see cref="LogLineFormatter"/>: the exact pipe-delimited line format, scope
/// rendering, and indented exception (including inner exception) rendering.
/// </summary>
public sealed class LogLineFormatterTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 2, 11, 24, 31, 482, TimeSpan.Zero);

    [Fact]
    public void Format_renders_timestamp_level_category_and_message_pipe_delimited()
    {
        var entry = new LogEntry(Timestamp, LogLevel.Information, "My.Category", new EventId(1, "Test"), "Hello world", null, []);

        var line = LogLineFormatter.Format(entry);

        line.Should().Be("2026-10-02T11:24:31.482Z | INF | My.Category | Hello world" + Environment.NewLine);
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRC")]
    [InlineData(LogLevel.Debug, "DBG")]
    [InlineData(LogLevel.Information, "INF")]
    [InlineData(LogLevel.Warning, "WRN")]
    [InlineData(LogLevel.Error, "ERR")]
    [InlineData(LogLevel.Critical, "CRT")]
    public void Format_abbreviates_every_log_level_to_its_three_letter_code(LogLevel level, string expected)
    {
        var entry = new LogEntry(Timestamp, level, "Cat", new EventId(0), "msg", null, []);

        LogLineFormatter.Format(entry).Should().Contain($"| {expected} |");
    }

    [Fact]
    public void Format_renders_scopes_as_space_separated_key_value_pairs_before_the_message()
    {
        var scopes = new List<KeyValuePair<string, object?>>
        {
            new("map", "Nervi 2 A [JD3]"),
            new("gen", 7),
        };
        var entry = new LogEntry(Timestamp, LogLevel.Information, "Cat", new EventId(0), "Handled", null, scopes);

        var line = LogLineFormatter.Format(entry);

        line.Should().Be("2026-10-02T11:24:31.482Z | INF | Cat | map=Nervi 2 A [JD3] gen=7 | Handled" + Environment.NewLine);
    }

    [Fact]
    public void Format_omits_the_scopes_segment_entirely_when_no_scope_is_active()
    {
        var entry = new LogEntry(Timestamp, LogLevel.Information, "Cat", new EventId(0), "Handled", null, []);

        var line = LogLineFormatter.Format(entry);

        line.Should().NotContain("|  |");
        line.Should().Be("2026-10-02T11:24:31.482Z | INF | Cat | Handled" + Environment.NewLine);
    }

    [Fact]
    public void Format_appends_an_indented_exception_block_after_the_message_line()
    {
        var exception = new InvalidOperationException("boom");
        var entry = new LogEntry(Timestamp, LogLevel.Error, "Cat", new EventId(0), "Failed", exception, []);

        var line = LogLineFormatter.Format(entry);

        line.Should().Contain("Failed" + Environment.NewLine);
        line.Should().Contain("    System.InvalidOperationException: boom");
    }

    [Fact]
    public void Format_renders_inner_exceptions_recursively_with_increasing_indentation()
    {
        var inner = new ArgumentException("inner-cause");
        var outer = new InvalidOperationException("outer-failure", inner);
        var entry = new LogEntry(Timestamp, LogLevel.Error, "Cat", new EventId(0), "Failed", outer, []);

        var line = LogLineFormatter.Format(entry);

        line.Should().Contain("    System.InvalidOperationException: outer-failure");
        line.Should().Contain("    ---> Inner exception:");
        line.Should().Contain("        System.ArgumentException: inner-cause");
    }

    [Fact]
    public void Format_does_not_emit_any_exception_block_when_there_is_no_exception()
    {
        var entry = new LogEntry(Timestamp, LogLevel.Information, "Cat", new EventId(0), "All good", null, []);

        var line = LogLineFormatter.Format(entry);

        line.Should().Be("2026-10-02T11:24:31.482Z | INF | Cat | All good" + Environment.NewLine);
    }
}
