using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace RhinoSurfaceMapper.Infrastructure.Logging;

/// <summary>
/// Renders a <see cref="LogEntry"/> into the pipe-delimited, one-entry-per-line text format
/// specified by the design's "Logging design" §1:
/// <code>
/// 2026-10-02T11:24:31.482Z | INF | RhinoSurfaceMapper.Application.Features.MapSession.SaveMap | map=Nervi 2 A [JD3] gen=7 | Handled SaveMap.Command in 12 ms | Result=Success
/// </code>
/// Exceptions, when present, are appended after the message on their own indented lines,
/// including inner exceptions and stack traces.
/// </summary>
internal static class LogLineFormatter
{
    /// <summary>
    /// Builds the complete text (message line plus any exception lines, each terminated with
    /// <see cref="Environment.NewLine"/>) to append to the active log file for <paramref name="entry"/>.
    /// </summary>
    public static string Format(LogEntry entry) => Format(entry, out _);

    /// <summary>
    /// Overload of <see cref="Format(LogEntry)"/> that also reports, via
    /// <paramref name="hadUnprintableValue"/>, whether any scope value's <see cref="object.ToString"/>
    /// threw and was substituted with a placeholder by <see cref="SafeToString"/>. The caller
    /// (<c>RollingFileLoggerProvider.WriteEntrySafely</c>) uses this to count the degradation
    /// through the same corrupted-entry reporting mechanism used for a fully-failed entry, even
    /// though — thanks to <see cref="SafeToString"/> — the entry itself is still written intact
    /// in this case rather than being replaced wholesale by a placeholder line.
    /// </summary>
    public static string Format(LogEntry entry, out bool hadUnprintableValue)
    {
        var builder = new StringBuilder();
        hadUnprintableValue = false;

        // Segment order: timestamp | level | category | [scopes |] message
        // The scopes segment is entirely omitted (not left as an empty "| |") when no scope
        // is active, keeping the common case readable while remaining greppable.
        builder.Append(entry.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
        builder.Append(" | ").Append(LevelAbbreviation(entry.Level));
        builder.Append(" | ").Append(entry.Category);

        if (entry.Scopes.Count > 0)
        {
            builder.Append(" | ").Append(FormatScopes(entry.Scopes, out hadUnprintableValue));
        }

        builder.Append(" | ").Append(entry.Message);
        builder.Append(Environment.NewLine);

        if (entry.Exception is not null)
        {
            AppendException(builder, entry.Exception, indent: 1);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders scope key/value pairs as space-separated <c>key=value</c> tokens, in the order
    /// they were collected (outermost scope first).
    /// </summary>
    /// <remarks>
    /// Each value is rendered through <see cref="SafeToString"/> rather than a direct
    /// <see cref="StringBuilder.Append(object)"/>: a scope value is caller-supplied data (see
    /// the design's "Structured context" scopes, e.g. <c>{ map, system, body, generation }</c>)
    /// and this formatter has no control over its <c>ToString()</c> override, so one bad value
    /// degrades to a placeholder token in place rather than preventing every other scope (and
    /// the entry's own message) in the same line from rendering at all. This is defence in
    /// depth alongside, not a replacement for, <c>RollingFileLoggerProvider.WriteEntrySafely</c>'s
    /// entry-level catch — that is still the last line of defence for any failure this method
    /// does not anticipate (for example <see cref="KeyValuePair{TKey,TValue}.Key"/> itself, or
    /// <see cref="StringBuilder.Append(string)"/>, throwing).
    /// </remarks>
    private static string FormatScopes(IReadOnlyList<KeyValuePair<string, object?>> scopes, out bool hadUnprintableValue)
    {
        var builder = new StringBuilder();
        hadUnprintableValue = false;

        for (var i = 0; i < scopes.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            var rendered = SafeToString(scopes[i].Value, out var valueWasUnprintable);
            hadUnprintableValue |= valueWasUnprintable;
            builder.Append(scopes[i].Key).Append('=').Append(rendered);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Calls <see cref="object.ToString"/> on <paramref name="value"/>, substituting a
    /// placeholder that names the failing type instead of propagating the exception if that
    /// call throws — see the remarks on <see cref="FormatScopes"/> for why a scope value is
    /// treated as untrusted. Returns the literal text <c>"(null)"</c> for a <see langword="null"/>
    /// value, matching what <see cref="StringBuilder.Append(object)"/> would otherwise render.
    /// Reports the failure via <paramref name="failed"/> so the caller can still count and
    /// report it, even though the entry as a whole is not lost.
    /// </summary>
    /// <remarks>
    /// A second, narrowly-scoped waiver of AGENTS.md §6 alongside
    /// <c>RollingFileLoggerProvider.WriteEntrySafely</c>'s: <paramref name="value"/>'s
    /// <c>ToString()</c> override is caller-supplied code that can throw any exception type by
    /// definition, so there is no finite, specific set of exception types to catch here.
    /// </remarks>
    private static string SafeToString(object? value, out bool failed)
    {
        failed = false;

        if (value is null)
        {
            return "(null)";
        }

        try
        {
            return value.ToString() ?? "(null)";
        }
        catch (Exception ex)
        {
            failed = true;
            return $"(unprintable: {ex.GetType().Name})";
        }
    }

    /// <summary>
    /// Appends an exception (and, recursively, every <see cref="Exception.InnerException"/>)
    /// as indented lines following the message line, preserving the stack trace.
    /// </summary>
    private static void AppendException(StringBuilder builder, Exception exception, int indent)
    {
        var prefix = new string(' ', indent * 4);

        foreach (var line in SplitLines($"{exception.GetType().FullName}: {exception.Message}"))
        {
            builder.Append(prefix).Append(line).Append(Environment.NewLine);
        }

        if (exception.StackTrace is not null)
        {
            foreach (var line in SplitLines(exception.StackTrace))
            {
                builder.Append(prefix).Append(line).Append(Environment.NewLine);
            }
        }

        if (exception.InnerException is not null)
        {
            builder.Append(prefix).Append("---> Inner exception:").Append(Environment.NewLine);
            AppendException(builder, exception.InnerException, indent + 1);
        }
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Maps a <see cref="LogLevel"/> to the fixed-width 3-letter abbreviation used in the file
    /// format (matching the example in the design document).
    /// </summary>
    private static string LevelAbbreviation(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "NON",
    };
}
