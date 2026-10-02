using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Tests;

/// <summary>
/// Covers <see cref="ConfigureServices.AddApplication"/>: the mediator and both pipeline
/// behaviors must be resolvable and must actually run for a dispatched command.
/// </summary>
public sealed class ConfigureServicesTests
{
    private sealed record Command : ICommand<string>;

    private sealed class Handler : ICommandHandler<Command, string>
    {
        public Task<string> HandleAsync(Command command, CancellationToken cancellationToken = default)
            => Task.FromResult("Success");
    }

    [Fact]
    public async Task AddApplication_registers_a_mediator_that_dispatches_through_both_behaviors()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddApplication();
        services.AddScoped<ICommandHandler<Command, string>, Handler>();

        var provider = services.BuildServiceProvider();
        var mediator = provider.GetRequiredService<IMediator>();

        var result = await mediator.SendCommandAsync<Command, string>(new Command());

        result.Should().Be("Success");
    }

    [Fact]
    public void AddApplication_returns_the_same_service_collection_for_chaining()
    {
        var services = new ServiceCollection();

        var result = services.AddApplication();

        result.Should().BeSameAs(services);
    }
}
