using FluentAssertions;
using RhinoSurfaceMapper.Infrastructure.Paths;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Paths;

/// <summary>
/// Covers <see cref="AppPaths"/> against the portable resolution rules reproduced from
/// <c>python/app_paths.py</c>: everything resolves beside a single base directory, directory
/// members are created lazily, and <see cref="RhinoSurfaceMapper.Domain.Interfaces.IAppPaths.OptionsPath"/>
/// is never created automatically.
/// </summary>
public sealed class AppPathsTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (System.IO.Directory.Exists(_tempRoot))
        {
            System.IO.Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Parameterless_constructor_resolves_relative_to_AppContext_BaseDirectory()
    {
        // This is the ".NET does not need to branch for sys.frozen" equivalence documented on
        // IAppPaths: AppContext.BaseDirectory already resolves correctly for framework-dependent,
        // self-contained, and single-file publishes alike, so the parameterless constructor is
        // the production entry point for every one of those modes.
        var paths = new AppPaths();

        paths.BaseDirectory.Should().Be(AppContext.BaseDirectory);
    }

    [Fact]
    public void MapsDirectory_is_a_MAPAS_subfolder_of_the_base_directory_and_is_created_on_access()
    {
        var paths = new AppPaths(_tempRoot);

        var mapsDirectory = paths.MapsDirectory;

        mapsDirectory.Should().Be(Path.Combine(_tempRoot, "MAPAS"));
        System.IO.Directory.Exists(mapsDirectory).Should().BeTrue();
    }

    [Fact]
    public void LogsDirectory_is_a_logs_subfolder_of_the_base_directory_and_is_created_on_access()
    {
        var paths = new AppPaths(_tempRoot);

        var logsDirectory = paths.LogsDirectory;

        logsDirectory.Should().Be(Path.Combine(_tempRoot, "logs"));
        System.IO.Directory.Exists(logsDirectory).Should().BeTrue();
    }

    [Fact]
    public void OptionsPath_sits_directly_beside_the_base_directory_and_is_not_created()
    {
        var paths = new AppPaths(_tempRoot);

        paths.OptionsPath.Should().Be(Path.Combine(_tempRoot, "options.json"));
        File.Exists(paths.OptionsPath).Should().BeFalse("IAppPaths documents that only directory members are created eagerly");
    }

    [Fact]
    public void Constructing_AppPaths_does_not_create_any_directory_until_a_property_is_read()
    {
        // Mirrors the Python original: maps_directory() creates the folder only when called,
        // not merely by importing the module / constructing the owning object.
        _ = new AppPaths(_tempRoot);

        System.IO.Directory.Exists(_tempRoot).Should().BeFalse();
    }

    [Fact]
    public void The_whole_layout_stays_rooted_under_a_single_base_directory_for_portability()
    {
        var paths = new AppPaths(_tempRoot);

        new[] { paths.MapsDirectory, paths.OptionsPath, paths.LogsDirectory }
            .Should().OnlyContain(path => path.StartsWith(_tempRoot, StringComparison.Ordinal));
    }
}
