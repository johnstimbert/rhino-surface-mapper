using FluentValidation;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Features.MapSession;

/// <summary>
/// Records the user's chosen disposition for the previously active map's unsaved changes during
/// a pending location transition, ported from the three actionable buttons of Python's
/// <c>prepare_to_replace_current_map</c> dialog. Dispatched by the
/// <c>ProtectedMapDialog</c>/transition-prompt Blazor dialog once the user answers; see
/// <c>Interfaces.IMapTransitionCoordinator</c>'s remarks for why this gates rather than blocks
/// the telemetry poll loop.
/// </summary>
public static class ResolveUnsavedChanges
{
    /// <summary>The user's chosen disposition for the old map.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>How to dispose of the previous map's unsaved changes.</summary>
        public required Interfaces.OldMapDisposition Disposition { get; init; }
    }

    /// <summary>No fields require validation beyond the enum already being well-formed.</summary>
    public sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>Creates the validator.</summary>
        public Validator() => RuleFor(command => command.Disposition).IsInEnum();
    }

    /// <summary>Forwards the user's choice to the lifecycle coordinator.</summary>
    public sealed class Handler(Interfaces.IMapTransitionCoordinator coordinator) : ICommandHandler<Command, Response>
    {
        /// <inheritdoc />
        public Task<Response> HandleAsync(Command command, CancellationToken cancellationToken = default)
        {
            coordinator.ResolveOldMapDisposition(command.Disposition);
            return Task.FromResult(new Response { Result = Result.Recorded });
        }
    }

    /// <summary>Outcome of recording the disposition.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="ResolveUnsavedChanges"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>
        /// The disposition was recorded. This does not itself guarantee the old map's save
        /// succeeded — the next <c>EvaluateTelemetryPoll</c> tick performs the actual I/O and
        /// reports failure by leaving the transition pending, exactly as Python's own
        /// <c>QMessageBox.critical</c> failure path leaves <c>pending_old_map_resolved</c> unset.
        /// </summary>
        Recorded,
    }
}
