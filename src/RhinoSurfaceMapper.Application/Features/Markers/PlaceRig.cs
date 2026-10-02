using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Entities;

namespace RhinoSurfaceMapper.Application.Features.Markers;

/// <summary>
/// Places a rig at an explicit local-metre position, ported from
/// <c>qt_map_operations.place_rig</c>. The caller (the canvas click handler / toolbar placement
/// mode) is responsible for converting a screen click to local metres, exactly as Python's own
/// <c>self.view.world(point)</c> does before calling <c>place_rig</c>.
/// </summary>
public static class PlaceRig
{
    /// <summary>Places a rig at the given local-metre position.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>Local easting in metres relative to the map centre.</summary>
        public required double X { get; init; }

        /// <summary>Local northing in metres relative to the map centre.</summary>
        public required double Y { get; init; }
    }

    /// <summary>No fields require validation; any finite local coordinate is acceptable.</summary>
    public sealed class Validator : AbstractValidator<Command>;

    /// <summary>Converts the local position back to geographic coordinates and appends the rig.</summary>
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
            if (session.ReadOnly || session.CenterLat is null)
            {
                return new Response { Result = Result.ReadOnly };
            }

            var (lat, lon) = session.GeographicFromLocal(command.X, command.Y);
            session.AddRig(new Rig(Guid.NewGuid(), command.X, command.Y, lat, lon));
            return new Response { Result = Result.Success };
        }
    }

    /// <summary>Outcome of placing a rig.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="PlaceRig"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The rig was placed.</summary>
        Success,

        /// <summary>The map is protected, mining-only, or has no established centre.</summary>
        ReadOnly,
    }
}
