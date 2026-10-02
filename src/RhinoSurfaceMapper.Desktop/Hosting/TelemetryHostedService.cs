using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.ValueObjects;
using System.IO;

namespace RhinoSurfaceMapper.Desktop.Hosting;

/// <summary>
/// The 50 ms telemetry loop from the design's "Threading and concurrency model" table: reads
/// <c>Status.json</c> when it changed, falls back to the Journal-derived system name when
/// <c>Status.json</c> omits <c>StarSystem</c> (design requirement F2), applies the sample to the
/// live <see cref="MapSession"/> through <see cref="IMapSessionStore"/>, and raises coalesced
/// <see cref="IMapSessionNotifier"/> notifications. Read-only for this phase: no map save/open,
/// no search route, no steering — those are later phases' hosted services.
/// </summary>
/// <remarks>
/// Ported, at the scope this phase needs, from the polling half of Python's
/// <c>MapperWindow.poll</c>: the game-running gate, the <c>read_status_if_changed</c> call, and
/// the "fill a missing <c>StarSystem</c> from the Journal" fallback. The later <c>poll</c> logic
/// (map-reload guards, "Rhino on another body" messaging, map-open transitions) belongs to the
/// map-operations feature (Phase 4) this phase deliberately does not implement.
/// </remarks>
public sealed class TelemetryHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IMapSessionStore _store;
    private readonly IMapSessionNotifier _notifier;
    private readonly IStatusTelemetryReader _statusReader;
    private readonly IJournalIdentityReader _journalReader;
    private readonly IGameProcessCheck _gameProcessCheck;
    private readonly IClock _clock;
    private readonly string _statusPath;
    private readonly ILogger<TelemetryHostedService> _logger;

    private long? _previousMtimeTicks;
    private int _lastGeneration;
    private bool _lastGameRunning;
    private bool _hasAppliedAnySample;
    private bool _lastAppliedSampleStarSystemWasMissing;

    /// <summary>Creates the service with every dependency required to poll, map and apply one telemetry sample.</summary>
    public TelemetryHostedService(
        IMapSessionStore store,
        IMapSessionNotifier notifier,
        IStatusTelemetryReader statusReader,
        IJournalIdentityReader journalReader,
        IGameProcessCheck gameProcessCheck,
        IClock clock,
        IOptions<TelemetryOptions> options,
        ILogger<TelemetryHostedService> logger)
    {
        _store = store;
        _notifier = notifier;
        _statusReader = statusReader;
        _journalReader = journalReader;
        _gameProcessCheck = gameProcessCheck;
        _clock = clock;
        _logger = logger;
        _statusPath = options.Value.StatusPath ?? ResolveDefaultStatusPath();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await PollOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs exactly one poll tick. Every expected failure mode (missing file, partial write,
    /// permission error) is caught and logged at <see cref="LogLevel.Warning"/> so one bad tick
    /// never stops the loop — matching the Python contract that the polling *caller* owns retry
    /// policy, not the reader.
    /// </summary>
    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        bool running = _gameProcessCheck.IsRunning();
        if (running != _lastGameRunning)
        {
            _lastGameRunning = running;
            _logger.GameProcessStateChanged(running);
        }

        if (!running)
        {
            return;
        }

        // Cheap on every tick: the reader only re-parses bytes when its own byte offset has new
        // data, so calling it even on ticks where Status.json did not change costs nothing extra.
        JournalIdentity? journalIdentity = _journalReader.CurrentIdentity();

        StatusReadResult? result;
        try
        {
            result = _statusReader.TryReadIfChanged(_statusPath, _previousMtimeTicks);

            if (result is null && _lastAppliedSampleStarSystemWasMissing && !string.IsNullOrEmpty(journalIdentity?.System))
            {
                // Status.json itself did not change on this tick, but the Journal-derived
                // identity has only just become available after a prior sample whose
                // StarSystem was missing. Python's poll() re-triggers reprocessing of that same
                // (unchanged) payload immediately rather than waiting for the file to change
                // again; force a re-read of the same bytes so the self-heal is not delayed by up
                // to one extra 50 ms tick, matching Python's immediate correction.
                result = _statusReader.TryReadIfChanged(_statusPath, _previousMtimeTicks, force: true);
            }
        }
        catch (IOException ex)
        {
            // FileNotFoundException derives from IOException, so one clause covers both "file
            // missing" and "file locked by another process".
            _logger.TelemetryStatusReadFailed(ex, _statusPath);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.TelemetryStatusReadFailed(ex, _statusPath);
            return;
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Matches read_status_if_changed's documented contract: a partial write surfaces as
            // a JSON error and is not retried by the reader itself — the next 50 ms tick is the
            // retry.
            _logger.TelemetryStatusReadFailed(ex, _statusPath);
            return;
        }

        if (result is null)
        {
            return;
        }

        try
        {
            using (result)
            {
                _previousMtimeTicks = result.MtimeTicks;

                var sample = StatusSampleMapper.Map(result.Document, _clock);
                if (string.IsNullOrEmpty(sample.StarSystem) && !string.IsNullOrEmpty(journalIdentity?.System))
                {
                    // Design requirement F2 / Python poll()'s "data['StarSystem'] = journal_identity.system"
                    // fallback: Status.json omits StarSystem for several seconds after certain
                    // transitions, and the Journal is the authoritative secondary source.
                    sample = sample with { StarSystem = journalIdentity!.System };
                }

                _lastAppliedSampleStarSystemWasMissing = string.IsNullOrEmpty(sample.StarSystem);

                await ApplySampleAsync(sample, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Everything downstream of the guarded read above (Domain invariant checks inside
            // ProcessStatus, a throwing notifier subscriber, scope/log construction) must not be
            // able to permanently wedge this BackgroundService's internal task — one bad tick is
            // logged and the next tick still runs, matching the read-failure contract above.
            _logger.TelemetryApplyFailed(ex);
        }
    }

    private async Task ApplySampleAsync(TelemetryStatusSample sample, CancellationToken cancellationToken)
    {
        StatusUpdate update = StatusUpdate.Rejected;
        await _store.MutateAsync(session => update = session.ProcessStatus(sample), cancellationToken).ConfigureAwait(false);

        if (!update.Accepted)
        {
            return;
        }

        MapSessionSnapshot snapshot = _store.Snapshot;

        // The scope dictionary and BeginScope call are real allocations on a hot 50 ms loop; the
        // only log emitted inside is Debug and the telemetry category defaults to Warning, so
        // skip constructing either unless Debug is actually enabled.
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            using (_logger.BeginScope(new Dictionary<string, object?>
            {
                ["map"] = snapshot.PmlId,
                ["system"] = snapshot.System,
                ["body"] = snapshot.Body,
                ["generation"] = snapshot.MapGeneration,
            }))
            {
                _logger.TelemetryStatusProcessed(update.System, update.Body, update.LocationChanged);
            }
        }

        // Reserve the discrete TelemetryUpdated notification (and the StateHasChanged it drives
        // via MapScenePresenter) for genuinely discrete events — the first sample ever applied
        // (no map yet -> live) or a body/system identity change (update.LocationChanged) —
        // never for routine, continuous telemetry at up to 20 Hz. Continuous geometry is already
        // delivered every tick by MapScenePresenter's own timer-driven scene push, which does
        // not call StateHasChanged.
        bool isDiscreteTelemetryTransition = !_hasAppliedAnySample || update.LocationChanged;
        _hasAppliedAnySample = true;
        if (isDiscreteTelemetryTransition)
        {
            _notifier.NotifyTelemetryUpdated();
        }

        if (snapshot.MapGeneration != _lastGeneration)
        {
            _lastGeneration = snapshot.MapGeneration;
            _notifier.NotifySessionChanged();
        }
    }

    /// <summary>
    /// Resolves Elite Dangerous' standard <c>Status.json</c> location, matching the default in
    /// Python's <c>MapperWindow.__init__</c> (<c>Path.home()/'Saved Games'/'Frontier
    /// Developments'/'Elite Dangerous'/'Status.json'</c>) and
    /// <see cref="RhinoSurfaceMapper.Infrastructure.Telemetry.JournalIdentityReader"/>'s own
    /// default Journal directory.
    /// </summary>
    private static string ResolveDefaultStatusPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Saved Games",
        "Frontier Developments",
        "Elite Dangerous",
        "Status.json");
}
