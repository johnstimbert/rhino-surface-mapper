using Microsoft.Extensions.DependencyInjection;

namespace RhinoSurfaceMapper.Application.Mediator;

/// <summary>
/// Resolves and dispatches commands and queries to their registered handlers,
/// with pipeline behavior support. Ported verbatim from
/// <c>TheUnofficialWythevilleApp.Web.Application.Mediator.Mediator</c>.
/// </summary>
public sealed class Mediator(IServiceProvider provider) : IMediator
{
    /// <inheritdoc />
    public async Task<TResult> SendCommandAsync<TCommand, TResult>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand<TResult>
    {
        var handler = provider.GetService<ICommandHandler<TCommand, TResult>>()
            ?? throw new InvalidOperationException($"No handler registered for {typeof(TCommand).Name}");

        // Retrieve behaviors registered for this exact TCommand/TResult pair.
        // Reverse() ensures that the first-registered behavior becomes the outermost wrapper,
        // matching the conventional "first in, first out" pipeline ordering.
        var behaviors = provider.GetServices<IPipelineBehavior<TCommand, TResult>>().Reverse();

        // Seed the delegate chain with the actual handler invocation.
        Func<Task<TResult>> handlerDelegate = () => handler.HandleAsync(command, cancellationToken);

        // Wrap the current delegate in each behavior, building an onion-layered call chain.
        // The loop captures `next` in a local before reassigning `handlerDelegate` so that
        // each closure refers to the correct previous step rather than always calling itself.
        foreach (var behavior in behaviors)
        {
            var next = handlerDelegate;
            handlerDelegate = () => behavior.HandleAsync(command, next, cancellationToken);
        }

        return await handlerDelegate();
    }

    /// <inheritdoc />
    public async Task<TResult> SendQueryAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IQuery<TResult>
    {
        var handler = provider.GetService<IQueryHandler<TQuery, TResult>>()
            ?? throw new InvalidOperationException($"No handler registered for {typeof(TQuery).Name}");

        // Same behavior-wrapping logic as SendCommandAsync — see comments there.
        var behaviors = provider.GetServices<IPipelineBehavior<TQuery, TResult>>().Reverse();

        Func<Task<TResult>> handlerDelegate = () => handler.HandleAsync(query, cancellationToken);

        foreach (var behavior in behaviors)
        {
            var next = handlerDelegate;
            handlerDelegate = () => behavior.HandleAsync(query, next, cancellationToken);
        }

        return await handlerDelegate();
    }
}
