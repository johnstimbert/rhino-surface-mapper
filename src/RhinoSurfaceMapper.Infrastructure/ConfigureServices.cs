using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Infrastructure.Logging;
using RhinoSurfaceMapper.Infrastructure.Migration;
using RhinoSurfaceMapper.Infrastructure.Paths;
using RhinoSurfaceMapper.Infrastructure.Persistence;
using RhinoSurfaceMapper.Infrastructure.Telemetry;
using RhinoSurfaceMapper.Infrastructure.Time;

namespace RhinoSurfaceMapper.Infrastructure;

/// <summary>
/// Dependency injection configuration for the Infrastructure layer.
/// </summary>
public static class ConfigureServices
{
    /// <summary>
    /// Registers <see cref="IClock"/>, <see cref="IAppPaths"/>, the rolling-file logging
    /// provider (via <see cref="AddInfrastructureLogging"/>), the JSON persistence
    /// repositories, the telemetry readers, and the decision-D7 legacy-map migration hosted
    /// service into the DI container.
    /// </summary>
    /// <param name="services">The service collection to register services into.</param>
    /// <param name="configuration">Application configuration, used to bind the <c>Logging</c> section.</param>
    /// <returns>The same <paramref name="services"/> instance, for method chaining.</returns>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAppPaths, AppPaths>();

        services.AddInfrastructureLogging(configuration);

        // Registered as both its concrete type and IMapRepository, resolving to the same
        // instance per scope: LegacyMapMigrationService needs the concrete type to reach
        // LoadWithLegacyLiteralInfoAsync (not part of the frozen IMapRepository contract),
        // while every other consumer depends on the interface only. Singleton because the
        // repository itself is stateless (its dependencies, IAppPaths/IClock, are singletons
        // too) and a hosted service resolves it once from the root provider.
        services.AddSingleton<JsonMapRepository>();
        services.AddSingleton<IMapRepository>(sp => sp.GetRequiredService<JsonMapRepository>());

        services.AddSingleton<IPreferencesRepository, JsonPreferencesRepository>();

        // Both readers are effectively stateless/single-poller-owned (JournalIdentityReader
        // carries its own read-position state across calls), so a singleton lifetime matches
        // their intended usage from a single telemetry polling loop in a later phase.
        services.AddSingleton<IStatusTelemetryReader, StatusFileReader>();
        services.AddSingleton<IJournalIdentityReader, JournalIdentityReader>();

        // Decision D7: must run, and complete, before any later-phase hosted service that reads
        // map files for telemetry/radar/steering — registered first so that ordering holds once
        // those services exist; IHostedService instances start in registration order.
        services.AddHostedService<LegacyMapMigrationService>();

        return services;
    }

    /// <summary>
    /// Registers the logging pipeline described in the design's "Logging design" section:
    /// the custom <see cref="RollingFileLoggerProvider"/> plus the built-in debug provider
    /// for Visual Studio output during development, both bound to the <c>Logging</c>
    /// configuration section so levels are overridable without a rebuild.
    /// </summary>
    /// <remarks>
    /// <c>EventLogLoggerProvider</c> (startup/shutdown failures only) is intentionally not
    /// wired here: the design marks it optional and off by default, and it is
    /// Windows-Event-Log-specific — out of scope for the Phase 0 skeleton.
    /// </remarks>
    /// <param name="services">The service collection to register services into.</param>
    /// <param name="configuration">Application configuration, used to bind the <c>Logging</c> section.</param>
    /// <returns>The same <paramref name="services"/> instance, for method chaining.</returns>
    public static IServiceCollection AddInfrastructureLogging(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RollingFileLoggerOptions>()
            .Bind(configuration.GetSection("Logging:File"));

        // Registering the provider as a singleton ILoggerProvider is enough: AddLogging()
        // resolves every ILoggerProvider already present in the container.
        services.AddSingleton<RollingFileLoggerProvider>();
        services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<RollingFileLoggerProvider>());

        // Registered as a hosted service (not a provider constructor dependency) to avoid the
        // ILoggerFactory/IHostApplicationLifetime dependency cycle documented on
        // LoggingShutdownFlusherHostedService.
        services.AddHostedService<LoggingShutdownFlusherHostedService>();

        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddDebug();
        });

        return services;
    }
}
