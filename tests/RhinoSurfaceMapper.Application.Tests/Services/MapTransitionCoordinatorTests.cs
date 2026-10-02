using FluentAssertions;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Services;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Application.Tests.Services;

/// <summary>
/// Unit tests for <see cref="MapTransitionCoordinator"/> — the pending-transition/PML-activation
/// state machine, ported (at this port's architecture, not line-by-line) from the lifecycle
/// group of <c>python/tests/test_map_operations.py</c>
/// (<c>test_active_map_lifecycle_evaluation_*</c>, <c>test_pending_transition_*</c>,
/// <c>test_destination_retry_*</c>, <c>test_new_destination_*</c>,
/// <c>test_existing_destination_*</c>, <c>test_pending_destination_activation_*</c>,
/// <c>test_stale_prepared_destination_*</c>, <c>test_activation_failure_*</c>,
/// <c>test_post_commit_activation_failure_rolls_back_live_state</c>). Each test method's XML doc
/// names the Python test(s) it covers.
/// </summary>
public sealed class MapTransitionCoordinatorTests
{
    private const long SrvFlag = MapperConstants.SrvFlag;

    private static TelemetryStatusSample Sample(string system, string body, double lat, double lon, long flags = SrvFlag) =>
        new(flags, null, 0, lat, lon, system, body, 1_000_000.0, 1000.0);

    private static MapSession EstablishedSession(string system = "Sol", string body = "Earth", string pmlId = "6")
    {
        var session = new MapSession();
        session.ProcessStatus(Sample(system, body, 38, -9));
        session.PmlId = pmlId;
        session.PmlCenterLat = 38;
        session.PmlCenterLon = -9;
        return session;
    }

    private static (MapTransitionCoordinator Coordinator, FakeMapRepository Repository, FakeAppPaths Paths) CreateCoordinator()
    {
        var repository = new FakeMapRepository();
        var paths = new FakeAppPaths();
        var coordinator = new MapTransitionCoordinator(repository, paths, new FakeClock());
        return (coordinator, repository, paths);
    }

    /// <summary>
    /// Ports <c>test_active_map_lifecycle_evaluation_preserves_pending_mismatch</c>: a
    /// corresponding update never sets <see cref="IMapTransitionCoordinator.TransitionRequired"/>,
    /// a mismatch sets it once, and every later update (even a later-corresponding one, or a
    /// non-SRV sample) leaves the originally recorded pending state untouched.
    /// </summary>
    [Fact]
    public void EvaluateStatusUpdate_sticks_to_the_first_mismatch_and_ignores_later_updates()
    {
        var (coordinator, _, _) = CreateCoordinator();

        // All of these are same-system/same-body (LocationChanged: false) — exactly the
        // Handler-reachable shape for a drift-away-from-the-PML-centre sample: TelemetryProcessor
        // never reports LocationChanged for pure lat/lon movement on the same body, only
        // `correspondence` varies as the SRV drives around. A LocationChanged: true input here
        // would describe a combination EvaluateTelemetryPoll.Handler can never actually produce
        // (TelemetryProcessor only ever sets LocationChanged alongside an actual BodyKey change),
        // which previously masked the OR/AND guard bug this test exists to catch.
        var nearbyUpdate = new StatusUpdate(true, false, "Sol", "Earth", 38, -9.001);
        coordinator.EvaluateStatusUpdate(nearbyUpdate, correspondence: true, Sample("Sol", "Earth", 38, -9.001));
        coordinator.TransitionRequired.Should().BeFalse("a sample that still corresponds to the active PML must not start a transition");

        var mismatchUpdate = new StatusUpdate(true, false, "Sol", "Earth", 39, -9);
        coordinator.EvaluateStatusUpdate(mismatchUpdate, correspondence: false, Sample("Sol", "Earth", 39, -9));
        coordinator.TransitionRequired.Should().BeTrue("drifting past the PML radius while staying on the same body must start a transition");

        var laterCorrespondingUpdate = new StatusUpdate(true, false, "Sol", "Earth", 38, -9);
        coordinator.EvaluateStatusUpdate(laterCorrespondingUpdate, correspondence: true, Sample("Sol", "Earth", 38, -9));
        coordinator.TransitionRequired.Should().BeTrue("the pending transition is sticky until resolved/activated, not re-evaluated");

        var nonSrvUpdate = new StatusUpdate(true, false, "Sol", "Earth", 38, -9);
        coordinator.EvaluateStatusUpdate(nonSrvUpdate, correspondence: null, Sample("Sol", "Earth", 38, -9, flags: 0));
        coordinator.TransitionRequired.Should().BeTrue();
    }

