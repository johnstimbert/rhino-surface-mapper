namespace RhinoSurfaceMapper.Application.Interfaces;

/// <summary>
/// Incrementally resolves the commander's current system/body identity from the active Elite
/// Dangerous Journal file, ported from <c>elite_dangerous.journal.JournalIdentityReader</c>.
/// Declared here (not in <c>Domain</c>) for the same reason as <see cref="IStatusTelemetryReader"/>:
/// it is an Application-facing service boundary a later-phase hosted service depends on;
/// <c>Infrastructure</c>'s <c>JournalIdentityReader</c> is its only implementation.
/// </summary>
public interface IJournalIdentityReader
{
    /// <summary>
    /// Consumes newly appended Journal records and returns the latest identity.
    /// </summary>
    /// <returns>
    /// The most recent <see cref="JournalIdentity"/> observed in the active file, or
    /// <see langword="null"/> when no authoritative <c>Location</c>/<c>FSDJump</c> event has
    /// been read yet (including when no Journal file exists at all).
    /// </returns>
    /// <remarks>
    /// A newly selected "latest" file resets all session state (offset, pending fragment,
    /// identity) because it represents a new game/logging session. A file that shrank since the
    /// last read is treated the same way: rotation, replacement or truncation invalidated the
    /// previous byte offset. Incomplete trailing JSON lines are buffered and retried on the next
    /// call instead of being parsed prematurely — see the implementation for the exact rules.
    /// </remarks>
    JournalIdentity? CurrentIdentity();

    /// <summary>
    /// Discards the active file selection, byte offset, pending fragment and last-known
    /// identity, as if this reader had just been constructed.
    /// </summary>
    void Reset();
}

/// <summary>
/// The latest known Journal-derived location identity, ported from
/// <c>elite_dangerous.journal.JournalIdentity</c>.
/// </summary>
/// <param name="System">Non-empty, trimmed star-system name.</param>
/// <param name="Body">
/// Trimmed body name, or <see langword="null"/> when the authoritative event that established
/// <paramref name="System"/> did not also supply one.
/// </param>
public sealed record JournalIdentity(string System, string? Body);
