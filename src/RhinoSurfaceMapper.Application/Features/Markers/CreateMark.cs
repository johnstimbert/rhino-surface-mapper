using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Services;

namespace RhinoSurfaceMapper.Application.Features.Markers;

/// <summary>
/// Creates a named marker from a bearing and distance relative to the live Rhino position,
/// ported from <c>qt_map_operations.mark</c>/<c>mark_coordinates</c>.
/// </summary>
public static class CreateMark
{
    /// <summary>Creates a mark at a bearing/distance offset from the current Rhino position.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>Free-text marker label.</summary>
        public required string Name { get; init; }

        /// <summary>Bearing in degrees from the Rhino position, clockwise from true north.</summary>
        public required double AzimuthDegrees { get; init; }

        /// <summary>Distance in metres from the Rhino position.</summary>
        public required double DistanceMetres { get; init; }
    }

    /// <summary>Requires a non-negative distance; azimuth is normalised by <c>GreatCircleDestination</c>, not restricted here.</summary>
    public sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>Creates the validator.</summary>
        public Validator() => RuleFor(command => command.DistanceMetres).GreaterThanOrEqualTo(0);
    }

    /// <summary>Computes the destination point and appends the mark.</summary>
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

            if (session.RhinoLat is not double rhinoLat || session.RhinoLon is not double rhinoLon || session.CenterLat is null)
            {
                return new Response { Result = Result.NoRhinoPosition };
            }

            var (lat, lon) = PlanetGeometry.GreatCircleDestination(rhinoLat, rhinoLon, session.Radius, command.AzimuthDegrees, command.DistanceMetres);
            var (x, y) = session.LocalFromGeographic(lat, lon);
            session.AddMark(new Domain.Entities.MapMark(Guid.NewGuid(), command.Name, x, y, lat, lon));
            return new Response { Result = Result.Success };
        }
    }

    /// <summary>Outcome of creating a mark.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="CreateMark"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The mark was created.</summary>
        Success,

        /// <summary>The map is protected or mining-only.</summary>
        ReadOnly,

        /// <summary>No Rhino position is known yet.</summary>
        NoRhinoPosition,
    }
}
