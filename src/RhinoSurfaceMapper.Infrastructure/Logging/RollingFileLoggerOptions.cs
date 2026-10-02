namespace RhinoSurfaceMapper.Infrastructure.Logging;

/// <summary>
/// Configuration for <see cref="RollingFileLoggerProvider"/>, bound from the
/// <c>Logging:File</c> section of <c>appsettings.json</c> via <c>IOptions&lt;RollingFileLoggerOptions&gt;</c>.
/// </summary>
public sealed class RollingFileLoggerOptions
{
    /// <summary>
    /// Gets or sets the directory log files are written to. A relative value is resolved
    /// against <see cref="Domain.Interfaces.IAppPaths.BaseDirectory"/> so the default
    /// ("logs") keeps the install portable; an absolute value is honoured as-is for the rare
    /// case an operator deliberately wants logs outside the app folder.
    /// </summary>
    public string Directory { get; set; } = "logs";

    /// <summary>
    /// Gets or sets the maximum age, in days, a log file is kept before being deleted during
    /// retention pruning. Default 14, matching N5 ("bounded disk usage").
    /// </summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>
    /// Gets or sets the size, in bytes, at which the active log file is rolled to a new,
    /// sequence-suffixed segment. Default 10 MiB (<c>10 * 1024 * 1024</c>).
    /// </summary>
    public long MaxFileBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Gets or sets the maximum combined size, in bytes, of all retained log files. Oldest
    /// files are deleted first when this is exceeded. Default 100 MiB (<c>100 * 1024 * 1024</c>).
    /// </summary>
    public long MaxTotalBytes { get; set; } = 100 * 1024 * 1024;
}
