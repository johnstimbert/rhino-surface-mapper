using FluentValidation;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Features.MapSession;

/// <summary>
/// Convenience wrapper over <see cref="SaveMap"/> fixed to <see cref="SaveMode.NewVersion"/>,
/// ported from the "Save new version" dialog button (also reused internally by
/// <c>confirm_pml_exit</c>/<c>prepare_to_replace_current_map</c>'s own new-version choice). Kept
/// as its own feature (rather than only a <see cref="SaveMode"/> value) because the design's
/// feature inventory lists it as a distinct use-case the UI dispatches directly from a
/// "Save new version" toolbar/menu action, independent of the interactive save-mode dialog.
/// </summary>
public static class SaveMapVersion
{
    /// <summary>Saves a new numbered version of the active map.</summary>
    public sealed record Command : ICommand<SaveMap.Response>;

    /// <summary>No fields require validation.</summary>
    public sealed class Validator : AbstractValidator<Command>;

    /// <summary>Delegates to <see cref="SaveMap.Handler"/> with <see cref="SaveMode.NewVersion"/>.</summary>
    public sealed class Handler(SaveMap.Handler inner) : ICommandHandler<Command, SaveMap.Response>
    {
        /// <inheritdoc />
        public Task<SaveMap.Response> HandleAsync(Command command, CancellationToken cancellationToken = default) =>
            inner.HandleAsync(new SaveMap.Command { Mode = SaveMode.NewVersion }, cancellationToken);
    }
}
