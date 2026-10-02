using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Application.Features.Pml;

/// <summary>
/// Lists the saved file versions of the active map's identified PML, newest first, ported from
/// the version-scanning branch of <c>qt_map_operations.load_map</c>.
/// </summary>
public static class ListPmlVersions
{
    /// <summary>Requests the saved versions of the active PML. Carries no parameters.</summary>
    public sealed record Query : IQuery<Response>;

    /// <summary>No fields require validation.</summary>
    public sealed class Validator : AbstractValidator<Query>;

    /// <summary>Scans the current system's maps directory for files matching the active body/PML id.</summary>
    public sealed class Handler(IMapRepository mapRepository, IMapSessionStore store) : IQueryHandler<Query, Response>
    {
        /// <inheritdoc />
        public async Task<Response> HandleAsync(Query query, CancellationToken cancellationToken = default)
        {
            string system = string.Empty;
            string body = string.Empty;
            string pmlId = string.Empty;
            await store.MutateAsync(session =>
            {
                system = session.System;
                body = session.Body;
                pmlId = session.PmlId;
            }, cancellationToken).ConfigureAwait(false);

            if (system.Trim().Length == 0 || body.Trim().Length == 0 || pmlId.Trim().Length == 0)
            {
                return new Response { Versions = [] };
            }

            var versions = new List<Version>();
            foreach (string path in mapRepository.EnumerateMaps(system))
            {
                Domain.Entities.MapSession candidate;
                try
                {
                    candidate = await mapRepository.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or MapValidationException)
                {
                    continue;
                }

                if (string.Equals(candidate.Body, body, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.PmlId, pmlId, StringComparison.OrdinalIgnoreCase))
                {
                    versions.Add(new Version(path, File.GetLastWriteTimeUtc(path)));
                }
            }

            versions.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            return new Response { Versions = versions };
        }
    }

    /// <summary>One saved version of the active PML.</summary>
    /// <param name="Path">The file path.</param>
    /// <param name="LastWriteTimeUtc">The file's last-write time, used for display and ordering.</param>
    public sealed record Version(string Path, DateTime LastWriteTimeUtc);

    /// <summary>The active PML's saved versions, newest first.</summary>
    public sealed record Response
    {
        /// <summary>Saved versions, newest first; empty when the active map has no identified PML yet.</summary>
        public required IReadOnlyList<Version> Versions { get; init; }
    }
}
