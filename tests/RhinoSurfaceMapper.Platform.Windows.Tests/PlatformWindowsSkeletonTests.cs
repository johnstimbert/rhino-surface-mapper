using FluentAssertions;

namespace RhinoSurfaceMapper.Platform.Windows.Tests;

/// <summary>
/// Placeholder test for the Phase 0 skeleton. <c>RhinoSurfaceMapper.Platform.Windows</c>
/// introduces no public types in this phase (P/Invoke steering/radar interop arrives in a
/// later phase per the design's phased-delivery table); this test honestly asserts that
/// current, empty state rather than faking coverage for functionality that does not exist
/// yet, and will fail (as a deliberate reminder) the moment the project gains its first
/// public type without an accompanying test.
/// </summary>
public sealed class PlatformWindowsSkeletonTests
{
    [Fact]
    public void The_Platform_Windows_assembly_exposes_no_public_types_yet()
    {
        var testAssemblyDirectory = Path.GetDirectoryName(typeof(PlatformWindowsSkeletonTests).Assembly.Location)!;
        var platformAssemblyPath = Path.Combine(testAssemblyDirectory, "RhinoSurfaceMapper.Platform.Windows.dll");

        var loaded = System.Reflection.Assembly.LoadFrom(platformAssemblyPath);

        loaded.GetExportedTypes().Should().BeEmpty(
            "Phase 0 ships Platform.Windows as an empty, compiling skeleton; update this test when the first public type is added");
    }
}
