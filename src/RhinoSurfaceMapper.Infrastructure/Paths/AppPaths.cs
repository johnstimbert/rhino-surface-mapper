using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Infrastructure.Paths;

/// <summary>
/// Production <see cref="IAppPaths"/> resolving the portable, app-local directory layout
/// described in <c>python/app_paths.py</c> and the design's "Packaging and distribution"
/// section. See <see cref="IAppPaths"/> for the resolution-order rationale.
/// </summary>
public sealed class AppPaths : IAppPaths
{
    private readonly Lazy<string> _mapsDirectory;
    private readonly Lazy<string> _logsDirectory;

    /// <summary>
    /// Creates an <see cref="AppPaths"/> rooted at <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public AppPaths() : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>
    /// Creates an <see cref="AppPaths"/> rooted at an explicit base directory. Used by tests
    /// to verify the portable layout without depending on the actual test-runner location.
    /// </summary>
    /// <param name="baseDirectory">The directory that stands in for the application folder.</param>
    public AppPaths(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        OptionsPath = Path.Combine(BaseDirectory, "options.json");

        // Lazy + directory creation on first access mirrors app_paths.maps_directory()'s
        // mkdir(parents=True, exist_ok=True) call: the directory is guaranteed to exist by
        // the time any caller reads the property, but it is not created merely by
        // constructing this class.
        _mapsDirectory = new Lazy<string>(() => EnsureDirectory(Path.Combine(BaseDirectory, "MAPAS")));
        _logsDirectory = new Lazy<string>(() => EnsureDirectory(Path.Combine(BaseDirectory, "logs")));
    }

    /// <inheritdoc />
    public string BaseDirectory { get; }

    /// <inheritdoc />
    public string MapsDirectory => _mapsDirectory.Value;

    /// <inheritdoc />
    public string OptionsPath { get; }

    /// <inheritdoc />
    public string LogsDirectory => _logsDirectory.Value;

    /// <summary>
    /// Creates <paramref name="directory"/> (and any missing parents) when it does not yet
    /// exist, then returns it unchanged — the .NET equivalent of
    /// <c>Path.mkdir(parents=True, exist_ok=True)</c>.
    /// </summary>
    private static string EnsureDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
