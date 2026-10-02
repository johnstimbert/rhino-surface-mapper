using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Features.Markers;

/// <summary>Removes a deposit by id, ported from <c>qt_map_operations.delete_marker</c>'s deposit branch.</summary>
public static class DeleteDeposit
{
    /// <summary>Deletes the deposit with the given id.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>The deposit to delete.</summary>
        public required Guid DepositId { get; init; }
    }

    /// <summary>No fields require validation.</summary>
    public sealed class Validator : AbstractValidator<Command>;

    /// <summary>Removes the deposit if the map is not read-only and it still exists.</summary>
    public sealed class Handler(IMapSessionStore store, IMapSessionNotifier notifier) : ICommandHandler<Command, Response>
    {
        /// <inheritdoc />
        public async Task<Response> HandleAsync(Command command, CancellationToken cancellationToken = default)
        {
            Response response = await store.MutateAsync((session, _) => Task.FromResult(Compute(session, command)), cancellationToken).ConfigureAwait(false);

            if (response.Result == Result.Success)
            {
                notifier.NotifySessionChanged();
            }

            return response;
        }

        private static Response Compute(Domain.Entities.MapSession session, Command command)
        {
            if (session.ReadOnly)
            {
                return new Response { Result = Result.ReadOnly };
            }

            return session.DeleteDeposit(command.DepositId)
                ? new Response { Result = Result.Success }
                : new Response { Result = Result.NotFound };
        }
    }

    /// <summary>Outcome of deleting a deposit.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="DeleteDeposit"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The deposit was removed.</summary>
        Success,

        /// <summary>The map is protected or mining-only.</summary>
        ReadOnly,

        /// <summary>No deposit with the given id exists.</summary>
        NotFound,
    }
}
