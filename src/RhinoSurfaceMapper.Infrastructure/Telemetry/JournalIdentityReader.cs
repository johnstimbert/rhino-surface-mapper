using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Domain.Constants;

namespace RhinoSurfaceMapper.Infrastructure.Telemetry;

/// <summary>
/// <see cref="IJournalIdentityReader"/> implementation tracking the player's current star
/// system/body from Elite Dangerous' rolling <c>Journal.&lt;timestamp&gt;.&lt;sequence&gt;.log</c>
/// files, ported from <c>elite_dangerous/journal.py</c>.
/// </summary>
/// <remarks>
/// One instance owns mutable read-position state (<see cref="_activeJournalPath"/>,
/// <see cref="_byteOffset"/>, <see cref="_pendingFragment"/>, <see cref="_identity"/>) across
/// calls, exactly like the Python reader's instance attributes.
/// <para>
/// <b>Single-consumer only, not thread-safe.</b> This type is registered as a DI singleton on
/// the assumption that exactly one caller (a future telemetry hosted service) polls it
/// sequentially. It performs no locking around its mutable fields. Introducing a second
/// concurrent caller — for example, a future diagnostics panel that also wants "current
/// identity" — will race on <see cref="_byteOffset"/>/<see cref="_pendingFragment"/> and
/// silently corrupt the incremental read position (skipped or duplicated journal lines), not
/// throw a visible error. Any future second consumer must share this same instance (through DI)
/// rather than constructing its own, since even two instances reading the same files
/// independently would each maintain their own (inconsistent) offset/fragment state; a second
/// *concurrent* caller to <see cref="CurrentIdentity"/> on the same instance is the specific
/// hazard this remark warns against.
/// </para>
/// </remarks>
public sealed class JournalIdentityReader : IJournalIdentityReader
{
    // Journal.2024-01-02T030405.01.log — the timestamp segment uses Elite Dangerous' own
    // "yyyy-MM-ddTHHmmss" layout (no separators in the time part), not ISO-8601 proper.
    private static readonly Regex JournalFileNamePattern = new(
        @"^Journal\.(?<timestamp>\d{4}-\d{2}-\d{2}T\d{6})\.(?<sequence>\d+)\.log$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string JournalTimestampFormat = "yyyy-MM-ddTHHmmss";

    private readonly string _journalDirectory;
    private readonly ILogger<JournalIdentityReader> _logger;

    private string? _activeJournalPath;
    private long _byteOffset;
    private byte[] _pendingFragment = [];
    private JournalIdentity? _identity;

    /// <summary>
    /// Creates the reader, defaulting <paramref name="journalDirectory"/> to Elite Dangerous'
    /// standard Saved Games location when not supplied (matching <c>journal.py</c>'s own
    /// default, which reads <c>%USERPROFILE%</c> rather than hard-coding a drive letter).
    /// </summary>
    public JournalIdentityReader(ILogger<JournalIdentityReader> logger, string? journalDirectory = null)
    {
        _logger = logger;
        _journalDirectory = journalDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Saved Games",
            "Frontier Developments",
            "Elite Dangerous");
    }

    /// <inheritdoc />
    public JournalIdentity? CurrentIdentity()
    {
        string? latest = FindLatestJournalFile();
        if (latest is null)
        {
            return _identity;
        }

        if (!string.Equals(latest, _activeJournalPath, StringComparison.OrdinalIgnoreCase))
        {
            // A new Journal file means a new game session: Elite Dangerous always re-emits a
            // Location/FSDJump event near the top of each file, so starting from offset 0 with
            // a cleared identity is correct, not a loss of state.
            _activeJournalPath = latest;
            _byteOffset = 0;
            _pendingFragment = [];
            _identity = null;
        }

        byte[] chunk;
        try
        {
            var fileInfo = new FileInfo(latest);
            if (!fileInfo.Exists)
            {
                return _identity;
            }

            if (fileInfo.Length < _byteOffset)
            {
                // The file shrank underneath us (log rotation/truncation) — resetting the
                // offset/pending fragment is the only safe response, matching Python's
                // shrink-detection reset.
                _byteOffset = 0;
                _pendingFragment = [];
            }

            using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(_byteOffset, SeekOrigin.Begin);
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            chunk = memory.ToArray();
        }
        catch (IOException)
        {
            return _identity;
        }
        catch (UnauthorizedAccessException)
        {
            return _identity;
        }

        if (chunk.Length == 0)
        {
            return _identity;
        }

        _byteOffset += chunk.Length;

        byte[] data = _pendingFragment.Length == 0 ? chunk : [.. _pendingFragment, .. chunk];
        var lines = SplitLinesKeepingTerminators(data);

        if (lines.Count > 0 && !EndsWithLineTerminator(lines[^1]))
        {
            // The writer flushed mid-line; keep the incomplete tail for the next poll instead of
            // discarding or mis-parsing it, matching Python's `_pending` fragment retry.
            _pendingFragment = lines[^1];
            lines.RemoveAt(lines.Count - 1);
        }
        else
        {
            _pendingFragment = [];
        }

        foreach (byte[] line in lines)
        {
            ConsumeLine(line);
        }

        return _identity;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _activeJournalPath = null;
        _byteOffset = 0;
        _pendingFragment = [];
        _identity = null;
    }

