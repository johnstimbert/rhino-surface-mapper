using FluentAssertions;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Tests.Mediator;

/// <summary>
/// Covers <see cref="ExceptionLoggingBehavior{TInput,TOutput}"/>: it must log the exception
/// at <see cref="LogLevel.Error"/> and rethrow the original instance unchanged.
/// </summary>
public sealed class ExceptionLoggingBehaviorTests
{
    private sealed record Command : ICommand<string>;

    [Fact]
    public async Task HandleAsync_logs_Error_and_rethrows_the_original_exception()
    {
        var logger = new RecordingLogger<ExceptionLoggingBehavior<Command, string>>();
        var behavior = new ExceptionLoggingBehavior<Command, string>(logger);
        var thrown = new InvalidOperationException("boom");

        var act = () => behavior.HandleAsync(new Command(), () => throw thrown);

        var assertion = await act.Should().ThrowAsync<InvalidOperationException>();
        assertion.Which.Should().BeSameAs(thrown);

        var errorEntry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        errorEntry.Exception.Should().BeSameAs(thrown);
    }

    [Fact]
    public async Task HandleAsync_returns_the_result_unchanged_when_next_succeeds()
    {
        var logger = new RecordingLogger<ExceptionLoggingBehavior<Command, string>>();
        var behavior = new ExceptionLoggingBehavior<Command, string>(logger);

        var output = await behavior.HandleAsync(new Command(), () => Task.FromResult("ok"));

        output.Should().Be("ok");
        logger.Entries.Should().BeEmpty();
    }
}
