using Microsoft.Extensions.Hosting;

namespace RhinoSurfaceMapper.Infrastructure.Logging;

/// <summary>
/// Flushes <see cref="RollingFileLoggerProvider"/> to disk when the host begins shutting
/// down, so a clean exit never loses the last few buffered log lines.
/// </summary>
/// <remarks>
/// This behaviour is deliberately implemented as a separate hosted service rather than as a
/// constructor dependency on <see cref="RollingFileLoggerProvider"/> itself: hosted services
/// are resolved and started only after the DI container (and therefore the logging pipeline)
/// is fully built, so depending on <see cref="IHostApplicationLifetime"/> here is safe. The
/// default <c>IHostApplicationLifetime</c> implementation depends on
/// <c>ILogger&lt;ApplicationLifetime&gt;</c>, which depends on every registered
/// <c>ILoggerProvider</c> — if <see cref="RollingFileLoggerProvider"/> itself depended on
/// <see cref="IHostApplicationLifetime"/>, that would form a dependency cycle and hang
/// <c>HostApplicationBuilder.Build()</c> (observed and diagnosed during Phase 0 development).
/// </remarks>
internal sealed class LoggingShutdownFlusherHostedService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly RollingFileLoggerProvider _provider;
    private IDisposable? _registration;

    /// <summary>Creates the hosted service with the lifetime and provider it coordinates.</summary>
    public LoggingShutdownFlusherHostedService(IHostApplicationLifetime lifetime, RollingFileLoggerProvider provider)
    {
        _lifetime = lifetime;
        _provider = provider;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = _lifetime.ApplicationStopping.Register(_provider.FlushAndReportDrops);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registration?.Dispose();
        return Task.CompletedTask;
    }
}
