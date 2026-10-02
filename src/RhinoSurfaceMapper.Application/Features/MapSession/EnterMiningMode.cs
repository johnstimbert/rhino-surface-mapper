using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Features.MapSession;

/// <summary>
/// Suspends exploration/search while keeping existing records viewable, ported from
/// <c>MapperState.enter_mining_mode</c>. Used by the UI's "Mining mode" toggle so a commander can
/// review a map's deposits/rigs without risking an accidental mutation.
/// </summary>
public static class EnterMiningMode
{
    /// <summary>Switches the active map into mining-only (read-only) mode. Carries no parameters.</summary>
    public sealed record Command : ICommand<Response>;

    /// <summary>No fields require validation.</summary>
    public sealed class Validator : AbstractValidator<Command>;

    /// <summary>Applies <see cref="Domain.Entities.MapSession.EnterMiningMode"/> to the live session.</summary>
    public sealed class Handler(IMapSessionStore store, IMapSessionNotifier notifier) : ICommandHandler<Command, Response>
    {
        /// <inheritdoc />
        public async Task<Response> HandleAsync(Command command, CancellationToken cancellationToken = default)
        {
            await store.MutateAsync(session => session.EnterMiningMode(), cancellationToken).ConfigureAwait(false);
            notifier.NotifySessionChanged();
            return new Response { Result = Result.Success };
        }
    }

    /// <summary>Outcome of entering mining mode.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="EnterMiningMode"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>Mining-only mode was entered.</summary>
        Success,
    }
}
