namespace RhinoSurfaceMapper.Domain.Interfaces;

/// <summary>
/// Resolves the portable, app-local filesystem layout described in
/// <c>python/app_paths.py</c> and the design's "Packaging and distribution" section:
/// everything the application reads or writes lives beside the executable, nothing is
/// written to the registry or the user profile, and the whole folder remains
/// xcopy-deployable.
/// </summary>
/// <remarks>
/// <para>
/// Resolution order mirrors the Python <c>sys.frozen</c> branch exactly, but the .NET
/// implementation does not need to branch explicitly: <see cref="System.AppContext.BaseDirectory"/>
/// already resolves to the folder containing the running host in every supported
/// publish mode (framework-dependent, self-contained, and single-file), which is the
/// direct equivalent of "next to <c>sys.executable</c> when frozen, next to the source
/// module otherwise".
/// </para>
/// <para>
/// Directory-valued members create their directory on first access (<c>mkdir(parents=True,
/// exist_ok=True)</c> in the Python original); <see cref="OptionsPath"/> is a file path
/// and is never created by this interface — the preferences repository owns that.
/// </para>
/// </remarks>
public interface IAppPaths
{
    /// <summary>
    /// Gets the application's base directory — the folder containing the running host
    /// executable, used as the root for every other portable path.
    /// </summary>
    string BaseDirectory { get; }

    /// <summary>
    /// Gets the <c>MAPAS</c> directory holding per-PML planetary map JSON files,
    /// creating it if it does not yet exist. Ported from <c>app_paths.maps_directory()</c>.
    /// </summary>
    string MapsDirectory { get; }

    /// <summary>
    /// Gets the full path to <c>options.json</c>, the persisted user-preferences file,
    /// located directly beside the executable. Ported from the <c>OPTIONS_PATH</c>
    /// module-level constant in <c>rhino_surface_mapper_qt.py</c>.
    /// </summary>
    string OptionsPath { get; }

    /// <summary>
    /// Gets the <c>logs</c> directory used by the rolling-file logger provider,
    /// creating it if it does not yet exist.
    /// </summary>
    string LogsDirectory { get; }
}
