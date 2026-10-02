using FluentAssertions;
using RhinoSurfaceMapper.Infrastructure.Time;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Time;

/// <summary>
/// Smoke-tests <see cref="SystemClock"/>: it must return a current UTC timestamp and a
/// monotonically non-decreasing seconds counter.
/// </summary>
public sealed class SystemClockTests
{
    [Fact]
    public void UtcNow_returns_a_value_close_to_the_real_current_time()
    {
        var clock = new SystemClock();

        clock.UtcNow.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void MonotonicSeconds_never_decreases_between_two_successive_reads()
    {
        var clock = new SystemClock();

        var first = clock.MonotonicSeconds;
        var second = clock.MonotonicSeconds;

        second.Should().BeGreaterThanOrEqualTo(first);
    }
}
