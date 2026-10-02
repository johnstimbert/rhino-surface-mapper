using Microsoft.Extensions.DependencyInjection;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Application.Services;

namespace RhinoSurfaceMapper.Application;

/// <summary>
/// Dependency injection configuration for the Application layer.
/// </summary>
public static class ConfigureServices
{
    /// <summary>
    /// Registers the custom mediator and its pipeline behaviors, plus the Application-owned
    /// <see cref="Interfaces.IMapSessionStore"/>/<see cref="Interfaces.IMapSessionNotifier"/>
    /// singletons, into the DI container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Handler registration is explicit — there is no auto-scanning.</strong> Phase 0
    /// introduces no command/query features, so there is nothing to register yet. When the
    /// first feature lands (Phase 1+), each <c>ICommandHandler</c>/<c>IQueryHandler</c> must
    /// be added individually here, mirroring the reference app's
    /// <c>TheUnofficialWythevilleApp.Web.Application.ConfigureServices.AddApplication</c>.
    /// Omitting a handler causes <see cref="InvalidOperationException"/> at dispatch time.
    /// </para>
    /// <para>
    /// Pipeline behaviors are registered as open generics, outermost first:
    /// <see cref="LoggingPipelineBehavior{TInput,TOutput}"/> wraps
    /// <see cref="ExceptionLoggingBehavior{TInput,TOutput}"/>, so a dispatch that throws is
    /// logged once (by the exception behavior) rather than twice.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register services into.</param>
    /// <returns>The same <paramref name="services"/> instance, for method chaining.</returns>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<IMediator, Mediator.Mediator>();

        // Open-generic registrations cover every TInput/TOutput combination automatically.
        // Order matters: GetServices<T>() returns registrations in registration order, and
        // Mediator.Reverse()'s them so the first-registered behavior becomes the outermost
        // wrapper — LoggingPipelineBehavior therefore wraps ExceptionLoggingBehavior.
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(LoggingPipelineBehavior<,>));
        services.AddScoped(typeof(IPipelineBehavior<,>), typeof(ExceptionLoggingBehavior<,>));

        // Singletons per the design's "Application services and interfaces" table: exactly one
        // MapSession is live for the whole process (risk R10), shared by every hosted service
        // and by the UI's map canvas presenter.
        services.AddSingleton<IMapSessionStore, MapSessionStore>();
        services.AddSingleton<IMapSessionNotifier, MapSessionNotifier>();

        return services;
    }
}