    /// <summary>
    /// Ports the <c>LocationChanged: true</c> half of
    /// <c>test_active_map_lifecycle_evaluation_handles_identity_and_protection</c>: an actual
    /// body/system change always starts a transition, even when the coarser lat/lon
    /// correspondence check against the old map happens to say <see langword="true"/> — Python's
    /// condition is a pure OR with no exception for this combination.
    /// </summary>
    [Fact]
    public void EvaluateStatusUpdate_starts_a_transition_on_location_change_regardless_of_correspondence()
    {
        var (coordinator, _, _) = CreateCoordinator();

        var update = new StatusUpdate(true, true, "Sol", "Mars", 38, -9);
        coordinator.EvaluateStatusUpdate(update, correspondence: true, Sample("Sol", "Mars", 38, -9));

        coordinator.TransitionRequired.Should().BeTrue("a reported system/body identity change must always start a transition, regardless of correspondence");
    }

    /// <summary>
    /// Ports <c>test_pending_transition_retains_telemetry_and_schedules_once</c>: every accepted
    /// sample updates the latest-telemetry snapshot used for destination preparation, even while
    /// a transition is pending, and <see cref="IMapTransitionCoordinator.ObserveAcceptedSample"/>
    /// never itself flips <see cref="IMapTransitionCoordinator.TransitionRequired"/>.
    /// </summary>
    [Fact]
    public void ObserveAcceptedSample_keeps_latest_telemetry_without_requiring_a_transition()
    {
        var (coordinator, _, _) = CreateCoordinator();

        coordinator.ObserveAcceptedSample(Sample("Sol", "Earth", 38, -9));
        coordinator.TransitionRequired.Should().BeFalse();

        coordinator.ObserveAcceptedSample(Sample("Sol", "Earth", 38.5, -9.5));
        coordinator.TransitionRequired.Should().BeFalse("observing telemetry alone never starts a transition — only a mismatched StatusUpdate does");
    }

    /// <summary>
    /// Ports <c>test_pending_transition_dismissal_does_not_resolve_old_map</c>: without a
    /// recorded disposition, <see cref="IMapTransitionCoordinator.TryResolveOldMapAsync"/> gates
    /// (returns <see langword="false"/>) and performs no I/O or mutation, matching Python's
    /// "Cancel leaves the map open" contract without blocking the caller.
    /// </summary>
    [Fact]
    public async Task TryResolveOldMapAsync_without_a_recorded_disposition_gates_without_mutating()
    {
        var (coordinator, repository, _) = CreateCoordinator();
        var session = EstablishedSession();
        session.CurrentFilePath = "existing.json";

        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Sol", "Mars", 0, 0), false, Sample("Sol", "Mars", 0, 0));

        bool resolved = await coordinator.TryResolveOldMapAsync(session);

        resolved.Should().BeFalse();
        coordinator.PendingOldMapResolved.Should().BeFalse();
        repository.SavedPaths.Should().BeEmpty("gating must not perform any save I/O");
    }

