using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Domain.Tests.TestSupport;

/// <summary>
/// Deterministic <see cref="IClock"/> test double: <see cref="UtcNow"/> and
/// <see cref="MonotonicSeconds"/> are both settable, so tests can simulate date rollover and
/// elapsed-time scenarios (overlay blink timing in particular) without sleeping real wall-clock
/// time. Kept in the test project only, per the rule that test-friendly fakes do not belong in
/// production code.
/// </summary>
internal sealed class FakeClock : IClock
{
    /// <summary>Creates a clock starting at <paramref name="utcNow"/> (default: a fixed, arbitrary instant).</summary>
    public FakeClock(DateTimeOffset? utcNow = null)
    {
        UtcNow = utcNow ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; }

    /// <inheritdoc />
    public double MonotonicSeconds { get; set; }
}
