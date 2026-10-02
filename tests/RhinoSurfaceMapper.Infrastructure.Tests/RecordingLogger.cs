using Microsoft.Extensions.Logging;

namespace RhinoSurfaceMapper.Infrastructure.Tests;

/// <summary>
/// Minimal <see cref="ILogger{TCategoryName}"/> test double that records every call so tests can
/// assert on the level and formatted message without a mocking framework expression tree (which
/// cannot express <see cref="ILogger.Log{TState}"/>'s generic signature cleanly). Lives in the
/// test project only, per the "test-friendly fakes are not production code" rule applied to
/// <see cref="RhinoSurfaceMapper.Domain.Interfaces.IClock"/>'s own <c>FakeClock</c>.
/// </summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<RecordedLogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Entries.Add(new RecordedLogEntry(logLevel, eventId, formatter(state, exception), exception));
    }
}

/// <summary>A single call captured by <see cref="RecordingLogger{T}"/>.</summary>
internal sealed record RecordedLogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);
