using FluentValidation;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Application.Features.Pml;

/// <summary>
/// Returns the next available John Doe placeholder PML id for a body, ported from
/// <c>map_pml.next_john_doe_id</c>. Used by the "New map"/PML-identification dialogs to suggest a
/// placeholder id before the user supplies (or confirms) a real one, and by
/// <c>MapTransitionCoordinator</c>'s automatic new-PML path for the background transition flow.
/// </summary>
public static class AllocateJohnDoeId
{
    /// <summary>Requests the next John Doe id for a system/body.</summary>
    public sealed record Query : IQuery<Response>
    {
        /// <summary>Star system to scan existing maps in.</summary>
        public required string System { get; init; }

        /// <summary>Body name to match existing maps against.</summary>
        public required string Body { get; init; }
    }

    /// <summary>Requires non-empty system and body names.</summary>
    public sealed class Validator : AbstractValidator<Query>
    {
        /// <summary>Creates the validator.</summary>
        public Validator()
        {
            RuleFor(query => query.System).NotEmpty();
            RuleFor(query => query.Body).NotEmpty();
        }
    }

    /// <summary>Scans the system's maps directory and returns the next unused John Doe id.</summary>
    public sealed class Handler(IMapRepository mapRepository) : Mediator.IQueryHandler<Query, Response>
    {
        /// <inheritdoc />
        public Task<Response> HandleAsync(Query query, CancellationToken cancellationToken = default)
        {
            var paths = mapRepository.EnumerateMaps(query.System);
            string id = PmlRules.NextJohnDoeId(paths, query.System, query.Body, path => LoadCandidate(mapRepository, path));
            return Task.FromResult(new Response { PmlId = id });
        }

        private static PmlCandidate LoadCandidate(IMapRepository mapRepository, string path)
        {
            try
            {
                var session = mapRepository.LoadAsync(path).GetAwaiter().GetResult();
                return Services.PmlCandidateFactory.FromSession(session);
            }
            catch (Exception ex) when (ex is IOException or MapValidationException)
            {
                throw new MapValidationException("Unreadable candidate map.", ex);
            }
        }
    }

    /// <summary>The next available John Doe id.</summary>
    public sealed record Response
    {
        /// <summary>The suggested id, for example <c>"JD3"</c>.</summary>
        public required string PmlId { get; init; }
    }
}