    /// <summary>
    /// Selects the Journal file to tail: highest (parsed timestamp, sequence number, file
    /// modification time) wins, in that priority order — the exact tie-break Python's
    /// <c>_latest_journal_path</c> applies when two files share a timestamp+sequence (rare, but
    /// observed with rapid relaunches) or, degenerately, when parsing can't distinguish them.
    /// </summary>
    private string? FindLatestJournalFile()
    {
        try
        {
            if (!Directory.Exists(_journalDirectory))
            {
                return null;
            }

            (DateTime Timestamp, int Sequence, long MtimeTicks, string Path)? best = null;
            foreach (string path in Directory.EnumerateFiles(_journalDirectory, "Journal.*.log"))
            {
                Match match = JournalFileNamePattern.Match(Path.GetFileName(path));
                if (!match.Success)
                {
                    continue;
                }

                if (!DateTime.TryParseExact(
                        match.Groups["timestamp"].Value,
                        JournalTimestampFormat,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime timestamp))
                {
                    continue;
                }

                int sequence = int.Parse(match.Groups["sequence"].Value, CultureInfo.InvariantCulture);
                long mtimeTicks = File.GetLastWriteTimeUtc(path).Ticks;
                var candidate = (timestamp, sequence, mtimeTicks, path);

                if (best is null
                    || candidate.timestamp > best.Value.Timestamp
                    || (candidate.timestamp == best.Value.Timestamp && candidate.sequence > best.Value.Sequence)
                    || (candidate.timestamp == best.Value.Timestamp && candidate.sequence == best.Value.Sequence && candidate.mtimeTicks > best.Value.MtimeTicks))
                {
                    best = candidate;
                }
            }

            return best?.Path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses one raw Journal line (still carrying its trailing terminator, if any) and updates
    /// <see cref="_identity"/> only for <c>Location</c>/<c>FSDJump</c> events, matching Python's
    /// explicit event-name allow-list — every other event is ignored entirely, not merely
    /// deferred.
    /// </summary>
    private void ConsumeLine(byte[] rawLine)
    {
        ReadOnlySpan<byte> trimmed = TrimLineTerminator(rawLine);
        if (trimmed.IsEmpty)
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed.ToArray());
        }
        catch (JsonException)
        {
            LogSkippedRecord("invalid JSON");
            return;
        }
        catch (DecoderFallbackException)
        {
            LogSkippedRecord("invalid UTF-8");
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("event", out var eventElement)
                || eventElement.ValueKind != JsonValueKind.String)
            {
                LogSkippedRecord("not a recognised event object");
                return;
            }

            string? eventName = eventElement.GetString();
            if (eventName != "Location" && eventName != "FSDJump")
            {
                return;
            }

            if (!root.TryGetProperty("StarSystem", out var starSystemElement)
                || starSystemElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(starSystemElement.GetString()))
            {
                LogSkippedRecord("missing StarSystem");
                return;
            }

            string starSystem = starSystemElement.GetString()!;
            string? body = root.TryGetProperty("Body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()
                : _identity?.Body;

            _identity = new JournalIdentity(starSystem, body);
        }
    }

    private void LogSkippedRecord(string reason) =>
        _logger.LogDebug(new EventId(LogEvents.JournalRecordSkipped, nameof(LogEvents.JournalRecordSkipped)),
            "Skipped malformed Journal record: {Reason}", reason);

    /// <summary>
    /// Splits raw bytes on line boundaries (<c>\n</c>, <c>\r</c>, <c>\r\n</c>) while keeping each
    /// terminator attached to the line it ends — the byte-level analogue of Python's
    /// <c>bytes.splitlines(keepends=True)</c>, restricted to the ASCII terminators Journal files
    /// actually use.
    /// </summary>
    private static List<byte[]> SplitLinesKeepingTerminators(byte[] data)
    {
        var lines = new List<byte[]>();
        int start = 0;
        int i = 0;
        while (i < data.Length)
        {
            if (data[i] == (byte)'\n')
            {
                lines.Add(data[start..(i + 1)]);
                start = i + 1;
                i++;
            }
            else if (data[i] == (byte)'\r')
            {
                int end = i + 1;
                if (end < data.Length && data[end] == (byte)'\n')
                {
                    end++;
                }

                lines.Add(data[start..end]);
                start = end;
                i = end;
            }
            else
            {
                i++;
            }
        }

        if (start < data.Length)
        {
            lines.Add(data[start..]);
        }

        return lines;
    }

    private static bool EndsWithLineTerminator(byte[] line) =>
        line.Length > 0 && (line[^1] == (byte)'\n' || line[^1] == (byte)'\r');

    private static ReadOnlySpan<byte> TrimLineTerminator(byte[] line)
    {
        int end = line.Length;
        while (end > 0 && (line[end - 1] == (byte)'\n' || line[end - 1] == (byte)'\r'))
        {
            end--;
        }

        return line.AsSpan(0, end);
    }
}
