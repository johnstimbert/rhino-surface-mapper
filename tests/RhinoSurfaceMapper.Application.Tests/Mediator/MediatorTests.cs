using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Tests.Mediator;

/// <summary>
/// Covers <see cref="RhinoSurfaceMapper.Application.Mediator.Mediator"/> dispatch for a
/// registered command, a registered query, and the "no handler registered" failure mode.
/// </summary>
public sealed class MediatorTests
{
    private sealed record PingCommand(string Text) : ICommand<string>;

    private sealed class PingCommandHandler : ICommandHandler<PingCommand, string>
    {
        public Task<string> HandleAsync(PingCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult($"command:{command.Text}");
    }

    private sealed record PingQuery(string Text) : IQuery<string>;

    private sealed class PingQueryHandler : IQueryHandler<PingQuery, string>
    {
        public Task<string> HandleAsync(PingQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult($"query:{query.Text}");
    }

    private sealed record UnregisteredCommand : ICommand<string>;

    private static IMediator BuildMediator(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IMediator, RhinoSurfaceMapper.Application.Mediator.Mediator>();
        services.AddScoped<ICommandHandler<PingCommand, string>, PingCommandHandler>();
        services.AddScoped<IQueryHandler<PingQuery, string>, PingQueryHandler>();
        configure?.Invoke(services);

        return services.BuildServiceProvider().GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task SendCommandAsync_dispatches_to_the_registered_handler()
    {
        var mediator = BuildMediator();

        var result = await mediator.SendCommandAsync<PingCommand, string>(new PingCommand("hello"));

        result.Should().Be("command:hello");
    }

    [Fact]
    public async Task SendQueryAsync_dispatches_to_the_registered_handler()
    {
        var mediator = BuildMediator();

        var result = await mediator.SendQueryAsync<PingQuery, string>(new PingQuery("world"));

        result.Should().Be("query:world");
    }

    [Fact]
    public async Task SendCommandAsync_throws_InvalidOperationException_when_no_handler_is_registered()
    {
        var mediator = BuildMediator();

        var act = () => mediator.SendCommandAsync<UnregisteredCommand, string>(new UnregisteredCommand());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{nameof(UnregisteredCommand)}*");
    }

    private sealed record UnregisteredQuery : IQuery<string>;

    [Fact]
    public async Task SendQueryAsync_throws_InvalidOperationException_when_no_handler_is_registered()
    {
        var mediator = BuildMediator();

        var act = () => mediator.SendQueryAsync<UnregisteredQuery, string>(new UnregisteredQuery());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{nameof(UnregisteredQuery)}*");
    }

    private sealed record TrackedCommand : ICommand<string>;

    private sealed class TrackedCommandHandler : ICommandHandler<TrackedCommand, string>
    {
        public Task<string> HandleAsync(TrackedCommand command, CancellationToken cancellationToken = default)
            => Task.FromResult("handler");
    }

    /// <summary>Records its name into a shared list both before and after calling <c>next()</c>, so tests can assert nesting order.</summary>
    private sealed class TrackingBehavior<TInput, TOutput>(string name, List<string> trace) : IPipelineBehavior<TInput, TOutput>
    {
        public async Task<TOutput> HandleAsync(TInput input, Func<Task<TOutput>> next, CancellationToken cancellationToken = default)
        {
            trace.Add($"{name}:before");
            var result = await next();
            trace.Add($"{name}:after");
            return result;
        }
    }

    [Fact]
    public async Task SendCommandAsync_runs_behaviors_as_an_onion_with_the_first_registered_behavior_outermost()
    {
        var trace = new List<string>();
        var mediator = BuildMediator(services =>
        {
            services.AddScoped<IPipelineBehavior<TrackedCommand, string>>(_ => new TrackingBehavior<TrackedCommand, string>("Outer", trace));
            services.AddScoped<IPipelineBehavior<TrackedCommand, string>>(_ => new TrackingBehavior<TrackedCommand, string>("Inner", trace));
            services.AddScoped<ICommandHandler<TrackedCommand, string>, TrackedCommandHandler>();
        });

        var result = await mediator.SendCommandAsync<TrackedCommand, string>(new TrackedCommand());

        result.Should().Be("handler");
        trace.Should().Equal(["Outer:before", "Inner:before", "Inner:after", "Outer:after"],
            "the first-registered behavior must wrap every later one, including the handler call itself");
    }
}
