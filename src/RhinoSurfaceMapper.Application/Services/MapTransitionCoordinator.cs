using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Application.Services;

/// <summary>
/// <see cref="IMapTransitionCoordinator"/> implementation, ported from
/// <c>rhino_surface_mapper_qt.MapperWindow</c>'s pending-transition fields and methods
/// (<c>transition_required</c>, <c>pending_status_update</c>, <c>pending_status_snapshot</c>,
/// <c>latest_status_snapshot</c>, <c>pending_old_map_resolved</c>, <c>pending_destination</c>,
/// <c>evaluate_status_update</c>, <c>resolve_pending_transition</c>,
/// <c>prepare_pending_destination</c>, <c>activate_pending_destination</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists as one Application-layer singleton rather than Domain state on
/// <see cref="MapSession"/>.</b> The state machine needs <see cref="IMapRepository"/> I/O
/// (scanning <c>MAPAS/&lt;system&gt;</c> for nearby PML files, saving a freshly identified PML)
/// which <c>Domain</c> may never depend on by the design's dependency rule
/// (<c>Application → Domain</c> only). <see cref="MapSession"/> itself gained exactly one new
/// member for this phase (<see cref="MapSession.InstallFrom"/>, a public alias for the existing
/// private "whole-session swap" used by <c>LoadFromDocument</c>) — every other new rule here is
/// orchestration over existing Domain services (<c>PmlRules</c>, <c>TelemetryProcessor</c>,
/// <c>MapValidator</c> via <c>IMapRepository</c>), exactly mirroring how Python's own
/// <c>MapperState</c> stayed untouched by this feature and all of the new logic landed in the Qt
/// window class instead.
/// </para>
/// <para>
/// <b>The one deliberate adaptation: no blocking modal dialog.</b> Python's
/// <c>prepare_to_replace_current_map</c>/<c>confirm_pml_exit</c> are synchronous
/// <c>QMessageBox</c> calls that block the Qt event loop — including the same 50&#160;ms poll
/// timer that is driving the transition — until the user clicks a button. This port's telemetry
/// loop is a <c>BackgroundService</c> that must never block waiting on Blazor UI input (the
/// design's N2/N6 requirements and the Phase 3 review's "no hot-loop stalls" fixes both forbid
/// it). <see cref="TryResolveOldMapAsync"/> therefore *gates* instead of *blocking*: when the
/// previous map has unsaved changes to resolve, it raises no exception and performs no I/O —
/// it simply returns <see langword="false"/> and leaves <see cref="PendingOldMapResolved"/>
/// unset, exactly as Python's own "Cancel" branch does (<c>return False</c>, state unchanged).
/// A Blazor dialog (<c>TransitionPromptDialog</c> in <c>UI.Components/Dialogs</c>) is expected to
/// present the same three choices Python's dialog offered and call
/// <see cref="ResolveOldMapDisposition"/> with the user's answer; until it does, every poll tick
/// keeps re-checking (via <see cref="TryResolveOldMapAsync"/>) but never auto-decides on the
/// user's behalf — matching the "Cancel leaves the map open" contract exactly, just without a
/// call stack that blocks the poll loop while waiting for it.
/// </para>
/// <para>
/// <b>Multiple nearby PML disambiguation is deliberately not ported.</b> Python's
/// <c>prepare_pending_destination</c> shows a <c>QInputDialog</c> list picker when more than one
/// distinct PML is found within the match radius. <see cref="TryPrepareDestinationAsync"/>
/// instead auto-selects the nearest match (candidates are already distance-sorted by
/// <see cref="PmlRules.MatchingCandidates"/>) — see this phase's summary for the explicit
/// omission and rationale. The far more common single-match and zero-match (new PML) paths are
/// fully ported.
/// </para>
/// <para>
/// <b>New-PML identification is deliberately simplified.</b> Python's <c>setup_new_pml</c> asks
/// the user for a PML id (or confirms a John Doe placeholder) and a bearing/distance to the PML
/// centre via two more modal dialogs — again something a background poll loop cannot block on.
/// When no nearby PML matches, this port auto-allocates a John Doe id
/// (<see cref="PmlRules.NextJohnDoeId"/>) and places the PML centre exactly at the current SRV
/// position (bearing/distance both zero) rather than prompting for an offset. The
/// <c>Pml.IdentifyPml</c> command lets the user *subsequently* supply a real id and a
/// bearing/distance offset for the centre and re-save, matching Python's own allowance for the
/// user to rename/move a John Doe PML once they know its real identifier.
/// </para>
/// <para>
/// <b>Concurrency precondition — single-caller only.</b> <c>_gate</c> is taken around individual
/// field reads/writes, not held across the <c>await</c>-spanning I/O between
/// <see cref="TryResolveOldMapAsync"/>, <see cref="TryPrepareDestinationAsync"/> and
/// <see cref="TryActivateAsync"/>. This is safe today because <c>TelemetryHostedService</c>'s
/// single-threaded poll loop is this type's only caller — it never starts a second prepare/
/// activate sequence before the first one (across possibly several poll ticks) finishes or is
/// reset. If a future caller needs to drive this state machine concurrently (for example, a
/// manual "retry transition" button dispatched from a Blazor event handler on the UI thread
/// while a poll tick is also in flight), the prepare→activate sequence must be protected by a
/// dedicated async gate (a <see cref="System.Threading.SemaphoreSlim"/> held for the whole
/// sequence, not just <c>_gate</c>'s per-field locks) before that caller is added — do not add a
/// second caller without first doing so.
/// </para>
/// </remarks>
public sealed class MapTransitionCoordinator : IMapTransitionCoordinator
{
    private readonly IMapRepository _mapRepository;
    private readonly IAppPaths _appPaths;
    private readonly IClock _clock;
    private readonly Lock _gate = new();

