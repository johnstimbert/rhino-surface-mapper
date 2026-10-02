using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.Services;

namespace RhinoSurfaceMapper.Application.Features.Pml;

/// <summary>
/// Assigns a real PML id (and, optionally, re-centres the PML via a bearing/distance offset from
/// the live Rhino position) to the active map, ported from <c>qt_map_operations.setup_new_pml</c>.
/// Lets the user rename/re-centre a John Doe placeholder PML once they know its real identifier
/// — the interactive counterpart to <c>MapTransitionCoordinator</c>'s automatic John Doe
/// allocation for the background transition path (see that type's remarks).
/// </summary>
public static class IdentifyPml
{
    /// <summary>Assigns a PML identity to the active map.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>The real PML identifier to assign.</summary>
        public required string PmlId { get; init; }

        /// <summary>
        /// Bearing in degrees from the Rhino position to the PML centre, or <see langword="null"/>
        /// to keep the centre at the current Rhino position (distance <c>0</c>).
        /// </summary>
        public double? AzimuthDegrees { get; init; }

        /// <summary>Distance in metres from the Rhino position to the PML centre, used only together with <see cref="AzimuthDegrees"/>.</summary>
        public double? DistanceMetres { get; init; }
    }

    /// <summary>Requires a non-empty PML id and a non-negative distance when supplied.</summary>
    public sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>Creates the validator.</summary>
        public Validator()
        {
            RuleFor(command => command.PmlId).NotEmpty();
            RuleFor(command => command.DistanceMetres!.Value).GreaterThanOrEqualTo(0).When(command => command.DistanceMetres is not null);
        }
    }

    /// <summary>Computes the PML centre and assigns the identity to the live session.</summary>
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

            if (session.RhinoLat is not double rhinoLat || session.RhinoLon is not double rhinoLon)
            {
                return new Response { Result = Result.NoRhinoPosition };
            }

            double azimuth = command.AzimuthDegrees ?? 0.0;
            double distance = command.DistanceMetres ?? 0.0;
            var (centerLat, centerLon) = PlanetGeometry.GreatCircleDestination(rhinoLat, rhinoLon, session.Radius, azimuth, distance);

            session.PmlId = command.PmlId;
            session.PmlCenterLat = centerLat;
            session.PmlCenterLon = centerLon;
            return new Response { Result = Result.Success };
        }
    }

    /// <summary>Outcome of assigning a PML identity.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="IdentifyPml"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The PML identity was assigned.</summary>
        Success,

        /// <summary>The map is protected or mining-only.</summary>
        ReadOnly,

        /// <summary>No Rhino position is known yet.</summary>
        NoRhinoPosition,
    }
}
