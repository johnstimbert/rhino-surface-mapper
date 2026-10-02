using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Desktop.Tests.Hosting;

/// <summary>
/// A fully controllable <see cref="IClock"/> test double: both <see cref="UtcNow"/> and
/// <see cref="MonotonicSeconds"/> are plain mutable fields the test sets explicitly, so
/// timing-dependent production code (the throttle in <c>EliteDangerousProcessCheck</c>, the
/// <c>timestamp</c> fallback in <c>StatusSampleMapper</c>) can be driven deterministically
/// instead of racing the real wall clock.
/// </summary>
internal sealed class FakeClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;

    /// <inheritdoc />
    public double MonotonicSeconds { get; set; }
}
