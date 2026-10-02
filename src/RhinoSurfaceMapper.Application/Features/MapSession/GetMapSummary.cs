using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.Application.Features.MapSession;

/// <summary>
/// Returns a read-only summary of the active map, ported from the assorted status-bar/label
/// fields <c>MapperWindow.refresh</c> reads off <c>MapperState</c> (system, body, PML identity,
/// favourite/protected/mining-only flags, timestamps and record counts). Exists because
/// <see cref="Interfaces.MapSessionSnapshot"/> deliberately omits fields that are session/UI
/// state rather than rendering-cadence data (<c>Favorite</c>, <c>Protected</c>,
/// <c>MiningOnly</c>, timestamps, <c>CurrentFilePath</c>) — see this feature's remarks on why a
/// query exists instead of widening the snapshot.
/// </summary>
public static class GetMapSummary
{
    /// <summary>Requests the current map summary. Carries no parameters.</summary>
    public sealed record Query : IQuery<Response>;

    /// <summary>No fields require validation.</summary>
    public sealed class Validator : AbstractValidator<Query>;

    /// <summary>Reads the live session's summary fields under the store's mutation gate (read-only; nothing is mutated).</summary>
    public sealed class Handler(IMapSessionStore store) : IQueryHandler<Query, Response>
    {
        /// <inheritdoc />
        public async Task<Response> HandleAsync(Query query, CancellationToken cancellationToken = default)
        {
            Response? response = null;
            await store.MutateAsync(session =>
            {
                response = new Response
                {
                    System = session.System,
                    Body = session.Body,
                    PmlId = session.PmlId,
                    Favorite = session.Favorite,
                    Protected = session.Protected,
                    MiningOnly = session.MiningOnly,
                    CreatedAt = session.CreatedAt,
                    LastSavedAt = session.LastSavedAt,
                    CurrentFilePath = session.CurrentFilePath,
                    DepositCount = session.Deposits.Count,
                    RigCount = session.Rigs.Count,
                    MarkCount = session.Marks.Count,
                    PointCount = session.Points.Count,
                };
            }, cancellationToken).ConfigureAwait(false);

            return response!;
        }
    }

    /// <summary>The active map's summary fields.</summary>
    public sealed record Response
    {
        /// <summary>Current star system name, or empty when no map is open.</summary>
        public required string System { get; init; }

        /// <summary>Current body name, or empty when no map is open.</summary>
        public required string Body { get; init; }

        /// <summary>Current PML identifier, or empty when not yet identified.</summary>
        public required string PmlId { get; init; }

        /// <summary>Whether the map is flagged as a favourite.</summary>
        public required bool Favorite { get; init; }

        /// <summary>Whether the map is protected (read-only) on disk.</summary>
        public required bool Protected { get; init; }

        /// <summary>Whether the session is currently in mining-only (read-only, no recording) mode.</summary>
        public required bool MiningOnly { get; init; }

        /// <summary>Creation timestamp, in the persisted ISO-8601 form, or <see langword="null"/> if never saved.</summary>
        public required string? CreatedAt { get; init; }

        /// <summary>Last-saved timestamp, in the persisted ISO-8601 form, or <see langword="null"/> if never saved.</summary>
        public required string? LastSavedAt { get; init; }

        /// <summary>The file this map was last loaded from or saved to, or <see langword="null"/>.</summary>
        public required string? CurrentFilePath { get; init; }

        /// <summary>Number of recorded mining deposits.</summary>
        public required int DepositCount { get; init; }

        /// <summary>Number of placed rigs.</summary>
        public required int RigCount { get; init; }

        /// <summary>Number of placed marks.</summary>
        public required int MarkCount { get; init; }

        /// <summary>Number of recorded trail points.</summary>
        public required int PointCount { get; init; }
    }
}