    /// <summary>
    /// Ports <c>test_pending_transition_resolves_old_map_once_without_installing</c> and
    /// <c>test_destination_retry_does_not_repeat_old_map_resolution</c>: once a disposition is
    /// recorded and resolution succeeds, the old map is saved exactly once even across repeated
    /// calls, and resolving never installs a destination.
    /// </summary>
    [Fact]
    public async Task TryResolveOldMapAsync_resolves_exactly_once_and_is_idempotent_on_retry()
    {
        var (coordinator, repository, _) = CreateCoordinator();
        var session = EstablishedSession();
        session.CurrentFilePath = "existing.json";

        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Sol", "Mars", 0, 0), false, Sample("Sol", "Mars", 0, 0));
        coordinator.ResolveOldMapDisposition(OldMapDisposition.SaveReplace);

        bool firstCall = await coordinator.TryResolveOldMapAsync(session);
        bool secondCall = await coordinator.TryResolveOldMapAsync(session);

        firstCall.Should().BeTrue();
        secondCall.Should().BeTrue();
        coordinator.PendingOldMapResolved.Should().BeTrue();
        repository.SavedPaths.Should().ContainSingle().Which.Should().Be("existing.json");
        coordinator.PendingDestination.Should().BeNull("resolving the old map must never itself install a destination");
    }

    /// <summary>
    /// Ports <c>test_pending_transition_gates_foreign_poll_consumers</c>: a read-only/protected
    /// map and a map with no identity yet both resolve for free (no save, nothing to lose),
    /// unlike an identified, writable map which requires an explicit disposition first.
    /// </summary>
    [Fact]
    public async Task TryResolveOldMapAsync_resolves_for_free_when_there_is_nothing_to_lose()
    {
        var (coordinator, repository, _) = CreateCoordinator();
        var session = new MapSession(); // No system/body/PML identity yet.

        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Sol", "Mars", 0, 0), false, Sample("Sol", "Mars", 0, 0));

        bool resolved = await coordinator.TryResolveOldMapAsync(session);

        resolved.Should().BeTrue();
        repository.SavedPaths.Should().BeEmpty();
    }

    /// <summary>
    /// Ports <c>test_new_destination_is_prepared_detached_from_live_state</c>: when no nearby PML
    /// file matches, a fresh <see cref="MapSession"/> is prepared (a John Doe id allocated, PML
    /// centre set to the current SRV position) and saved under its own path, entirely detached
    /// from (never mutating) the live session passed to <see cref="IMapTransitionCoordinator.TryActivateAsync"/>.
    /// </summary>
    [Fact]
    public async Task TryPrepareDestinationAsync_creates_a_new_detached_candidate_when_nothing_matches()
    {
        var (coordinator, repository, _) = CreateCoordinator();
        var liveSession = EstablishedSession();

        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Wytheville", "New Body", 10, 20), false, Sample("Wytheville", "New Body", 10, 20));
        coordinator.ObserveAcceptedSample(Sample("Wytheville", "New Body", 10, 20));

        bool prepared = await coordinator.TryPrepareDestinationAsync();

        prepared.Should().BeTrue();
        coordinator.PendingDestination.Should().NotBeNull();
        coordinator.PendingDestination!.State.PmlId.Should().StartWith("JD");
        coordinator.PendingDestination.State.System.Should().Be("Wytheville");
        liveSession.System.Should().Be("Sol", "preparing a destination must never mutate the still-live session");
        repository.SavedPaths.Should().ContainSingle();
    }

    /// <summary>
    /// Ports <c>test_existing_destination_is_prepared_detached_without_poll</c>: when a nearby
    /// PML file does match, it is loaded as the pending destination (nearest-match auto-selection
    /// — see this type's documented multiple-PML-disambiguation omission) without re-saving it.
    /// </summary>
    [Fact]
    public async Task TryPrepareDestinationAsync_loads_the_nearest_existing_match_without_resaving()
    {
        var (coordinator, repository, paths) = CreateCoordinator();
        var existing = new MapSession();
        existing.ProcessStatus(Sample("Wytheville", "New Body", 10, 20));
        existing.PmlId = "42";
        existing.PmlCenterLat = 10;
        existing.PmlCenterLon = 20;
        string existingPath = System.IO.Path.Combine(paths.MapsDirectory, "Wytheville", "New Body [42].json");
        repository.Add(existingPath, existing);

        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Wytheville", "New Body", 10, 20), false, Sample("Wytheville", "New Body", 10, 20));
        coordinator.ObserveAcceptedSample(Sample("Wytheville", "New Body", 10, 20));

        int savesBefore = repository.SavedPaths.Count;
        bool prepared = await coordinator.TryPrepareDestinationAsync();

        prepared.Should().BeTrue();
        coordinator.PendingDestination!.State.PmlId.Should().Be("42");
        repository.SavedPaths.Count.Should().Be(savesBefore, "loading an existing match must not re-save it");
    }