    private bool _transitionRequired;
    private StatusUpdate? _pendingStatusUpdate;
    private TelemetryStatusSample? _pendingStatusSnapshot;
    private TelemetryStatusSample? _latestStatusSnapshot;
    private bool _pendingOldMapResolved;
    private PendingDestination? _pendingDestination;
    private OldMapDisposition? _recordedDisposition;

    /// <summary>Creates the coordinator with the map persistence/paths/clock dependencies it needs to prepare destinations.</summary>
    public MapTransitionCoordinator(IMapRepository mapRepository, IAppPaths appPaths, IClock clock)
    {
        _mapRepository = mapRepository;
        _appPaths = appPaths;
        _clock = clock;
    }

    /// <inheritdoc />
    public bool TransitionRequired
    {
        get { lock (_gate) { return _transitionRequired; } }
    }

    /// <inheritdoc />
    public bool PendingOldMapResolved
    {
        get { lock (_gate) { return _pendingOldMapResolved; } }
    }

    /// <inheritdoc />
    public PendingDestination? PendingDestination
    {
        get { lock (_gate) { return _pendingDestination; } }
    }

    /// <inheritdoc />
    public void ObserveAcceptedSample(TelemetryStatusSample sample)
    {
        lock (_gate)
        {
            _latestStatusSnapshot = sample;
        }
    }

    /// <inheritdoc />
    public void EvaluateStatusUpdate(StatusUpdate update, bool? correspondence, TelemetryStatusSample rawSample)
    {
        lock (_gate)
        {
            // Ported verbatim from Python's own condition in evaluate_status_update:
            //   if (not self.transition_required and status_update.accepted
            //           and (status_update.location_changed or correspondence is False)):
            // This is a pure OR: a transition starts when EITHER the system/body identity
            // changed (LocationChanged), OR the position no longer corresponds to the active
            // PML's centre even while staying on the exact same body (correspondence == false).
            // The second branch is what test_active_map_lifecycle_evaluation_preserves_pending_mismatch
            // exercises: the SRV drives far from the PML centre without ever leaving the body.
            // There is no special-casing of correspondence == true here (an earlier revision of
            // this port incorrectly suppressed a LocationChanged transition whenever
            // correspondence was true — Python has no such exception: a reported identity change
            // always starts a transition, regardless of what the coarser lat/lon correspondence
            // check says).
            if (_transitionRequired || !update.Accepted || !(update.LocationChanged || correspondence == false))
            {
                return;
            }

            _transitionRequired = true;
            _pendingStatusUpdate = update;
            _pendingStatusSnapshot = rawSample;
        }
    }

