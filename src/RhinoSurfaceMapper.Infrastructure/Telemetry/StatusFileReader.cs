using System.Text.Json;
using RhinoSurfaceMapper.Application.Interfaces;

namespace RhinoSurfaceMapper.Infrastructure.Telemetry;

/// <summary>
/// <see cref="IStatusTelemetryReader"/> implementation reading Elite Dangerous' <c>Status.json</c>
/// straight off disk, ported from <c>elite_dangerous.status.read_status_if_changed</c>.
/// </summary>
/// <remarks>
/// Deliberately stateless and dependency-free beyond the file system: the caller (a later-phase
/// telemetry hosted service) owns the previous change token and decides polling cadence and
/// retry policy. This type's only contract is "tell the truth, fast" — no retry, no locking, no
/// swallowed exception, exactly matching the Python module's own documented contract.
/// </remarks>
public sealed class StatusFileReader : IStatusTelemetryReader
{
    /// <inheritdoc />
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
    /// <exception cref="JsonException">
    /// The file content is not valid JSON — typically because the game producer is mid-write.
    /// Not retried here; the caller's polling loop decides retry timing.
    /// </exception>
    public StatusReadResult? TryReadIfChanged(string path, long? previousMtimeTicks, bool force = false)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Status file not found: '{path}'.", path);
        }

        // File.GetLastWriteTimeUtc's Ticks is the complete change-detection contract for
        // callers — the .NET analogue of Python's `st_mtime_ns`, in 100 ns units instead of ns.
        long mtimeTicks = File.GetLastWriteTimeUtc(path).Ticks;
        if (!force && mtimeTicks == previousMtimeTicks)
        {
            return null;
        }

        // Reads the whole file as UTF-8 and lets a partial-write JSON failure propagate rather
        // than masking it — matching the Python contract exactly: "this module deliberately
        // does not retry partial writes."
        string content = File.ReadAllText(path, System.Text.Encoding.UTF8);
        var document = JsonDocument.Parse(content);
        return new StatusReadResult(mtimeTicks, document);
    }
}
