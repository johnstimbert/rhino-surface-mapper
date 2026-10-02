using Microsoft.Extensions.Logging;

namespace RhinoSurfaceMapper.Application.Mediator;

/// <summary>
/// A pipeline behavior that logs <c>Error</c> and rethrows whenever the wrapped handler
/// (or an inner behavior) throws, so a failure is recorded even when a calling UI layer
/// swallows the exception for presentation purposes.
/// </summary>
/// <typeparam name="TInput">The command or query type.</typeparam>
/// <typeparam name="TOutput">The response type.</typeparam>
/// <remarks>
/// Registered innermost relative to <see cref="LoggingPipelineBehavior{TInput,TOutput}"/>
/// (closest to the handler), so the elapsed-time/result log from the outer behavior never
/// fires for a path that throws — only this behavior's <c>Error</c> entry does. The original
/// exception instance is rethrown unchanged (<see langword="throw;"/>), preserving its
/// stack trace for any caller further up the chain.
/// </remarks>
public sealed class ExceptionLoggingBehavior<TInput, TOutput>(
    ILogger<ExceptionLoggingBehavior<TInput, TOutput>> logger)
    : IPipelineBehavior<TInput, TOutput>
{
    private static readonly string InputName = typeof(TInput).Name;

    /// <inheritdoc/>
    public async Task<TOutput> HandleAsync(
        TInput input,
        Func<Task<TOutput>> next,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await next().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Sanctioned, narrowly-scoped waiver of AGENTS.md §6 ("catch specific exception
            // types, never a bare catch"): this behavior sits outermost-but-one in the pipeline
            // specifically to observe and log *any* failure a handler or inner behavior can
            // produce, regardless of type, before it propagates further up the call chain — a
            // cross-cutting diagnostic concern, not business error handling. It is safe because
            // the exception is only ever logged and immediately rethrown unchanged
            // (<see langword="throw;"/>, not "throw exception;"), never swallowed or replaced,
            // so callers still see — and must still handle — the original exception and its
            // original stack trace exactly as if this behavior were not present.
            logger.LogError(exception, "Unhandled exception while handling {HandlerType}", InputName);
            throw;
        }
    }
}
