namespace RhinoSurfaceMapper.Domain.Interfaces;

/// <summary>
/// Abstracts wall-clock and monotonic time so that timing-sensitive domain rules
/// (radar wave expansion, steering pulse cooldowns, overlay blink timing, turn-trial
/// duration) can be driven deterministically from tests, exactly as the Python
/// implementation's dependency on <c>time.time()</c> / <c>time.monotonic()</c> is
/// replaced with an injectable fake in <c>test_radar.py</c> / <c>test_steering.py</c>.
/// </summary>
/// <remarks>
/// Production code must obtain time only through this interface. Reading
/// <see cref="DateTimeOffset.UtcNow"/> or <see cref="Environment.TickCount64"/> directly
/// from a domain service makes the resulting behaviour unit-untestable and breaks parity
/// with the Python reference tests, which inject a controllable clock.
/// </remarks>
public interface IClock
{
    /// <summary>
    /// Gets the current wall-clock time in UTC. Used for persisted timestamps
    /// (map <c>created_at</c>/<c>last_saved_at</c>, log entry timestamps) where an
    /// absolute, human-meaningful instant is required.
    /// </summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Gets a monotonically increasing time reference, in seconds, suitable only for
    /// measuring elapsed durations. Mirrors Python's <c>time.monotonic()</c>: it has no
    /// fixed epoch and must never be persisted or compared across process restarts.
    /// </summary>
    /// <remarks>
    /// Backed by <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> in
    /// <c>SystemClock</c> so that radar pulse expansion, steering pulse/cooldown timing
    /// and turn-trial duration measurements are immune to system clock adjustments
    /// (NTP sync, daylight saving, user time changes) — the same guarantee
    /// <c>time.monotonic()</c> provides in the Python implementation.
    /// </remarks>
    double MonotonicSeconds { get; }
}
