using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Services;

namespace RhinoSurfaceMapper.Application.Features.Markers;

/// <summary>
/// Edits an existing mark's name and/or bearing/distance offset from the live Rhino position,
/// ported from <c>qt_map_operations.alter_mark</c>. Position is recomputed only when
/// <see cref="Command.AzimuthDegrees"/>/<see cref="Command.DistanceMetres"/> actually differ from
/// <see cref="Command.ExistingAzimuthDegrees"/>/<see cref="Command.ExistingDistanceMetres"/> —
/// accepting only a renamed marker must not move it through rounded bearing/distance fields,
/// exactly as Python's own equality check guards.
/// </summary>
public static class UpdateMark
{
    /// <summary>Edits an existing mark.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>The mark to edit.</summary>
        public required Guid MarkId { get; init; }

        /// <summary>New free-text label.</summary>
        public required string Name { get; init; }

        /// <summary>The bearing the edit dialog was pre-filled with (the mark's current bearing from the Rhino, rounded).</summary>
        public required double ExistingAzimuthDegrees { get; init; }

        /// <summary>The distance the edit dialog was pre-filled with (the mark's current distance from the Rhino, rounded).</summary>
        public required double ExistingDistanceMetres { get; init; }

        /// <summary>The bearing the user confirmed (possibly unchanged from <see cref="ExistingAzimuthDegrees"/>).</summary>
        public required double AzimuthDegrees { get; init; }

        /// <summary>The distance the user confirmed (possibly unchanged from <see cref="ExistingDistanceMetres"/>).</summary>
        public required double DistanceMetres { get; init; }
    }

    /// <summary>Requires a non-negative confirmed distance.</summary>
    public sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>Creates the validator.</summary>
        public Validator() => RuleFor(command => command.DistanceMetres).GreaterThanOrEqualTo(0);
    }

    /// <summary>Recomputes position only when the bearing/distance actually changed, then applies the edit.</summary>
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

            int index = -1;
            for (int i = 0; i < session.Marks.Count; i++)
            {
                if (session.Marks[i].Id == command.MarkId)
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                return new Response { Result = Result.NotFound };
            }

            var existing = session.Marks[index];
            double x = existing.X;
            double y = existing.Y;
            double lat = existing.Lat;
            double lon = existing.Lon;

            bool positionChanged = command.AzimuthDegrees != command.ExistingAzimuthDegrees
                || command.DistanceMetres != command.ExistingDistanceMetres;

            if (positionChanged)
            {
                if (session.RhinoLat is not double rhinoLat || session.RhinoLon is not double rhinoLon || session.CenterLat is null)
                {
                    return new Response { Result = Result.NoRhinoPosition };
                }

                (lat, lon) = PlanetGeometry.GreatCircleDestination(rhinoLat, rhinoLon, session.Radius, command.AzimuthDegrees, command.DistanceMetres);
                (x, y) = session.LocalFromGeographic(lat, lon);
            }

            return session.UpdateMark(command.MarkId, command.Name, x, y, lat, lon)
                ? new Response { Result = Result.Success }
                : new Response { Result = Result.NotFound };
        }
    }

    /// <summary>Outcome of editing a mark.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="UpdateMark"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The mark was updated.</summary>
        Success,

        /// <summary>The map is protected or mining-only.</summary>
        ReadOnly,

        /// <summary>No mark with the given id exists.</summary>
        NotFound,

        /// <summary>The bearing/distance changed but no Rhino position is known to recompute from.</summary>
        NoRhinoPosition,
    }
}
