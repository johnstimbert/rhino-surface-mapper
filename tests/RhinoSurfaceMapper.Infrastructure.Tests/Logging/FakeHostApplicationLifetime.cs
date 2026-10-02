using Microsoft.Extensions.Hosting;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Logging;

/// <summary>
/// Minimal <see cref="IHostApplicationLifetime"/> test double exposing a directly
/// triggerable <see cref="ApplicationStopping"/> token, so
/// <see cref="RhinoSurfaceMapper.Infrastructure.Logging.LoggingShutdownFlusherHostedService"/>
/// can be tested without building and starting a real generic host. Kept in the test project
/// only, per the rule that test-friendly fakes do not belong in production code.
/// </summary>
internal sealed class FakeHostApplicationLifetime : IHostApplicationLifetime, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    /// <inheritdoc />
    public CancellationToken ApplicationStarted { get; } = CancellationToken.None;

    /// <inheritdoc />
    public CancellationToken ApplicationStopping => _stopping.Token;

    /// <inheritdoc />
    public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

    /// <summary>Raises <see cref="ApplicationStopping"/>, simulating the host beginning shutdown.</summary>
    public void TriggerApplicationStopping() => _stopping.Cancel();

    /// <inheritdoc />
    public void StopApplication() => TriggerApplicationStopping();

    /// <inheritdoc />
    public void Dispose() => _stopping.Dispose();
}
