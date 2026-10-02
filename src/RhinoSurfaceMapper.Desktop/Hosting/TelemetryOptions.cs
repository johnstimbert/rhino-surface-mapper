namespace RhinoSurfaceMapper.Desktop.Hosting;

/// <summary>
/// Binds the <c>Telemetry</c> configuration section, letting an operator override the
/// <c>Status.json</c> path without a rebuild (mirroring the Python constructor's
/// <c>status_path=None</c> override parameter, which tests use to point at a temporary file).
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>
    /// Explicit override for the <c>Status.json</c> path. When <see langword="null"/> (the
    /// default), <see cref="TelemetryHostedService"/> resolves Elite Dangerous' standard
    /// location: <c>%USERPROFILE%\Saved Games\Frontier Developments\Elite Dangerous\Status.json</c>.
    /// </summary>
    public string? StatusPath { get; set; }
}
