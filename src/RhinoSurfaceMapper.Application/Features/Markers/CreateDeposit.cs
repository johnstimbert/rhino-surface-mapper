using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.Domain.Services;

namespace RhinoSurfaceMapper.Application.Features.Markers;

/// <summary>
/// Records a new mining deposit at the current SRV position, ported from
/// <c>qt_map_operations.mark_deposit</c>. Deposits within
/// <see cref="MapperConstants.DepositMinimumSeparationMetres"/> of an existing deposit are
/// rejected to prevent duplicates, exactly as Python's haversine pre-check does.
/// </summary>
public static class CreateDeposit
{
    /// <summary>Creates a deposit at the live Rhino position.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>Free-text deposit label.</summary>
        public required string Name { get; init; }

        /// <summary>Deposit size tier.</summary>
        public required DepositSize Size { get; init; }

        /// <summary>Rig count for this deposit.</summary>
        public required int Rigs { get; init; }
    }

    /// <summary>Requires a rig count in the Python-validated 1–6 range.</summary>
    public sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>Creates the validator.</summary>
        public Validator() => RuleFor(command => command.Rigs).InclusiveBetween(1, 6);
    }

    /// <summary>Validates separation, then appends the deposit at the live Rhino position.</summary>
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

            if (session.RhinoLat is not double lat || session.RhinoLon is not double lon || session.CenterLat is null)
            {
                return new Response { Result = Result.NoRhinoPosition };
            }

            foreach (var deposit in session.Deposits)
            {
                double distance = PlanetGeometry.SurfaceDistance(session.Radius, lat, lon, deposit.Lat, deposit.Lon);
                if (distance < MapperConstants.DepositMinimumSeparationMetres)
                {
                    return new Response { Result = Result.TooClose };
                }
            }

            var (x, y) = session.LocalFromGeographic(lat, lon);
            session.AddDeposit(new Deposit(Guid.NewGuid(), command.Name, command.Size, command.Rigs, x, y, lat, lon));
            return new Response { Result = Result.Success };
        }
    }

    /// <summary>Outcome of creating a deposit.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="CreateDeposit"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The deposit was recorded.</summary>
        Success,

        /// <summary>The map is protected or mining-only.</summary>
        ReadOnly,

        /// <summary>No Rhino position is known yet.</summary>
        NoRhinoPosition,

        /// <summary>An existing deposit is within the minimum separation distance.</summary>
        TooClose,
    }
}
