using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Enums;

namespace RhinoSurfaceMapper.Application.Features.Markers;

/// <summary>
/// Edits an existing deposit's name/size/rig count, ported from
/// <c>qt_map_operations.edit_deposit</c>. Position is never changed, matching Python's
/// <c>item.update(values)</c> which only ever carries the dialog's editable fields.
/// </summary>
public static class UpdateDeposit
{
    /// <summary>Edits an existing deposit.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>The deposit to edit.</summary>
        public required Guid DepositId { get; init; }

        /// <summary>New free-text label.</summary>
        public required string Name { get; init; }

        /// <summary>New size tier.</summary>
        public required DepositSize Size { get; init; }

        /// <summary>New rig count.</summary>
        public required int Rigs { get; init; }
    }

    /// <summary>Requires a rig count in the Python-validated 1–6 range.</summary>
    public sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>Creates the validator.</summary>
        public Validator() => RuleFor(command => command.Rigs).InclusiveBetween(1, 6);
    }

    /// <summary>Applies the edit if the map is not read-only and the deposit still exists.</summary>
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

            return session.UpdateDeposit(command.DepositId, command.Name, command.Size, command.Rigs)
                ? new Response { Result = Result.Success }
                : new Response { Result = Result.NotFound };
        }
    }

    /// <summary>Outcome of editing a deposit.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="UpdateDeposit"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The deposit was updated.</summary>
        Success,

        /// <summary>The map is protected or mining-only.</summary>
        ReadOnly,

        /// <summary>No deposit with the given id exists.</summary>
        NotFound,
    }
}