    /// <inheritdoc />
    public void ResolveOldMapDisposition(OldMapDisposition disposition)
    {
        lock (_gate)
        {
            if (!_transitionRequired)
            {
                return;
            }

            _recordedDisposition = disposition;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryResolveOldMapAsync(MapSession activeSession, CancellationToken cancellationToken = default)
    {
        bool alreadyResolved;
        bool readOnly;
        bool hasCurrentMap;
        OldMapDisposition? disposition;
        lock (_gate)
        {
            if (!_transitionRequired)
            {
                return false;
            }

            alreadyResolved = _pendingOldMapResolved;
            readOnly = activeSession.ReadOnly;
            hasCurrentMap = activeSession.CurrentFilePath is not null || activeSession.PmlId.Trim().Length > 0;
            disposition = _recordedDisposition;
        }

        if (alreadyResolved)
        {
            return true;
        }

        if (readOnly || !hasCurrentMap)
        {
            lock (_gate)
            {
                _pendingOldMapResolved = true;
            }

            return true;
        }

        if (disposition is null)
        {
            // Matches Python's "Cancel" branch: no I/O, no state change, the caller tries again
            // on a later tick once the user has answered.
            return false;
        }

        try
        {
            switch (disposition.Value)
            {
                case OldMapDisposition.Discard:
                    break;
                case OldMapDisposition.SaveReplace:
                    {
                        string path = activeSession.CurrentFilePath
                            ?? PmlRules.PmlPath(_appPaths.MapsDirectory, activeSession.System, activeSession.Body, activeSession.PmlId)
                            ?? throw new MapValidationException("The current map has no associated file yet.");
                        await _mapRepository.SaveAsync(activeSession, path, updateSavedAt: true, cancellationToken).ConfigureAwait(false);
                        activeSession.CurrentFilePath = path;
                        break;
                    }

                case OldMapDisposition.SaveNewVersion:
                    {
                        string? canonical = PmlRules.PmlPath(_appPaths.MapsDirectory, activeSession.System, activeSession.Body, activeSession.PmlId);
                        if (canonical is null)
                        {
                            throw new MapValidationException("The map has no system, body, or identified PML yet.");
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
                        var existingFileNames = Directory.Exists(Path.GetDirectoryName(canonical))
                            ? Directory.EnumerateFiles(Path.GetDirectoryName(canonical)!).Select(Path.GetFileName)!
                            : Enumerable.Empty<string>();
                        string destination = PmlRules.NextVersionPath(canonical, existingFileNames!);
                        await _mapRepository.SaveAsync(activeSession, destination, updateSavedAt: true, cancellationToken).ConfigureAwait(false);
                        activeSession.CurrentFilePath = destination;
                        break;
                    }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MapValidationException)
        {
            // Preparation failure: stays pending, exactly as Python's QMessageBox.critical path
            // leaves transition_required/pending_old_map_resolved untouched on an OSError/ValueError.
            return false;
        }

        lock (_gate)
        {
            _pendingOldMapResolved = true;
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> TryPrepareDestinationAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_pendingDestination is not null)
            {
                return true;
            }
        }

        TelemetryStatusSample? status = CurrentTransitionStatus();
        if (status is null)
        {
            return false;
        }

        string system = status.StarSystem!;
        string body = status.BodyName!;
        double latitude = status.Latitude!.Value;
        double longitude = status.Longitude!.Value;

        try
        {
            var matches = FindNearbyMaps(system, body, latitude, longitude);
            PendingDestination destination;
            if (matches.Count > 0)
            {
                // Nearest match wins automatically; see this type's remarks for the
                // multiple-PML-disambiguation omission.
                string path = matches[0].Path;
                var candidate = await _mapRepository.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                ApplyTelemetry(candidate, status with { StarSystem = candidate.System, BodyName = candidate.Body });
                destination = new PendingDestination(candidate, path, $"PML [{candidate.PmlId}] prepared");
            }
            else
            {
                var candidate = new MapSession();
                ApplyTelemetry(candidate, status);
                string pmlId = PmlRules.NextJohnDoeId(_mapRepository.EnumerateMaps(system), system, body, LoadCandidate);
                candidate.PmlId = pmlId;
                candidate.PmlCenterLat = candidate.RhinoLat;
                candidate.PmlCenterLon = candidate.RhinoLon;
                candidate.CreatedAt = _clock.UtcNow.ToString("yyyy-MM-dd\\THH:mm:ss\\Z", System.Globalization.CultureInfo.InvariantCulture);
                candidate.LastSavedAt = null;
                if (candidate.PmlCenterLat is double centerLat && candidate.PmlCenterLon is double centerLon)
                {
                    var (x, y) = candidate.LocalFromGeographic(centerLat, centerLon);
                    candidate.AddMark(new Domain.Entities.MapMark(Guid.NewGuid(), $"Centro [{pmlId}]", x, y, centerLat, centerLon));
                }

                string? path = PmlRules.PmlPath(_appPaths.MapsDirectory, candidate.System, candidate.Body, candidate.PmlId);
                if (path is null)
                {
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await _mapRepository.SaveAsync(candidate, path, updateSavedAt: true, cancellationToken).ConfigureAwait(false);
                destination = new PendingDestination(candidate, path, $"PML [{pmlId}] created");
            }

            lock (_gate)
            {
                _pendingDestination = destination;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MapValidationException or FormatException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryActivateAsync(IMapSessionStore store, Action<MapSession>? postInstallAction = null, CancellationToken cancellationToken = default)
    {
        PendingDestination? destination;
        bool resolved;
        bool required;
        lock (_gate)
        {
            required = _transitionRequired;
            resolved = _pendingOldMapResolved;
            destination = _pendingDestination;
        }

        if (!required || !resolved || destination is null)
        {
            return false;
        }

        TelemetryStatusSample? status = CurrentTransitionStatus();
        if (status is null)
        {
            return false;
        }

        string system = status.StarSystem!;
        string body = status.BodyName!;
        double latitude = status.Latitude!.Value;
        double longitude = status.Longitude!.Value;

        MapSession candidate = destination.State;
        if (!PmlRules.CorrespondsToMap(ToCandidate(candidate), system, body, latitude, longitude))
        {
            lock (_gate)
            {
                _pendingDestination = null;
            }

            return false;
        }

        try
        {
            ApplyTelemetry(candidate, status with { StarSystem = candidate.System, BodyName = candidate.Body });
        }
        catch (Exception ex) when (ex is InvalidOperationException or MapValidationException)
        {
            lock (_gate)
            {
                _pendingDestination = null;
            }

            return false;
        }

        if (!PmlRules.CorrespondsToMap(ToCandidate(candidate), system, body, latitude, longitude))
        {
            lock (_gate)
            {
                _pendingDestination = null;
            }

            return false;
        }

        bool installed = false;
        await store.MutateAsync(session =>
        {
            var previousDocument = session.ToDocument();
            string? previousPath = session.CurrentFilePath;
            try
            {
                session.InstallFrom(candidate);
                session.CurrentFilePath = destination.Path;
                postInstallAction?.Invoke(session);
                installed = true;
            }
            catch
            {
                session.LoadFromDocument(previousDocument);
                session.CurrentFilePath = previousPath;
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (!installed)
        {
            return false;
        }

        Reset();
        return true;
    }

    /// <inheritdoc />
    public void Reset()
    {
        lock (_gate)
        {
            _transitionRequired = false;
            _pendingStatusUpdate = null;
            _pendingStatusSnapshot = null;
            _latestStatusSnapshot = null;
            _pendingOldMapResolved = false;
            _pendingDestination = null;
            _recordedDisposition = null;
        }
    }

    /// <summary>
    /// Returns the latest telemetry suitable for destination preparation/activation, ported from
    /// <c>_current_transition_status</c>: the SRV flag must be set and system/body/lat/lon must
    /// all be present.
    /// </summary>
    private TelemetryStatusSample? CurrentTransitionStatus()
    {
        TelemetryStatusSample? status;
        lock (_gate)
        {
            status = _latestStatusSnapshot ?? _pendingStatusSnapshot;
        }

        if (status is null || (status.Flags & MapperConstants.SrvFlag) == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(status.StarSystem) || string.IsNullOrWhiteSpace(status.BodyName)
            || status.Latitude is null || status.Longitude is null)
        {
            return null;
        }

        return status;
    }

    private IReadOnlyList<(double Distance, string Path, Domain.ValueObjects.PmlCandidate Candidate)> FindNearbyMaps(string system, string body, double lat, double lon)
    {
        var paths = _mapRepository.EnumerateMaps(system);
        var matches = PmlRules.MatchingCandidates(paths, system, body, lat, lon, LoadCandidate);
        return PmlRules.NewestByPml(matches, path => File.GetLastWriteTimeUtc(path));
    }

    /// <summary>Loads a path into the lightweight <see cref="Domain.ValueObjects.PmlCandidate"/> shape <c>PmlRules</c> needs, ported from <c>_load_candidate</c>.</summary>
    private Domain.ValueObjects.PmlCandidate LoadCandidate(string path)
    {
        MapSession session = _mapRepository.LoadAsync(path).GetAwaiter().GetResult();
        return ToCandidate(session);
    }

    private static Domain.ValueObjects.PmlCandidate ToCandidate(MapSession session) => PmlCandidateFactory.FromSession(session);

    private static void ApplyTelemetry(MapSession session, TelemetryStatusSample sample) => session.ProcessStatus(sample);
}
