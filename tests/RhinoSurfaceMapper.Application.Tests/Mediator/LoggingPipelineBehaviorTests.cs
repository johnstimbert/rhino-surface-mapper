using FluentAssertions;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Tests.Mediator;

/// <summary>
/// Covers <see cref="LoggingPipelineBehavior{TInput,TOutput}"/>: it must log the elapsed
/// milliseconds and the <c>Result</c> value for both successful and "refused" outcomes.
/// </summary>
public sealed class LoggingPipelineBehaviorTests
{
    private sealed record Command : ICommand<Response>;

    private sealed record Response(string Result);

    [Fact]
    public async Task HandleAsync_logs_elapsed_milliseconds_and_result_on_success()
    {
        var logger = new RecordingLogger<LoggingPipelineBehavior<Command, Response>>();
        var behavior = new LoggingPipelineBehavior<Command, Response>(logger);

        var output = await behavior.HandleAsync(
            new Command(),
            () => Task.FromResult(new Response("Success")));

        output.Result.Should().Be("Success");

        var informationEntry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Subject;
        informationEntry.Message.Should().Contain("ms").And.Contain("Success");
    }

    [Fact]
    public async Task HandleAsync_logs_at_Warning_when_the_result_name_contains_Forbidden()
    {
        var logger = new RecordingLogger<LoggingPipelineBehavior<Command, Response>>();
        var behavior = new LoggingPipelineBehavior<Command, Response>(logger);

        await behavior.HandleAsync(
            new Command(),
            () => Task.FromResult(new Response("Forbidden")));

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Subject.Message.Should().Contain("Forbidden");
    }

    [Fact]
    public async Task HandleAsync_logs_at_Warning_when_the_result_name_contains_Denied()
    {
        var logger = new RecordingLogger<LoggingPipelineBehavior<Command, Response>>();
        var behavior = new LoggingPipelineBehavior<Command, Response>(logger);

        await behavior.HandleAsync(
            new Command(),
            () => Task.FromResult(new Response("AccessDenied")));

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning)
            .Subject.Message.Should().Contain("AccessDenied");
    }

    private sealed record Query : IQuery<PlainOutput>;

    /// <summary>An output type with no <c>Result</c> property, to cover the reflection fallback.</summary>
    private sealed record PlainOutput(string Value);

    [Fact]
    public async Task HandleAsync_logs_Result_as_n_slash_a_when_the_output_type_has_no_Result_property()
    {
        var logger = new RecordingLogger<LoggingPipelineBehavior<Query, PlainOutput>>();
        var behavior = new LoggingPipelineBehavior<Query, PlainOutput>(logger);

        var output = await behavior.HandleAsync(
            new Query(),
            () => Task.FromResult(new PlainOutput("ignored-by-the-logger")));

        output.Value.Should().Be("ignored-by-the-logger");
        var informationEntry = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Subject;
        informationEntry.Message.Should().Contain("Result=n/a");
    }

    [Fact]
    public async Task HandleAsync_logs_the_elapsed_milliseconds_as_a_non_negative_number()
    {
        var logger = new RecordingLogger<LoggingPipelineBehavior<Command, Response>>();
        var behavior = new LoggingPipelineBehavior<Command, Response>(logger);

        await behavior.HandleAsync(new Command(), () => Task.FromResult(new Response("Success")));

        var message = logger.Entries.Single(e => e.Level == LogLevel.Information).Message;
        var elapsedText = message.Split("in ")[1].Split(" ms")[0];
        int.Parse(elapsedText).Should().BeGreaterThanOrEqualTo(0);
    }
}
