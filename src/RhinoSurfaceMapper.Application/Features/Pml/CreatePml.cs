using FluentValidation;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Features.Pml;

/// <summary>
/// Writes the first persisted file for a newly identified PML, ported from
/// <c>qt_map_operations.open_or_create_pml_for_current_position</c>'s "no nearby match, create a
/// new file at the canonical PML path" branch. A thin wrapper over <see cref="SaveMap"/> fixed to
/// <see cref="SaveMode.Replace"/> — a fresh PML's canonical path never collides with an existing
/// file, so "replace" and "create" are the same write.
/// </summary>
public static class CreatePml
{
    /// <summary>Creates the canonical file for the active map's identified PML. Carries no parameters.</summary>
    public sealed record Command : ICommand<SaveMap.Response>;

    /// <summary>No fields require validation.</summary>
    public sealed class Validator : AbstractValidator<Command>;

    /// <summary>Delegates to <see cref="SaveMap.Handler"/> with <see cref="SaveMode.Replace"/>.</summary>
    public sealed class Handler(SaveMap.Handler inner) : ICommandHandler<Command, SaveMap.Response>
    {
        /// <inheritdoc />
        public Task<SaveMap.Response> HandleAsync(Command command, CancellationToken cancellationToken = default) =>
            inner.HandleAsync(new SaveMap.Command { Mode = SaveMode.Replace }, cancellationToken);
    }
}
