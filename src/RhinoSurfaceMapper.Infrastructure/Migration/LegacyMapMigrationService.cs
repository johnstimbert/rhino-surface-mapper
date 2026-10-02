using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Infrastructure.Persistence;

namespace RhinoSurfaceMapper.Infrastructure.Migration;

/// <summary>
/// One-time startup pass implementing decision D7 ("English-first persisted literals with
/// legacy-read migration"): every stored map is loaded once, and any map whose deposits used a
/// legacy Portuguese size literal is rewritten in English, in place, without disturbing its
/// user-visible timestamps.
/// </summary>
/// <remarks>
/// <para>
/// Must run, and complete, before any later-phase hosted service that reads map files for
/// telemetry/radar/steering purposes — those services do not exist yet in this phase, so there
/// is nothing to order against in <c>AddInfrastructure</c> today, but
/// <see cref="StartAsync"/> must remain registered first once they are added, since this is the
/// only place that normalises legacy literals before anything else assumes they are already
/// English.
/// </para>
/// <para>
/// Depends on the concrete <see cref="JsonMapRepository"/> (not <see cref="IMapRepository"/>)
/// because the legacy-literal signal this service needs is not part of the frozen
/// <see cref="IMapRepository"/> contract — see
/// <see cref="JsonMapRepository.LoadWithLegacyLiteralInfoAsync"/>. The repository already knows
/// <see cref="IAppPaths.MapsDirectory"/> internally, so this service does not need its own
/// <see cref="IAppPaths"/> dependency.
/// </para>
/// </remarks>
public sealed class LegacyMapMigrationService : IHostedService
{
    private readonly JsonMapRepository _mapRepository;
    private readonly ILogger<LegacyMapMigrationService> _logger;

    /// <summary>Creates the service with its persistence and logging dependencies.</summary>
    public LegacyMapMigrationService(JsonMapRepository mapRepository, ILogger<LegacyMapMigrationService> logger)
    {
        _mapRepository = mapRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Visits every map under <see cref="IAppPaths.MapsDirectory"/> exactly once. A map with no
    /// legacy literal is loaded, checked, and left untouched — rewriting it would still change
    /// its modification time for no content-visible reason, which the design explicitly forbids.
    /// A single file's failure is logged and skipped; it never aborts the remaining pass.
    /// </remarks>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        int migrated = 0;
        int total = 0;

        foreach (string systemName in _mapRepository.EnumerateSystems())
        {
            foreach (string path in _mapRepository.EnumerateMaps(systemName))
            {
                total++;
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var (session, usedLegacyLiterals) = await _mapRepository
                        .LoadWithLegacyLiteralInfoAsync(path, cancellationToken)
                        .ConfigureAwait(false);

                    if (!usedLegacyLiterals)
                    {
                        continue;
                    }

                    // updateSavedAt: false — the rewrite is a transparent format normalisation,
                    // not a user-initiated save, so the map's own "last saved" metadata must not
                    // move even though the file's mtime necessarily will.
                    await _mapRepository.SaveAsync(session, path, updateSavedAt: false, cancellationToken).ConfigureAwait(false);
                    migrated++;

                    _logger.LogInformation(
                        new EventId(LogEvents.LegacyMapMigrated, nameof(LogEvents.LegacyMapMigrated)),
                        "Migrated legacy deposit-size literals in map '{Path}'.",
                        path);
                }
                catch (IOException ex)
                {
                    LogMigrationFailure(path, ex);
                }
                catch (UnauthorizedAccessException ex)
                {
                    LogMigrationFailure(path, ex);
                }
                catch (JsonException ex)
                {
                    // Not in the task's originally enumerated catch list (IOException,
                    // UnauthorizedAccessException, MapValidationException) but added
                    // deliberately: JsonMapRepository.LoadWithLegacyLiteralInfoAsync itself
                    // documents JsonException as a specific, expected failure mode for malformed
                    // JSON, and a single corrupted map must not abort the whole migration pass
                    // any more than a validation failure would.
                    LogMigrationFailure(path, ex);
                }
                catch (MapValidationException ex)
                {
                    LogMigrationFailure(path, ex);
                }
            }
        }

        _logger.LogInformation(
            new EventId(LogEvents.LegacyMapMigrationCompleted, nameof(LogEvents.LegacyMapMigrationCompleted)),
            "{Migrated} of {Total} maps migrated.",
            migrated,
            total);
    }

    /// <inheritdoc />
    /// <remarks>This service performs a single startup pass and owns no resources to release.</remarks>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void LogMigrationFailure(string path, Exception exception) =>
        _logger.LogWarning(
            new EventId(LogEvents.LegacyMapMigrationFailed, nameof(LogEvents.LegacyMapMigrationFailed)),
            exception,
            "Failed to migrate map '{Path}'; it will be retried on the next startup.",
            path);
}
