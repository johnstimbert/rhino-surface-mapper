using FluentAssertions;
using RhinoSurfaceMapper.Domain.Constants;

namespace RhinoSurfaceMapper.Domain.Tests.Constants;

/// <summary>
/// Covers the invariants <see cref="LogEvents"/> must hold for log analysis to key off ids
/// reliably: every defined id is unique, and every defined id falls inside one of the
/// documented per-subsystem bands (1000-lifecycle in Phase 0; 2000-telemetry and the
/// 1000-lifecycle legacy-migration ids added in Phase 2).
/// </summary>
public sealed class LogEventsTests
{
    private static System.Reflection.FieldInfo[] AllEventIdFields() =>
        typeof(LogEvents).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

    [Fact]
    public void Every_defined_event_id_is_unique()
    {
        var values = AllEventIdFields().Select(f => (int)f.GetValue(null)!).ToList();

        values.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_defined_event_id_falls_within_a_documented_subsystem_band()
    {
        var values = AllEventIdFields().Select(f => (int)f.GetValue(null)!);

        values.Should().OnlyContain(value => value >= 1000 && value < 9000);
    }

    [Fact]
    public void At_least_one_id_is_defined_for_every_documented_lifecycle_event()
    {
        LogEvents.ApplicationStarting.Should().Be(1000);
        LogEvents.ApplicationStarted.Should().Be(1001);
        LogEvents.ApplicationStopping.Should().Be(1002);
        LogEvents.ApplicationStopped.Should().Be(1003);
        LogEvents.UnhandledException.Should().Be(1004);
        LogEvents.LogEntryDropped.Should().Be(1005);
    }
}
