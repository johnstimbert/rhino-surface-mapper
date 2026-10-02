using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace RhinoSurfaceMapper.Application.Mediator;

/// <summary>
/// A pipeline behavior that logs the start, outcome, and elapsed time for every
/// command or query dispatched through the custom mediator.
/// </summary>
/// <typeparam name="TInput">The command or query type.</typeparam>
/// <typeparam name="TOutput">The response type.</typeparam>
/// <remarks>
/// <para>
/// Ported from <c>TheUnofficialWythevilleApp.Web.Application.Mediator.LoggingPipelineBehavior</c>.
/// Registered as an open generic (<c>IPipelineBehavior&lt;,&gt;</c>) so it is active for
/// every handler without per-handler wiring, and registered <em>outermost</em> relative to
/// <see cref="ExceptionLoggingBehavior{TInput,TOutput}"/> so a successful-but-"Forbidden"
/// style result is still logged even though no exception was thrown.
/// </para>
/// <para>
/// Log levels follow this rule:
/// <list type="bullet">
///   <item><description><c>Debug</c> — handler entry (suppressed in production by default).</description></item>
///   <item><description><c>Information</c> — successful or expected non-success outcomes.</description></item>
///   <item><description><c>Warning</c> — any result whose name contains "Forbidden" or "Denied".</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Sensitive data:</strong> this class logs only the <c>typeof(TInput).Name</c>,
/// elapsed milliseconds, and the string representation of the <c>Result</c> property on
/// <typeparamref name="TOutput"/>. Command/query payload properties are <em>never</em> read or logged.
/// </para>
/// </remarks>
public sealed class LoggingPipelineBehavior<TInput, TOutput>(
    ILogger<LoggingPipelineBehavior<TInput, TOutput>> logger)
    : IPipelineBehavior<TInput, TOutput>
{
    // Each generic instantiation (i.e., each unique TInput/TOutput pair) has its own copy of
    // these static fields, so the typeof/GetProperty lookups happen once per pair for the
    // entire application lifetime rather than once per dispatch.
    private static readonly string InputName = typeof(TInput).Name;

    private static readonly PropertyInfo? ResultProperty =
        typeof(TOutput).GetProperty("Result", BindingFlags.Public | BindingFlags.Instance);

    /// <inheritdoc/>
    public async Task<TOutput> HandleAsync(
        TInput input,
        Func<Task<TOutput>> next,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Handling {HandlerType}", InputName);

        var stopwatch = Stopwatch.StartNew();
        var output = await next().ConfigureAwait(false);
        stopwatch.Stop();

        var resultName = ResultProperty?.GetValue(output)?.ToString();

        // Use IndexOf rather than equality so composite result names such as
        // "ForbiddenWhileProtected" or "AccessDenied" still trigger the Warning level.
        var isRefused = resultName?.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) == true
            || resultName?.Contains("Denied", StringComparison.OrdinalIgnoreCase) == true;

        if (isRefused)
        {
            logger.LogWarning(
                "Handled {HandlerType} in {ElapsedMs} ms | Result={Result} [REFUSED]",
                InputName, stopwatch.ElapsedMilliseconds, resultName);
        }
        else
        {
            logger.LogInformation(
                "Handled {HandlerType} in {ElapsedMs} ms | Result={Result}",
                InputName, stopwatch.ElapsedMilliseconds, resultName ?? "n/a");
        }

        return output;
    }
}
