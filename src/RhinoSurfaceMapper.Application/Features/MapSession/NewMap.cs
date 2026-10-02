using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Features.MapSession;

/// <summary>
/// Starts a fresh (non-protected, non-mining-only) exploration pass, ported from
/// <c>MapperState.new_map</c>. Used by the UI's "New" toolbar action.
/// </summary>
/// <remarks>
/// This feature's namespace segment (<c>Features.MapSession</c>) intentionally shares a name
/// with <see cref="Domain.Entities.MapSession"/> (the reference app's own
/// <c>Features.Event</c>/<c>Domain.Entities.Event</c> convention this design is modelled on does
/// the same). Every file in this namespace therefore refers to the domain type only through the
/// fully qualified <see cref="Domain.Entities.MapSession"/> name, never a bare <c>MapSession</c>,
/// to avoid any ambiguity between the namespace and the type.
/// </remarks>
public static class NewMap
{
    /// <summary>Starts a new map, optionally preserving the currently identified PML.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>
        /// When <see langword="true"/>, keeps <see cref="Domain.Entities.MapSession.PmlId"/> and
        /// its centre so a fresh recording continues to belong to the same PML.
        /// </summary>
        public bool KeepPml { get; init; }
    }

    /// <summary>No fields require validation; <see cref="Command.KeepPml"/> is a plain flag.</summary>
    public sealed class Validator : AbstractValidator<Command>;

    /// <summary>Clears exploration data on the live session and notifies session-changed subscribers.</summary>
    public sealed class Handler(
        IMapSessionStore store,
        IMapSessionNotifier notifier,
        IValidator<Command> validator) : ICommandHandler<Command, Response>
    {
        /// <inheritdoc />
        public async Task<Response> HandleAsync(Command command, CancellationToken cancellationToken = default)
        {
            var validationResult = await validator.ValidateAsync(command, cancellationToken).ConfigureAwait(false);
            if (!validationResult.IsValid)
            {
                throw new ValidationException(validationResult.Errors);
            }

            await store.MutateAsync(session =>
            {
                session.NewMap(command.KeepPml);
                if (!command.KeepPml)
                {
                    session.CurrentFilePath = null;
                }
            }, cancellationToken).ConfigureAwait(false);

            notifier.NotifySessionChanged();
            return new Response { Result = Result.Success };
        }
    }

    /// <summary>Outcome of starting a new map.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="NewMap"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The map was cleared.</summary>
        Success,
    }
}
