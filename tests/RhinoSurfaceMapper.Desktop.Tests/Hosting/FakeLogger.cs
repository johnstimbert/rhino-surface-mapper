using Microsoft.Extensions.Logging;

namespace RhinoSurfaceMapper.Desktop.Tests.Hosting;

/// <summary>
/// A minimal <see cref="ILogger{TCategoryName}"/> test double that records every
/// <see cref="Log{TState}"/> call instead of writing anywhere, so tests can assert on log
/// level/exception/message without fighting Moq's handling of the <c>LoggerMessage</c>
/// source-generated extension methods <c>TelemetryHostedService</c> and
/// <c>EliteDangerousProcessCheck</c> use.
/// </summary>
internal sealed class FakeLogger<T> : ILogger<T>
{
    /// <summary>One recorded log call.</summary>
    public sealed record Entry(LogLevel Level, EventId EventId, Exception? Exception, string Message);

    /// <summary>Every call recorded so far, in call order.</summary>
    public List<Entry> Entries { get; } = [];

    /// <inheritdoc />
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => true;

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(new Entry(logLevel, eventId, exception, formatter(state, exception)));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
