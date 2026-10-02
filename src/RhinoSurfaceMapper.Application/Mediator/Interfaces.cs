namespace RhinoSurfaceMapper.Application.Mediator;

/// <summary>
/// Marker interface for a command that returns a result of type <typeparamref name="TResult"/>.
/// Commands represent write operations or actions that change state (map session mutations,
/// preference writes, PML allocation, and similar use-cases introduced from Phase 1 onward).
/// Implement this alongside <see cref="ICommandHandler{TCommand,TResult}"/> for each use-case action.
/// </summary>
/// <typeparam name="TResult">The type of the result produced by handling this command.</typeparam>
public interface ICommand<TResult>;

/// <summary>
/// Handles a specific command type and returns a result.
/// Implement this interface to provide the business logic for <typeparamref name="TCommand"/>.
/// Handlers are registered explicitly in <c>AddApplication()</c>; there is no auto-scanning.
/// </summary>
/// <typeparam name="TCommand">The command type this handler processes.</typeparam>
/// <typeparam name="TResult">The type of result the handler returns.</typeparam>
public interface ICommandHandler<in TCommand, TResult> where TCommand : ICommand<TResult>
{
    /// <summary>
    /// Executes the command and returns a result.
    /// </summary>
    /// <param name="command">The command to handle.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>A task that resolves to the command result.</returns>
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken = default);
}

/// <summary>
/// Marker interface for a query that returns a result of type <typeparamref name="TResult"/>.
/// Queries represent read operations that do not change state.
/// Implement this alongside <see cref="IQueryHandler{TQuery,TResult}"/> for each read use-case.
/// </summary>
/// <typeparam name="TResult">The type of the result produced by handling this query.</typeparam>
public interface IQuery<TResult>;

/// <summary>
/// Handles a specific query type and returns a result.
/// Implement this interface to provide the read logic for <typeparamref name="TQuery"/>.
/// Handlers are registered explicitly in <c>AddApplication()</c>; there is no auto-scanning.
/// </summary>
/// <typeparam name="TQuery">The query type this handler processes.</typeparam>
/// <typeparam name="TResult">The type of result the handler returns.</typeparam>
public interface IQueryHandler<in TQuery, TResult> where TQuery : IQuery<TResult>
{
    /// <summary>
    /// Executes the query and returns a result.
    /// </summary>
    /// <param name="query">The query to handle.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>A task that resolves to the query result.</returns>
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken = default);
}

/// <summary>
/// Cross-cutting concern that wraps command or query execution within the mediator pipeline.
/// Behaviors are applied in reverse registration order, making the first-registered behavior
/// the outermost wrapper.
/// </summary>
/// <typeparam name="TInput">The command or query type being dispatched.</typeparam>
/// <typeparam name="TOutput">The response type produced by the handler.</typeparam>
public interface IPipelineBehavior<in TInput, TOutput>
{
    /// <summary>
    /// Wraps the next step in the pipeline, performing cross-cutting logic before and/or after.
    /// </summary>
    /// <param name="input">The command or query being dispatched.</param>
    /// <param name="next">A delegate that invokes the next behavior, or the handler itself if no more behaviors remain.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>A task that resolves to the handler output.</returns>
    Task<TOutput> HandleAsync(TInput input, Func<Task<TOutput>> next, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves and dispatches commands and queries to their registered handlers,
/// running each dispatch through all registered <see cref="IPipelineBehavior{TInput,TOutput}"/>
/// wrappers (<c>LoggingPipelineBehavior</c>, <c>ExceptionLoggingBehavior</c>).
/// </summary>
/// <remarks>
/// Both type parameters must be supplied explicitly at the call site — they are not inferred:
/// <code>
/// await mediator.SendCommandAsync&lt;SaveMap.Command, SaveMap.Response&gt;(command);
/// </code>
/// Ported verbatim from <c>TheUnofficialWythevilleApp.Web.Application.Mediator</c>.
/// </remarks>
public interface IMediator
{
    /// <summary>
    /// Dispatches a command to its registered handler, passing it through the pipeline.
    /// </summary>
    /// <typeparam name="TCommand">The command type to dispatch.</typeparam>
    /// <typeparam name="TResult">The result type produced by the command handler.</typeparam>
    /// <param name="command">The command instance to dispatch.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>A task that resolves to the command result.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown at dispatch time when no <see cref="ICommandHandler{TCommand,TResult}"/>
    /// is registered for <typeparamref name="TCommand"/>.
    /// </exception>
    Task<TResult> SendCommandAsync<TCommand, TResult>(TCommand command, CancellationToken cancellationToken = default)
        where TCommand : ICommand<TResult>;

    /// <summary>
    /// Dispatches a query to its registered handler, passing it through the pipeline.
    /// </summary>
    /// <typeparam name="TQuery">The query type to dispatch.</typeparam>
    /// <typeparam name="TResult">The result type produced by the query handler.</typeparam>
    /// <param name="query">The query instance to dispatch.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>A task that resolves to the query result.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown at dispatch time when no <see cref="IQueryHandler{TQuery,TResult}"/>
    /// is registered for <typeparamref name="TQuery"/>.
    /// </exception>
    Task<TResult> SendQueryAsync<TQuery, TResult>(TQuery query, CancellationToken cancellationToken = default)
        where TQuery : IQuery<TResult>;
}
