using System.Text;
using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Infrastructure.Logging;

/// <summary>
/// Owns the on-disk log file for <see cref="RollingFileLoggerProvider"/>: resolves the
/// active file name, rolls to a new segment on date change or size overflow, and prunes
/// retention (age and total size) on startup and on every roll.
/// </summary>
/// <remarks>
/// File naming follows the design exactly: <c>rhino-YYYYMMDD.log</c> for the first segment
/// of a calendar day, then <c>rhino-YYYYMMDD.001.log</c>, <c>.002.log</c>, … for subsequent
/// same-day segments created once the active file exceeds <c>MaxFileBytes</c>. All disk
/// failures are caught as the specific <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/>
/// types and degrade to "silently drop further writes" rather than propagating — a logger
/// must never crash the application it is observing.
/// </remarks>
internal sealed class LogFileRoller : IDisposable
{
    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly int _retentionDays;
    private readonly long _maxTotalBytes;
    private readonly IClock _clock;

    private FileStream? _stream;
    private DateOnly _currentDate;
    private int _currentSequence;
    private long _currentSize;
    private bool _faulted;

    /// <summary>
    /// Creates a roller rooted at <paramref name="directory"/>, pruning retention and
    /// opening (or resuming) today's active file immediately.
    /// </summary>
    public LogFileRoller(string directory, long maxFileBytes, int retentionDays, long maxTotalBytes, IClock clock)
    {
        _directory = directory;
        _maxFileBytes = maxFileBytes;
        _retentionDays = retentionDays;
        _maxTotalBytes = maxTotalBytes;
        _clock = clock;

        try
        {
            Directory.CreateDirectory(_directory);

            // Resolve which file is about to become active *before* pruning so that file can
            // be excluded from deletion explicitly. At construction time (unlike a mid-run
            // roll) nothing has opened/locked it yet, so without this exclusion a resumed
            // same-day segment that happens to be a legitimate prune candidate under the
            // configured retention budget would be deleted out from under the roller the
            // instant before it tries to resume appending to it.
            var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
            var resumeTarget = ResolveResumeTarget(today);
            PruneRetention(excludePath: resumeTarget.Path);
            OpenForDate(today, resumeTarget.Sequence);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The directory is not writable (read-only volume, permissions, locked by another
            // process). Degrade to a no-op sink rather than taking the application down with us.
            _faulted = true;
        }
    }

    /// <summary>Gets the full path of the file currently being written to, or an empty string if faulted.</summary>
    public string CurrentFilePath { get; private set; } = string.Empty;

    /// <summary>Gets the number of writes dropped because the directory could not be opened or written to.</summary>
    public long FaultedWriteCount { get; private set; }

    /// <summary>
    /// Appends <paramref name="text"/> to the active file, rolling to a new day or a new
    /// size-based segment first if needed. No-ops silently if the roller is faulted.
    /// </summary>
    public void Write(string text)
    {
        if (_faulted)
        {
            FaultedWriteCount++;
            return;
        }

        try
        {
            var today = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
            var bytes = Encoding.UTF8.GetBytes(text);

            if (today != _currentDate)
            {
                CloseCurrent();
                OpenForDate(today);
                PruneRetention();
            }
            else if (_currentSize + bytes.Length > _maxFileBytes)
            {
                CloseCurrent();
                _currentSequence++;
                OpenFile(_currentDate, _currentSequence);
                PruneRetention();
            }

            _stream!.Write(bytes, 0, bytes.Length);
            _currentSize += bytes.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _faulted = true;
            FaultedWriteCount++;
        }
    }

    /// <summary>Flushes the active file to disk, including the OS file-system cache (<c>fsync</c> equivalent).</summary>
    public void Flush()
    {
        try
        {
            _stream?.Flush(flushToDisk: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _faulted = true;
        }
    }

    /// <inheritdoc />
    public void Dispose() => CloseCurrent();

    private void OpenForDate(DateOnly date, int? resumeSequence = null)
    {
        _currentDate = date;

        // Resume the highest existing sequence for today so a process restart on the same
        // day appends to (or rolls past) the segment already in progress, rather than
        // truncating it. The constructor already resolved this (to protect the file from
        // retention pruning before it existed as an open handle); a roll never calls this
        // with a resumeSequence because it always starts a brand-new segment.
        _currentSequence = resumeSequence ?? ResolveResumeTarget(date).Sequence;
        OpenFile(date, _currentSequence);
    }

    /// <summary>
    /// Computes the file that a fresh <see cref="OpenForDate"/> call would resume for
    /// <paramref name="date"/> — the highest existing same-day sequence, or sequence 0 (the
    /// unsuffixed file) if none exists yet — without opening it. Used by the constructor to
    /// know which path must be excluded from the construction-time retention prune, since
    /// that prune runs before the file is opened (and therefore before it is protected by
    /// the OS file lock that protects it during a later roll).
    /// </summary>
    private (int Sequence, string Path) ResolveResumeTarget(DateOnly date)
    {
        var existingSequences = EnumerateLogFiles()
            .Where(f => f.Date == date)
            .Select(f => f.Sequence)
            .ToList();

        var sequence = existingSequences.Count == 0 ? 0 : existingSequences.Max();
        var fileName = sequence == 0
            ? $"rhino-{date:yyyyMMdd}.log"
            : $"rhino-{date:yyyyMMdd}.{sequence:000}.log";

        return (sequence, Path.Combine(_directory, fileName));
    }

    private void OpenFile(DateOnly date, int sequence)
    {
        var fileName = sequence == 0
            ? $"rhino-{date:yyyyMMdd}.log"
            : $"rhino-{date:yyyyMMdd}.{sequence:000}.log";

        CurrentFilePath = Path.Combine(_directory, fileName);
        _stream = new FileStream(CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _currentSize = _stream.Length;
    }

    private void CloseCurrent()
    {
        _stream?.Flush(flushToDisk: true);
        _stream?.Dispose();
        _stream = null;
    }

    /// <summary>
    /// Deletes log files older than <see cref="_retentionDays"/> days, then deletes the
    /// oldest remaining files until the combined size no longer exceeds
    /// <see cref="_maxTotalBytes"/>. Runs on construction and after every roll.
    /// </summary>
    /// <param name="excludePath">
    /// A file that must never be considered for deletion by this pass, regardless of age or
    /// the size budget — used only by the constructor, to protect the file it is about to
    /// resume appending to before that file is opened (and therefore before the OS file lock
    /// that otherwise protects an active file during a roll exists). <see langword="null"/>
    /// for every other caller, since a roll already holds the active file open/locked by the
    /// time it prunes.
    /// </param>
    private void PruneRetention(string? excludePath = null)
    {
        var cutoffDate = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime).AddDays(-_retentionDays);
        var files = EnumerateLogFiles()
            .Where(f => excludePath is null || !string.Equals(f.Path, excludePath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Date).ThenBy(f => f.Sequence).ToList();

        foreach (var file in files.Where(f => f.Date < cutoffDate).ToList())
        {
            TryDelete(file.Path);
            files.Remove(file);
        }

        var totalBytes = files.Sum(f => f.SizeBytes);
        foreach (var file in files)
        {
            if (totalBytes <= _maxTotalBytes)
            {
                break;
            }

            TryDelete(file.Path);
            totalBytes -= file.SizeBytes;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort retention: a file locked by another process (e.g. a text editor
            // with the log open) is left in place and reconsidered on the next prune pass.
        }
    }

    private IEnumerable<LogFileInfo> EnumerateLogFiles()
    {
        if (!Directory.Exists(_directory))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(_directory, "rhino-*.log"))
        {
            if (TryParseFileName(Path.GetFileName(path), out var date, out var sequence))
            {
                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    size = 0;
                }

                yield return new LogFileInfo(path, date, sequence, size);
            }
        }
    }

    /// <summary>
    /// Parses <c>rhino-YYYYMMDD.log</c> or <c>rhino-YYYYMMDD.NNN.log</c> into its date and
    /// sequence number (0 for the unsuffixed first-of-day file).
    /// </summary>
    private static bool TryParseFileName(string fileName, out DateOnly date, out int sequence)
    {
        date = default;
        sequence = 0;

        const string prefix = "rhino-";
        const string suffix = ".log";

        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var middle = fileName[prefix.Length..^suffix.Length];
        var parts = middle.Split('.');

        if (parts.Length is not (1 or 2))
        {
            return false;
        }

        if (!DateOnly.TryParseExact(parts[0], "yyyyMMdd", out date))
        {
            return false;
        }

        if (parts.Length == 2 && !int.TryParse(parts[1], out sequence))
        {
            return false;
        }

        return true;
    }

    private readonly record struct LogFileInfo(string Path, DateOnly Date, int Sequence, long SizeBytes);
}
