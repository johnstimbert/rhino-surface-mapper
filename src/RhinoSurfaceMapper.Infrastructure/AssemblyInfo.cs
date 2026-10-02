using System.Runtime.CompilerServices;

// Exposes internal logging-subsystem types (LogEntry, LogLineFormatter, LogFileRoller,
// RollingFileLoggerProvider internals) to Infrastructure.Tests so the rolling/retention/
// format behaviour can be unit tested directly instead of only through the public
// ILoggerProvider surface.
[assembly: InternalsVisibleTo("RhinoSurfaceMapper.Infrastructure.Tests")]
