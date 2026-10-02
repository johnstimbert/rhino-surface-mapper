using System.Text.Json;

namespace RhinoSurfaceMapper.Application.Interfaces;

/// <summary>
/// Reads Elite Dangerous' <c>Status.json</c> only when it changed, ported from
/// <c>elite_dangerous.status.read_status_if_changed</c>. Declared here (not in
/// <c>Domain</c>) because it is an Application-facing service boundary the telemetry hosted
/// service (a later phase) depends on, exactly as the design's "Application services and
/// interfaces" table assigns it; <c>Infrastructure</c>'s <c>StatusFileReader</c> is its only
/// implementation.
/// </summary>
public interface IStatusTelemetryReader
{
    /// <summary>
    /// Reads <paramref name="path"/> when its last-write-time changed since
    /// <paramref name="previousMtimeTicks"/>, or unconditionally when <paramref name="force"/>
    /// is <see langword="true"/>.
    /// </summary>
    /// <param name="path">File-system path to the current <c>Status.json</c> document.</param>
    /// <param name="previousMtimeTicks">
    /// The <see cref="StatusReadResult.MtimeTicks"/> value from the previous successful read,
    /// or <see langword="null"/> when no version has been read yet — the .NET analogue of
    /// Python's <c>previous_mtime_ns</c>, using <see cref="DateTime.Ticks"/> (100 ns units)
    /// in place of nanoseconds.
    /// </param>
    /// <param name="force">When <see langword="true"/>, reads even if the timestamp token did not change.</param>
    /// <returns>
    /// <see langword="null"/> when the timestamp is unchanged and <paramref name="force"/> is
    /// <see langword="false"/>; otherwise a <see cref="StatusReadResult"/> the caller must
    /// dispose once done with its <see cref="StatusReadResult.Document"/>.
    /// </returns>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
    /// <exception cref="JsonException">
    /// The file content is not valid JSON — for example because the game producer is mid-write.
    /// This method deliberately does not retry partial writes; the caller's polling loop decides
    /// retry timing, matching the Python contract exactly.
    /// </exception>
    StatusReadResult? TryReadIfChanged(string path, long? previousMtimeTicks, bool force = false);
}

/// <summary>
/// One successful <see cref="IStatusTelemetryReader.TryReadIfChanged"/> read: the change-token
/// observed and the parsed document, ported from Python's <c>(mtime_ns, payload)</c> tuple.
/// </summary>
/// <param name="MtimeTicks">
/// The file's <see cref="FileSystemInfo.LastWriteTimeUtc"/> expressed as <see cref="DateTime.Ticks"/>
/// at the moment of this read — the change token a subsequent call passes back as
/// <c>previousMtimeTicks</c>.
/// </param>
/// <param name="Document">
/// The parsed <c>Status.json</c> document. Ownership transfers to the caller, which must
/// <see cref="IDisposable.Dispose"/> this result (or its <see cref="Document"/> directly) once
/// its fields have been read, since <see cref="JsonDocument"/> owns pooled unmanaged memory.
/// </param>
public sealed record StatusReadResult(long MtimeTicks, JsonDocument Document) : IDisposable
{
    /// <summary>Disposes the owned <see cref="Document"/>.</summary>
    public void Dispose() => Document.Dispose();
}
