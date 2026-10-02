using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using RhinoSurfaceMapper.Application.Features.Markers;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Features.Pml;
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
    /// Registers the custom mediator and its pipeline behaviors, every Phase 4 command/query
    /// handler, the map-lifecycle orchestration singleton, FluentValidation's auto-scanned
    /// validators, plus the Application-owned
    /// <see cref="Interfaces.IMapSessionStore"/>/<see cref="Interfaces.IMapSessionNotifier"/>
    /// singletons, into the DI container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Handler registration is explicit — there is no auto-scanning.</strong> Each
    /// <c>ICommandHandler</c>/<c>IQueryHandler</c> must be added individually here, mirroring the
    /// reference app's
    /// <c>TheUnofficialWythevilleApp.Web.Application.ConfigureServices.AddApplication</c>.
    /// Omitting a handler causes <see cref="InvalidOperationException"/> at dispatch time.
    /// <see cref="AbstractValidator{T}"/> implementations are auto-scanned via
    /// <c>AddValidatorsFromAssembly</c> instead.
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

        // The Phase 4 pending-transition/PML-activation state machine: one instance for the
        // whole process, mutated only from TelemetryHostedService's single poll loop and the
        // MapSession/ResolveUnsavedChanges feature handlers it is injected into alongside.
        services.AddSingleton<IMapTransitionCoordinator, MapTransitionCoordinator>();

        services.AddValidatorsFromAssembly(typeof(ConfigureServices).Assembly);

        services.AddScoped<ICommandHandler<NewMap.Command, NewMap.Response>, NewMap.Handler>();
        services.AddScoped<ICommandHandler<LoadMap.Command, LoadMap.Response>, LoadMap.Handler>();
        services.AddScoped<SaveMap.Handler>();
        services.AddScoped<ICommandHandler<SaveMap.Command, SaveMap.Response>, SaveMap.Handler>();
        services.AddScoped<ICommandHandler<SaveMapVersion.Command, SaveMap.Response>, SaveMapVersion.Handler>();
        services.AddScoped<IQueryHandler<GetMapSummary.Query, GetMapSummary.Response>, GetMapSummary.Handler>();
        services.AddScoped<ICommandHandler<EnterMiningMode.Command, EnterMiningMode.Response>, EnterMiningMode.Handler>();
        services.AddScoped<ICommandHandler<ResolveUnsavedChanges.Command, ResolveUnsavedChanges.Response>, ResolveUnsavedChanges.Handler>();
        services.AddScoped<ICommandHandler<EvaluateTelemetryPoll.Command, EvaluateTelemetryPoll.Response>, EvaluateTelemetryPoll.Handler>();

        services.AddScoped<ICommandHandler<CreateDeposit.Command, CreateDeposit.Response>, CreateDeposit.Handler>();
        services.AddScoped<ICommandHandler<UpdateDeposit.Command, UpdateDeposit.Response>, UpdateDeposit.Handler>();
        services.AddScoped<ICommandHandler<DeleteDeposit.Command, DeleteDeposit.Response>, DeleteDeposit.Handler>();
        services.AddScoped<ICommandHandler<PlaceRig.Command, PlaceRig.Response>, PlaceRig.Handler>();
        services.AddScoped<ICommandHandler<DeleteRig.Command, DeleteRig.Response>, DeleteRig.Handler>();
        services.AddScoped<ICommandHandler<CreateMark.Command, CreateMark.Response>, CreateMark.Handler>();
        services.AddScoped<ICommandHandler<UpdateMark.Command, UpdateMark.Response>, UpdateMark.Handler>();
        services.AddScoped<ICommandHandler<DeleteMark.Command, DeleteMark.Response>, DeleteMark.Handler>();

        services.AddScoped<ICommandHandler<IdentifyPml.Command, IdentifyPml.Response>, IdentifyPml.Handler>();
        services.AddScoped<ICommandHandler<CreatePml.Command, SaveMap.Response>, CreatePml.Handler>();
        services.AddScoped<IQueryHandler<ListPmlVersions.Query, ListPmlVersions.Response>, ListPmlVersions.Handler>();
        services.AddScoped<IQueryHandler<AllocateJohnDoeId.Query, AllocateJohnDoeId.Response>, AllocateJohnDoeId.Handler>();

        return services;
    }
}