    /// <summary>
    /// Ports <c>test_activation_failure_preserves_pending_lifecycle_state</c>: activation before
    /// the old map is resolved (or before a destination is prepared) fails without clearing any
    /// pending state, so a later poll tick can still complete the transition.
    /// </summary>
    [Fact]
    public async Task TryActivateAsync_fails_and_preserves_state_when_old_map_not_yet_resolved()
    {
        var (coordinator, _, _) = CreateCoordinator();
        var session = EstablishedSession();
        var store = new MapSessionStore();
        await store.MutateAsync(s => s.InstallFrom(session));

        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Wytheville", "New Body", 10, 20), false, Sample("Wytheville", "New Body", 10, 20));
        coordinator.ObserveAcceptedSample(Sample("Wytheville", "New Body", 10, 20));
        await coordinator.TryPrepareDestinationAsync();

        bool activated = await coordinator.TryActivateAsync(store);

        activated.Should().BeFalse();
        coordinator.TransitionRequired.Should().BeTrue("activation failure must not clear the pending transition");
        coordinator.PendingDestination.Should().NotBeNull("activation failure must not discard the prepared destination");
    }

    /// <summary>
    /// Ports <c>test_pending_destination_activation_clears_state_after_success</c>: once the old
    /// map is resolved and a destination prepared, activation installs the destination into the
    /// live store and resets every pending-transition field.
    /// </summary>
    [Fact]
    public async Task TryActivateAsync_installs_the_destination_and_resets_pending_state_on_success()
    {
        var (coordinator, _, _) = CreateCoordinator();
        var store = new MapSessionStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));

        var update = new StatusUpdate(true, true, "Wytheville", "New Body", 10, 20);
        var sample = Sample("Wytheville", "New Body", 10, 20);
        coordinator.EvaluateStatusUpdate(update, false, sample);
        coordinator.ObserveAcceptedSample(sample);
        coordinator.ResolveOldMapDisposition(OldMapDisposition.Discard);

        bool oldMapResolved = await coordinator.TryResolveOldMapAsync(await ReadSessionAsync(store));
        oldMapResolved.Should().BeTrue();
        (await coordinator.TryPrepareDestinationAsync()).Should().BeTrue();

        bool activated = await coordinator.TryActivateAsync(store);

        activated.Should().BeTrue();
        coordinator.TransitionRequired.Should().BeFalse();
        coordinator.PendingDestination.Should().BeNull();
        coordinator.PendingOldMapResolved.Should().BeFalse("Reset clears every pending field for the next transition");
        store.Snapshot.System.Should().Be("Wytheville");
    }

    /// <summary>
    /// Ports <c>test_post_commit_activation_failure_rolls_back_live_state</c>: if anything after
    /// the live swap throws (simulated here via <c>postInstallAction</c>, since production code
    /// has no natural post-install failure point), the live session is rolled back to exactly
    /// its pre-activation state and the exception propagates.
    /// </summary>
    [Fact]
    public async Task TryActivateAsync_rolls_back_live_state_when_the_post_install_step_throws()
    {
        var (coordinator, _, _) = CreateCoordinator();
        var store = new MapSessionStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));

        var update = new StatusUpdate(true, true, "Wytheville", "New Body", 10, 20);
        var sample = Sample("Wytheville", "New Body", 10, 20);
        coordinator.EvaluateStatusUpdate(update, false, sample);
        coordinator.ObserveAcceptedSample(sample);
        coordinator.ResolveOldMapDisposition(OldMapDisposition.Discard);
        (await coordinator.TryResolveOldMapAsync(await ReadSessionAsync(store))).Should().BeTrue();
        (await coordinator.TryPrepareDestinationAsync()).Should().BeTrue();

        Func<Task> act = () => coordinator.TryActivateAsync(store, postInstallAction: _ => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        store.Snapshot.System.Should().Be("Sol", "a post-install failure must roll the live session back to its pre-activation state");
        coordinator.TransitionRequired.Should().BeTrue("a rolled-back activation must not have cleared the pending transition");
    }

    /// <summary>
    /// Ports <c>test_stale_prepared_destination_remains_pending_without_installing</c>: once a
    /// prepared destination no longer corresponds to the freshest telemetry, activation must
    /// refuse to install it, clear only the stale prepared destination, and leave the old live
    /// map untouched so a later tick can prepare a fresh candidate.
    /// </summary>
    [Fact]
    public async Task TryActivateAsync_clears_only_the_stale_prepared_destination_when_fresh_telemetry_no_longer_matches_it()
    {
        var (coordinator, _, _) = CreateCoordinator();
        var store = new MapSessionStore();
        var liveSession = EstablishedSession();
        liveSession.CurrentFilePath = "active.json";
        await store.MutateAsync(s => s.InstallFrom(liveSession));

        var initialSample = Sample("Wytheville", "New Body", 10, 20);
        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Wytheville", "New Body", 10, 20), false, initialSample);
        coordinator.ObserveAcceptedSample(initialSample);
        coordinator.ResolveOldMapDisposition(OldMapDisposition.Discard);
        (await coordinator.TryResolveOldMapAsync(await ReadSessionAsync(store))).Should().BeTrue();
        (await coordinator.TryPrepareDestinationAsync()).Should().BeTrue();
        coordinator.PendingDestination.Should().NotBeNull();

        coordinator.ObserveAcceptedSample(Sample("Wytheville", "New Body", 10.8, 20));

        bool activated = await coordinator.TryActivateAsync(store);
        MapSession liveAfter = await ReadSessionAsync(store);

        activated.Should().BeFalse();
        coordinator.TransitionRequired.Should().BeTrue();
        coordinator.PendingOldMapResolved.Should().BeTrue();
        coordinator.PendingDestination.Should().BeNull("a stale prepared destination must be discarded so a later tick can re-prepare from fresh telemetry");
        store.Snapshot.System.Should().Be("Sol");
        store.Snapshot.Body.Should().Be("Earth");
        store.Snapshot.PmlId.Should().Be("6");
        liveAfter.CurrentFilePath.Should().Be("active.json");
    }

    /// <summary>
    /// Ports the remaining half of <c>test_new_destination_is_prepared_detached_from_live_state</c>:
    /// while telemetry keeps advancing toward the new location, the prepared destination remains
    /// a detached candidate and the live store stays on the old map until activation is
    /// explicitly attempted.
    /// </summary>
    [Fact]
    public async Task TryActivateAsync_keeps_the_prepared_destination_detached_while_telemetry_keeps_arriving_before_activation()
    {
        var (coordinator, _, _) = CreateCoordinator();
        var store = new MapSessionStore();
        var liveSession = EstablishedSession();
        liveSession.CurrentFilePath = "active.json";
        liveSession.AddDeposit(new Deposit(Guid.NewGuid(), "Old", Domain.Enums.DepositSize.Grande, 2, 0, 0, 38, -9));
        await store.MutateAsync(s => s.InstallFrom(liveSession));

        var initialSample = Sample("Wytheville", "New Body", 10, 20);
        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Wytheville", "New Body", 10, 20), false, initialSample);
        coordinator.ObserveAcceptedSample(initialSample);
        coordinator.ResolveOldMapDisposition(OldMapDisposition.Discard);
        (await coordinator.TryResolveOldMapAsync(await ReadSessionAsync(store))).Should().BeTrue();
        (await coordinator.TryPrepareDestinationAsync()).Should().BeTrue();
        PendingDestination prepared = coordinator.PendingDestination!;
        MapSession candidate = prepared.State;
        string candidatePmlId = candidate.PmlId;
        double? candidateLat = candidate.RhinoLat;
        double? candidateLon = candidate.RhinoLon;

        coordinator.ObserveAcceptedSample(Sample("Wytheville", "New Body", 10.02, 20.01));
        coordinator.ObserveAcceptedSample(Sample("Wytheville", "New Body", 10.03, 20.02));

        coordinator.PendingDestination.Should().NotBeNull();
        coordinator.PendingDestination!.State.Should().BeSameAs(candidate);
        coordinator.PendingDestination.State.System.Should().Be("Wytheville");
        coordinator.PendingDestination.State.PmlId.Should().Be(candidatePmlId);
        coordinator.PendingDestination.State.RhinoLat.Should().Be(candidateLat, "ObserveAcceptedSample updates only the coordinator's telemetry snapshot, not the detached prepared state");
        coordinator.PendingDestination.State.RhinoLon.Should().Be(candidateLon);
        store.Snapshot.System.Should().Be("Sol");
        store.Snapshot.Body.Should().Be("Earth");
        store.Snapshot.Deposits.Should().ContainSingle();

        bool activated = await coordinator.TryActivateAsync(store);
        MapSession liveAfter = await ReadSessionAsync(store);

        activated.Should().BeTrue();
        store.Snapshot.System.Should().Be("Wytheville");
        store.Snapshot.Body.Should().Be("New Body");
        liveAfter.CurrentFilePath.Should().Be(prepared.Path);
        store.Snapshot.Deposits.Should().BeEmpty("the live session should not swap to the detached destination until activation actually installs it");
    }

    private static async Task<MapSession> ReadSessionAsync(MapSessionStore store)
    {
        MapSession? captured = null;
        await store.MutateAsync(session => captured = session);
        return captured!;
    }

    private sealed class FakeAppPaths : IAppPaths
    {
        public string BaseDirectory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rsm-tests-" + Guid.NewGuid());
        public string MapsDirectory => System.IO.Path.Combine(BaseDirectory, "MAPAS");
        public string OptionsPath => System.IO.Path.Combine(BaseDirectory, "options.json");
        public string LogsDirectory => System.IO.Path.Combine(BaseDirectory, "logs");
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public double MonotonicSeconds => 0.0;
    }

    private sealed class FakeMapRepository : IMapRepository
    {
        private readonly Dictionary<string, MapSession> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _savedPaths = [];

        public IReadOnlyList<string> SavedPaths => _savedPaths;

        public void Add(string path, MapSession session) => _files[path] = Clone(session);

        public Task<MapSession> LoadAsync(string path, CancellationToken ct = default) => Task.FromResult(Clone(_files[path]));

        public Task SaveAsync(MapSession session, string path, bool updateSavedAt = true, CancellationToken ct = default)
        {
            _files[path] = Clone(session);
            _savedPaths.Add(path);
            return Task.CompletedTask;
        }

        public Task<bool> IsProtectedAsync(string path, CancellationToken ct = default) => Task.FromResult(false);

        public Task SetFlagsAsync(string path, bool favorite, bool protectedFlag, CancellationToken ct = default) => Task.CompletedTask;

        public Task<(string CreatedAt, string LastSavedAt)> ReadTimestampsAsync(string path, CancellationToken ct = default) =>
            Task.FromResult((_files[path].CreatedAt ?? string.Empty, _files[path].LastSavedAt ?? string.Empty));

        public IReadOnlyList<string> EnumerateMaps(string systemName) =>
            _files.Keys.Where(path => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) == systemName).ToList();

        public IReadOnlyList<string> EnumerateSystems() =>
            _files.Keys.Select(path => System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path))!).Distinct().ToList();

        private static MapSession Clone(MapSession session)
        {
            var copy = new MapSession();
            copy.LoadFromDocument(session.ToDocument());
            return copy;
        }
    }
}
