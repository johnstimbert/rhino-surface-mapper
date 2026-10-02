using System.Reflection;
using FluentAssertions;
using RhinoSurfaceMapper.Desktop.Hosting;

namespace RhinoSurfaceMapper.Desktop.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="EliteDangerousProcessCheck"/>'s one-second throttle, per the
/// design's "the game-process check is throttled to once per second, as today" statement. The
/// actual process query (<see cref="System.Diagnostics.Process.GetProcessesByName"/>) is a
/// static OS call that cannot be substituted without changing production code, so these tests
/// drive the throttle purely through its injected <see cref="FakeClock"/> and verify the private
/// <c>_nextCheckAtMonotonicSeconds</c> bookkeeping field via reflection — a deliberate,
/// documented white-box exception to this suite's usual black-box style, since no public seam
/// exists to observe "did this call re-query the OS" any other way.
/// </summary>
public sealed class EliteDangerousProcessCheckTests
{
    private static double GetNextCheckAt(EliteDangerousProcessCheck check)
    {
        FieldInfo field = typeof(EliteDangerousProcessCheck).GetField("_nextCheckAtMonotonicSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (double)field.GetValue(check)!;
    }

    [Fact]
    public void IsRunning_advances_the_throttle_window_by_exactly_one_second_on_a_real_check()
    {
        var clock = new FakeClock { MonotonicSeconds = 0.0 };
        var check = new EliteDangerousProcessCheck(clock);

        check.IsRunning();

        GetNextCheckAt(check).Should().Be(1.0, "the first call always performs a real check and schedules the next one exactly one second later");
    }

    [Fact]
    public void IsRunning_does_not_re_query_or_reschedule_while_still_inside_the_one_second_window()
    {
        var clock = new FakeClock { MonotonicSeconds = 0.0 };
        var check = new EliteDangerousProcessCheck(clock);

        check.IsRunning();
        double nextCheckAfterFirstCall = GetNextCheckAt(check);

        clock.MonotonicSeconds = 0.999;
        bool secondResult = check.IsRunning();

        GetNextCheckAt(check).Should().Be(nextCheckAfterFirstCall,
            "a call still inside the throttle window must return the cached result without rescheduling the next check");
        secondResult.Should().Be(check.IsRunning(), "repeated calls inside the same window must keep returning the identical cached value");
    }

    [Fact]
    public void IsRunning_performs_a_fresh_check_once_the_one_second_window_has_elapsed()
    {
        var clock = new FakeClock { MonotonicSeconds = 0.0 };
        var check = new EliteDangerousProcessCheck(clock);

        check.IsRunning();
        clock.MonotonicSeconds = 1.0;
        check.IsRunning();

        GetNextCheckAt(check).Should().Be(2.0, "once the window has elapsed, the next call must perform a fresh check and reschedule exactly one second ahead again");
    }

    [Fact]
    public void IsRunning_treats_many_rapid_calls_within_the_window_as_a_single_logical_check()
    {
        var clock = new FakeClock { MonotonicSeconds = 0.0 };
        var check = new EliteDangerousProcessCheck(clock);

        check.IsRunning();
        double nextCheckAt = GetNextCheckAt(check);

        for (int i = 0; i < 1000; i++)
        {
            check.IsRunning();
        }

        GetNextCheckAt(check).Should().Be(nextCheckAt,
            "a thousand calls at a frozen clock must not move the throttle window at all, proving the gate is based purely on elapsed monotonic time, not call count");
    }
}
