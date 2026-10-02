using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RhinoSurfaceMapper.Application;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Application.Services;
using RhinoSurfaceMapper.Desktop.Hosting;
using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Desktop.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="TelemetryHostedService"/>: the 50 ms telemetry loop from the
/// design's "Threading and concurrency model" table. Exercises the loop through the public
/// <see cref="BackgroundService"/>/<see cref="IHostedService"/> surface (<c>StartAsync</c>/
/// <c>StopAsync</c>) with a real <see cref="MapSessionStore"/>/<see cref="MapSessionNotifier"/>
/// pair and mocked telemetry/process/clock dependencies, so assertions read the same published
/// snapshot a real renderer would.
/// </summary>
public sealed class TelemetryHostedServiceTests
{
    private const long SrvFlag = 0x04000000;

    /// <summary>
    /// Builds a real <see cref="IMediator"/> wired through <c>AddApplication()</c> but sharing
    /// this test's already-constructed <see cref="MapSessionStore"/>/<see cref="MapSessionNotifier"/>/
    /// <see cref="IClock"/> instances, so assertions against those instances still observe the
    /// effects of dispatching <c>EvaluateTelemetryPoll</c>. <see cref="IMapRepository"/> and
    /// <see cref="IAppPaths"/> are only exercised by the pending-transition machinery, which
    /// these loop-resilience tests never reach (no sample here changes system/body identity), so
    /// bare mocks are sufficient.
    /// </summary>
    private static IMediator CreateMediator(MapSessionStore store, MapSessionNotifier notifier, IClock clock) =>
        CreateMediator(store, notifier, clock, Mock.Of<IMapRepository>(), Mock.Of<IAppPaths>());

    /// <summary>
    /// Overload accepting a real/fake <see cref="IMapRepository"/>/<see cref="IAppPaths"/> pair,
    /// for the three tests below that exercise the pending-transition machinery far enough to
    /// reach <c>EvaluateTelemetryPoll.Result.TransitionPending</c>/<c>Activated</c>, which bare
    /// mocks cannot satisfy (they need to actually load/save map files).
    /// </summary>
    private static IMediator CreateMediator(MapSessionStore store, MapSessionNotifier notifier, IClock clock, IMapRepository repository, IAppPaths appPaths) =>
        BuildProvider(store, notifier, clock, repository, appPaths).GetRequiredService<IMediator>();

    /// <summary>
    /// Builds the same DI container <see cref="CreateMediator(MapSessionStore,MapSessionNotifier,IClock,IMapRepository,IAppPaths)"/>
    /// resolves its <see cref="IMediator"/> from, so <see cref="CreateService"/> can also resolve
    /// the exact same singleton <see cref="IMapTransitionCoordinator"/> instance the mediator's
    /// <c>EvaluateTelemetryPoll</c> handler mutates — required since <see cref="TelemetryHostedService"/>
    /// now calls <c>IMapTransitionCoordinator.Reset()</c> directly on the running→not-running
    /// transition, and a second, independently-constructed coordinator instance would never see
    /// that reset reflected in what the mediator observes (or vice versa).
    /// </summary>
    private static ServiceProvider BuildProvider(MapSessionStore store, MapSessionNotifier notifier, IClock clock, IMapRepository repository, IAppPaths appPaths)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddSingleton<IMapSessionStore>(store);
        services.AddSingleton<IMapSessionNotifier>(notifier);
        services.AddSingleton(clock);
        services.AddSingleton(repository);
        services.AddSingleton(appPaths);
        services.AddLogging();
        return services.BuildServiceProvider();
    }

    private static TelemetryHostedService CreateService(
        MapSessionStore store,
        MapSessionNotifier notifier,
        Mock<IStatusTelemetryReader> statusReader,
        Mock<IJournalIdentityReader> journalReader,
        Mock<IGameProcessCheck> gameProcessCheck,
        IClock clock,
        FakeLogger<TelemetryHostedService> logger)
    {
        var provider = BuildProvider(store, notifier, clock, Mock.Of<IMapRepository>(), Mock.Of<IAppPaths>());
        return new(
            store,
            notifier,
            provider.GetRequiredService<IMediator>(),
            provider.GetRequiredService<IMapTransitionCoordinator>(),
            statusReader.Object,
            journalReader.Object,
            gameProcessCheck.Object,
            clock,
            Options.Create(new TelemetryOptions { StatusPath = "unused-mocked-path" }),
            logger);
    }

    /// <summary>Overload used by the Result-dispatch tests below, wiring a real/fake <see cref="IMapRepository"/>/<see cref="IAppPaths"/> pair through the mediator.</summary>
    private static TelemetryHostedService CreateService(
        MapSessionStore store,
        MapSessionNotifier notifier,
        Mock<IStatusTelemetryReader> statusReader,
        Mock<IJournalIdentityReader> journalReader,
        Mock<IGameProcessCheck> gameProcessCheck,
        IClock clock,
        FakeLogger<TelemetryHostedService> logger,
        IMapRepository repository,
        IAppPaths appPaths)
    {
        var provider = BuildProvider(store, notifier, clock, repository, appPaths);
        return new(
            store,
            notifier,
            provider.GetRequiredService<IMediator>(),
            provider.GetRequiredService<IMapTransitionCoordinator>(),
            statusReader.Object,
            journalReader.Object,
            gameProcessCheck.Object,
            clock,
            Options.Create(new TelemetryOptions { StatusPath = "unused-mocked-path" }),
            logger);
    }

    private static Mock<IGameProcessCheck> GameAlwaysRunning()
    {
        var mock = new Mock<IGameProcessCheck>();
        mock.Setup(g => g.IsRunning()).Returns(true);
        return mock;
    }

    private static string ValidStatusJson(string? starSystem = "Col 123 Sector", string bodyName = "A 1", long flags = SrvFlag) =>
        $$"""
        {
            "Flags": {{flags}},
            "Latitude": 10.0,
            "Longitude": 20.0,
            "StarSystem": {{(starSystem is null ? "null" : $"\"{starSystem}\"")}},
            "BodyName": "{{bodyName}}",
            "PlanetRadius": 1000000.0,
            "timestamp": 1000.0
        }
        """;

    /// <summary>Asserts every reader-thrown exception type keeps the poll loop alive and is logged at <see cref="LogLevel.Warning"/>.</summary>
    [Theory]
    [MemberData(nameof(ReaderExceptionFactories))]
    public async Task PollOnceAsync_survives_every_documented_reader_exception_and_logs_a_Warning(Func<Exception> makeException)
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Throws(makeException());
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(260); // ~5 ticks at the 50 ms poll interval.
        await service.StopAsync(CancellationToken.None);

        statusReader.Invocations.Count.Should().BeGreaterThanOrEqualTo(2,
            "the loop must keep polling on subsequent ticks instead of stopping after the first failure");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning,
            "a read failure must be logged at Warning, matching the design's 'one bad tick never stops the loop' contract");
    }

    /// <summary>Supplies one factory per documented caught exception type, so xUnit reports each as its own named case.</summary>
    public static IEnumerable<object[]> ReaderExceptionFactories()
    {
        yield return [(Func<Exception>)(() => new FileNotFoundException("Status.json missing"))];
        yield return [(Func<Exception>)(() => new IOException("Status.json locked by another process"))];
        yield return [(Func<Exception>)(() => new UnauthorizedAccessException("Status.json access denied"))];
        yield return [(Func<Exception>)(() => new JsonException("Status.json mid-write, malformed"))];
    }

    [Fact]
    public async Task PollOnceAsync_does_nothing_when_the_game_process_is_not_running()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var statusReader = new Mock<IStatusTelemetryReader>();
        var journalReader = new Mock<IJournalIdentityReader>();
        var gameProcessCheck = new Mock<IGameProcessCheck>();
        gameProcessCheck.Setup(g => g.IsRunning()).Returns(false);
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        statusReader.Verify(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()), Times.Never,
            "Status.json must not be polled at all while the game process is not running");
        store.Snapshot.Should().BeSameAs(MapSessionSnapshot.Empty);
    }

    /// <summary>
    /// Ports <c>test_game_closing_clears_only_live_session_state</c> end-to-end through the real
    /// poll loop: once accepted telemetry has established live SRV state, the game process
    /// stopping must clear every live-telemetry field (<c>InSrv</c>, Rhino lat/lon) on the
    /// running→not-running transition — exactly once, not on every subsequent tick while still
    /// offline — while the persistent map identity (<c>System</c>/<c>Body</c>) survives
    /// untouched. Also asserts the journal-identity reader's <c>Reset()</c> is invoked exactly
    /// once, proving <see cref="TelemetryHostedService"/>'s new offline path (not just
    /// <see cref="RhinoSurfaceMapper.Domain.Entities.MapSession.SetOffline"/> in isolation) is
    /// wired end-to-end.
    /// </summary>
    [Fact]
    public async Task PollOnceAsync_clears_live_telemetry_once_on_the_running_to_not_running_transition_while_preserving_persistent_map_data()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson())));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);

        int runningCalls = 0;
        var gameProcessCheck = new Mock<IGameProcessCheck>();

        // Running for the first few ticks (long enough for at least one sample to be applied and
        // observed below), then permanently not running for the remainder of the test.
        gameProcessCheck.Setup(g => g.IsRunning()).Returns(() => Interlocked.Increment(ref runningCalls) <= 3);
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(160); // several ticks while running, enough to apply at least one sample.

        store.Snapshot.InSrv.Should().BeTrue("the applied sample must have set live SRV state while the game was running");
        store.Snapshot.System.Should().Be("Col 123 Sector");
        store.Snapshot.Body.Should().Be("A 1");

        await Task.Delay(300); // runs past the running->not-running transition and several offline ticks.
        await service.StopAsync(CancellationToken.None);

        store.Snapshot.InSrv.Should().BeFalse("SetOffline must clear InSrv once the game stops running");
        store.Snapshot.RhinoLat.Should().BeNull("SetOffline must clear live Rhino position once the game stops running");
        store.Snapshot.RhinoLon.Should().BeNull("SetOffline must clear live Rhino position once the game stops running");
        store.Snapshot.System.Should().Be("Col 123 Sector", "persistent map identity must survive a game-stop, matching Python's set_offline contract");
        store.Snapshot.Body.Should().Be("A 1", "persistent map identity must survive a game-stop, matching Python's set_offline contract");

        journalReader.Verify(j => j.Reset(), Times.Once,
            "the journal identity reader must be reset exactly once on the transition, not on every offline tick");
        logger.Entries.Count(e => e.Message.Contains("cleared live telemetry", StringComparison.OrdinalIgnoreCase)).Should().Be(1,
            "the offline transition must be logged exactly once, not once per tick while the game stays closed");
    }

    [Fact]
    public async Task PollOnceAsync_logs_GameProcessStateChanged_only_on_the_running_state_transition()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns((StatusReadResult?)null);
        var journalReader = new Mock<IJournalIdentityReader>();
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(260); // several ticks, IsRunning() stays true throughout.
        await service.StopAsync(CancellationToken.None);

        logger.Entries.Count(e => e.Message.Contains("running state changed")).Should().Be(1,
            "the running-state-changed log must fire exactly once (the initial false-to-true transition), never once per tick");
    }

    [Fact]
    public async Task ApplySampleAsync_uses_StatusJsons_own_StarSystem_when_present_ignoring_the_journal()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson(starSystem: "Status System"))));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns(new JournalIdentity("Journal System", "Journal Body"));
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(80);
        await service.StopAsync(CancellationToken.None);

        store.Snapshot.System.Should().Be("Status System",
            "Status.json's own non-empty StarSystem must win over the Journal fallback");
    }

    [Fact]
    public async Task ApplySampleAsync_falls_back_to_the_journal_identity_when_StatusJsons_StarSystem_is_absent()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson(starSystem: null))));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns(new JournalIdentity("Journal System", "Journal Body"));
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(80);
        await service.StopAsync(CancellationToken.None);

        store.Snapshot.System.Should().Be("Journal System",
            "an absent Status.json StarSystem must be filled in from the Journal-derived identity");
    }

    [Fact]
    public async Task ApplySampleAsync_does_not_consult_the_journal_fallback_when_StatusJsons_StarSystem_is_present()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson(starSystem: "Status System"))));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns(new JournalIdentity("Journal System", "Journal Body"));
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(80);
        await service.StopAsync(CancellationToken.None);

        // CurrentIdentity() is still called every tick (it's cheap/incremental per its own
        // design note), but its *value* must never override a present StarSystem — the
        // assertion above (ApplySampleAsync_uses_StatusJsons_own_StarSystem...) already proves
        // the outcome; this test documents the "only when absent" half of the invariant from the
        // opposite angle by asserting the journal's distinct system name never leaks through.
        store.Snapshot.System.Should().NotBe("Journal System");
    }

    [Fact]
    public async Task ApplySampleAsync_raises_NotifyTelemetryUpdated_for_every_accepted_sample()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var telemetryUpdatedCount = 0;
        notifier.TelemetryUpdated += (_, _) => telemetryUpdatedCount++;

        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson())));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(80);
        await service.StopAsync(CancellationToken.None);

        telemetryUpdatedCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ApplySampleAsync_raises_NotifySessionChanged_when_the_map_generation_changes()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var sessionChangedCount = 0;
        notifier.SessionChanged += (_, _) => sessionChangedCount++;

        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson())));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(80);
        await service.StopAsync(CancellationToken.None);

        // The very first accepted sample always establishes a brand-new map (generation 0 -> 1).
        sessionChangedCount.Should().BeGreaterThan(0);
        store.Snapshot.MapGeneration.Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Regression test for review BLOCKING 1: with the same system/body reported on every tick
    /// (no discrete transition past the very first sample), <c>NotifyTelemetryUpdated</c> must
    /// fire exactly once — never once per 20 Hz sample — matching the design's "StateHasChanged
    /// only for discrete state changes" rendering-cadence rule.
    /// </summary>
    [Fact]
    public async Task ApplySampleAsync_does_not_raise_NotifyTelemetryUpdated_again_for_subsequent_samples_with_no_location_change()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var telemetryUpdatedCount = 0;
        notifier.TelemetryUpdated += (_, _) => telemetryUpdatedCount++;

        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson())));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(300); // ~6 ticks; same system/body ("Col 123 Sector"/"A 1") on every one.
        await service.StopAsync(CancellationToken.None);

        statusReader.Invocations.Count.Should().BeGreaterThanOrEqualTo(3,
            "the test is only meaningful if several ticks actually ran");
        telemetryUpdatedCount.Should().Be(1,
            "only the first-ever accepted sample is a discrete no-map-yet -> live transition; every later sample reporting the same body must not refire NotifyTelemetryUpdated");
    }

    /// <summary>
    /// Regression test for review BLOCKING 3: an exception from a notifier subscriber (standing
    /// in for any downstream failure in the mutate/log/notify sequence, including a Domain
    /// invariant violation) must be caught and logged at <see cref="LogLevel.Error"/>, and must
    /// not permanently stop the <see cref="BackgroundService"/>'s internal polling task.
    /// </summary>
    [Fact]
    public async Task PollOnceAsync_survives_a_throwing_notifier_subscriber_and_keeps_polling_on_later_ticks()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        notifier.TelemetryUpdated += (_, _) => throw new InvalidOperationException("subscriber boom");

        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson())));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(260); // several ticks past the one where the subscriber throws.
        await service.StopAsync(CancellationToken.None);

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error,
            "a throwing notifier subscriber must be caught and logged at Error, not left to propagate out of the poll loop");
        statusReader.Invocations.Count.Should().BeGreaterThanOrEqualTo(4,
            "the BackgroundService's internal task must keep calling the reader on later ticks instead of dying after the first exception");
    }

    /// <summary>
    /// Review MEDIUM 6: when a prior sample's <c>StarSystem</c> was missing and the Journal
    /// identity resolves on a later tick where <c>Status.json</c> itself did not change, the
    /// service must force a re-read of the same bytes and self-heal immediately — matching
    /// Python's <c>poll()</c>, which re-triggers reprocessing of the unchanged payload on the
    /// very next poll rather than waiting for the file to change again.
    /// </summary>
    [Fact]
    public async Task PollOnceAsync_forces_a_reread_to_self_heal_StarSystem_once_the_journal_resolves_on_an_unchanged_file()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        const long fixedMtime = 123_456_789L;

        var statusReader = new Mock<IStatusTelemetryReader>();
        int nonForcedCallCount = 0;
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), false))
            .Returns(() =>
            {
                nonForcedCallCount++;
                return nonForcedCallCount == 1
                    ? new StatusReadResult(fixedMtime, JsonDocument.Parse(ValidStatusJson(starSystem: null)))
                    : null;
            });
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), true))
            .Returns(() => new StatusReadResult(fixedMtime, JsonDocument.Parse(ValidStatusJson(starSystem: null))));

        var journalReader = new Mock<IJournalIdentityReader>();
        int journalCallCount = 0;
        journalReader.Setup(j => j.CurrentIdentity()).Returns(() =>
        {
            journalCallCount++;
            return journalCallCount <= 2 ? null : new JournalIdentity("Journal System", "Journal Body");
        });

        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(260); // enough ticks for the file-unchanged tick where the journal has just resolved.
        await service.StopAsync(CancellationToken.None);

        statusReader.Verify(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), true), Times.AtLeastOnce,
            "once a prior sample's StarSystem was missing and the Journal identity resolves while Status.json is unchanged, the next poll must force a re-read instead of waiting for the file to change again");
        store.Snapshot.System.Should().Be("Journal System",
            "the forced re-read must self-heal StarSystem on the very tick the Journal identity became available, matching Python's immediate correction");
    }

    /// <summary>
    /// Regression test for a Phase 4 gap ported from Python's <c>test_load_rereads_unchanged_status_immediately</c>:
    /// <c>install_prepared_map</c> forces <c>self.last_mtime = None</c> and an immediate
    /// <c>poll(reloading_map=True)</c> after installing a freshly loaded/activated map, because
    /// map JSON never carries live telemetry (position/heading/fuel) and the commander could
    /// otherwise be shown as "not present" until Status.json next happens to change on disk. This
    /// service's equivalent is resetting <c>_previousMtimeTicks</c> whenever
    /// <see cref="IMapSessionNotifier.SessionChanged"/> fires (raised by <c>LoadMap</c>,
    /// <c>NewMap</c> and PML activation alike), so the very next tick re-reads and reapplies
    /// telemetry onto the newly-installed session even though the file's bytes/mtime never
    /// changed.
    /// </summary>
    [Fact]
    public async Task ApplySampleAsync_forces_an_immediate_reread_after_a_session_changed_notification_even_when_the_file_is_unchanged()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        const long fixedMtime = 987_654_321L;

        var statusReader = new Mock<IStatusTelemetryReader>();
        int callCountWithNoPreviousMtime = 0;
        statusReader
            .Setup(r => r.TryReadIfChanged(It.IsAny<string>(), null, It.IsAny<bool>()))
            .Returns(() =>
            {
                callCountWithNoPreviousMtime++;
                return new StatusReadResult(fixedMtime, JsonDocument.Parse(ValidStatusJson(starSystem: "Rediscovered")));
            });
        statusReader
            .Setup(r => r.TryReadIfChanged(It.IsAny<string>(), fixedMtime, It.IsAny<bool>()))
            .Returns((StatusReadResult?)null); // Unchanged on every later tick once last_mtime is set.

        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200); // Let the loop fully stabilize: the very first established sample itself raises one
                                // SessionChanged notification (new map bootstrap), which this fix's subscription also
                                // treats as a reason to force one further immediate re-read — both settle out quickly.
        store.Snapshot.System.Should().Be("Rediscovered");
        int baselineCallCount = callCountWithNoPreviousMtime;
        baselineCallCount.Should().BeGreaterThanOrEqualTo(1);

        // Simulate a LoadMap/NewMap/PML-activation install well after the loop has settled: raise
        // SessionChanged without the underlying file itself changing at all.
        notifier.NotifySessionChanged();

        await Task.Delay(150); // Give the loop a further tick to observe the forced re-read.
        await service.StopAsync(CancellationToken.None);

        callCountWithNoPreviousMtime.Should().BeGreaterThan(baselineCallCount,
            "a SessionChanged notification must force the next tick to re-read Status.json as if no previous mtime were known, " +
            "instead of waiting for the file's own mtime to change, so telemetry is reapplied onto the newly-installed session immediately");
    }

    /// <summary>
    /// <c>EvaluateTelemetryPoll.Result.Rejected</c> (a non-SRV sample) must raise neither
    /// notification and must not touch the live session at all, through the real mediator
    /// dispatch path rather than a stubbed response.
    /// </summary>
    [Fact]
    public async Task ApplySampleAsync_raises_no_notifications_and_leaves_the_session_untouched_when_the_result_is_Rejected()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        int telemetryUpdatedCount = 0, sessionChangedCount = 0;
        notifier.TelemetryUpdated += (_, _) => telemetryUpdatedCount++;
        notifier.SessionChanged += (_, _) => sessionChangedCount++;

        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson(flags: 0)))); // No SRV bit set.
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        telemetryUpdatedCount.Should().Be(0, "a rejected (non-SRV) sample must never raise the discrete telemetry notification");
        sessionChangedCount.Should().Be(0, "a rejected sample must never raise the session-changed notification");
        store.Snapshot.MapGeneration.Should().Be(0, "a rejected sample must never establish a map");
        store.Snapshot.System.Should().BeEmpty("a rejected sample must leave the live session completely untouched");
        store.Snapshot.RhinoLat.Should().BeNull("a rejected sample must never record a position");
        store.Snapshot.InSrv.Should().BeFalse();
    }

    /// <summary>
    /// <c>EvaluateTelemetryPoll.Result.TransitionPending</c>: a location-changing sample for an
    /// already-identified, writable map with unsaved changes (no disposition recorded yet) must
    /// raise the discrete telemetry notification (a location change is itself discrete) but must
    /// NOT raise the session-changed notification and must NOT touch the still-live old map,
    /// since nothing has been installed yet.
    /// </summary>
    [Fact]
    public async Task ApplySampleAsync_raises_TelemetryUpdated_but_not_SessionChanged_and_preserves_the_old_map_when_the_result_is_TransitionPending()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        int telemetryUpdatedCount = 0, sessionChangedCount = 0;
        notifier.TelemetryUpdated += (_, _) => telemetryUpdatedCount++;
        notifier.SessionChanged += (_, _) => sessionChangedCount++;

        // Pre-seed an already-identified, writable old map (PmlId + CurrentFilePath both set)
        // before the service ever starts, so every tick reports the same mismatched sample and
        // there is no race between establishing the old map and making it "identified and
        // writable" partway through polling.
        await store.MutateAsync(session =>
        {
            session.ProcessStatus(new Domain.ValueObjects.TelemetryStatusSample(SrvFlag, null, 0, 38, -9, "Sol", "Earth", 1_000_000.0, 1000.0));
            session.PmlId = "6";
            session.CurrentFilePath = "existing-old-map.json";
        });

        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson(starSystem: "Wytheville", bodyName: "New Body"))));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();
        var repository = new FakeMapRepository();
        var appPaths = new FakeAppPaths();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger, repository, appPaths);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(250); // Several ticks, all reporting the same mismatched Wytheville/New Body sample; the
                                // transition can never resolve (no disposition is ever recorded), so it stays gated
                                // on every single tick for as long as the loop runs.
        await service.StopAsync(CancellationToken.None);

        telemetryUpdatedCount.Should().BeGreaterThan(0, "a location-changing sample is itself a discrete transition, even while only pending");
        sessionChangedCount.Should().Be(1,
            "the only session-changed notification must be the one-time bootstrap of the pre-seeded old map itself; " +
            "nothing has been installed for the pending transition, so no further notification must ever fire while gated");
        store.Snapshot.System.Should().Be("Sol", "the still-live old map must be completely untouched while the transition is only pending");
        repository.SavedPaths.Should().BeEmpty("resolving/preparing must not happen at all without a recorded disposition for an identified, writable old map");
    }

    /// <summary>
    /// <c>EvaluateTelemetryPoll.Result.Activated</c>: a location-changing sample for an
    /// already-identified but read-only (protected) old map resolves for free, finds no nearby
    /// PML and so prepares/activates a brand-new one in a single tick, raising both the discrete
    /// telemetry notification and the session-changed notification and swapping the live session
    /// over to the new map.
    /// </summary>
    [Fact]
    public async Task ApplySampleAsync_raises_both_notifications_and_installs_the_new_map_when_the_result_is_Activated()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        int telemetryUpdatedCount = 0, sessionChangedCount = 0;
        notifier.TelemetryUpdated += (_, _) => telemetryUpdatedCount++;
        notifier.SessionChanged += (_, _) => sessionChangedCount++;

        await store.MutateAsync(session =>
        {
            session.ProcessStatus(new Domain.ValueObjects.TelemetryStatusSample(SrvFlag, null, 0, 38, -9, "Sol", "Earth", 1_000_000.0, 1000.0));
            session.PmlId = "6";
            session.Protected = true; // Read-only: the old map resolves for free, no disposition needed.
        });

        var statusReader = new Mock<IStatusTelemetryReader>();
        statusReader.Setup(r => r.TryReadIfChanged(It.IsAny<string>(), It.IsAny<long?>(), It.IsAny<bool>()))
            .Returns(() => new StatusReadResult(DateTime.UtcNow.Ticks, JsonDocument.Parse(ValidStatusJson(starSystem: "Wytheville", bodyName: "New Body"))));
        var journalReader = new Mock<IJournalIdentityReader>();
        journalReader.Setup(j => j.CurrentIdentity()).Returns((JournalIdentity?)null);
        var gameProcessCheck = GameAlwaysRunning();
        var logger = new FakeLogger<TelemetryHostedService>();
        var repository = new FakeMapRepository();
        var appPaths = new FakeAppPaths();

        var service = CreateService(store, notifier, statusReader, journalReader, gameProcessCheck, new FakeClock(), logger, repository, appPaths);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await service.StopAsync(CancellationToken.None);

        telemetryUpdatedCount.Should().BeGreaterThan(0, "an activation is itself a discrete transition");
        sessionChangedCount.Should().BeGreaterThan(0, "EvaluateTelemetryPoll explicitly notifies session-changed once a destination is actually installed");
        store.Snapshot.System.Should().Be("Wytheville", "the new destination must have been installed as the live map");
        repository.SavedPaths.Should().ContainSingle("a brand-new PML with no nearby match must be saved exactly once during preparation");
    }

    private sealed class FakeAppPaths : IAppPaths
    {
        public string BaseDirectory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rsm-desktop-tests-" + Guid.NewGuid());
        public string MapsDirectory => System.IO.Path.Combine(BaseDirectory, "MAPAS");
        public string OptionsPath => System.IO.Path.Combine(BaseDirectory, "options.json");
        public string LogsDirectory => System.IO.Path.Combine(BaseDirectory, "logs");
    }

    private sealed class FakeMapRepository : IMapRepository
    {
        private readonly Dictionary<string, Domain.Entities.MapSession> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _savedPaths = [];

        public IReadOnlyList<string> SavedPaths => _savedPaths;

        public Task<Domain.Entities.MapSession> LoadAsync(string path, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path, out var session))
            {
                throw new FileNotFoundException(path);
            }

            var copy = new Domain.Entities.MapSession();
            copy.LoadFromDocument(session.ToDocument());
            return Task.FromResult(copy);
        }

        public Task SaveAsync(Domain.Entities.MapSession session, string path, bool updateSavedAt = true, CancellationToken ct = default)
        {
            var copy = new Domain.Entities.MapSession();
            copy.LoadFromDocument(session.ToDocument());
            _files[path] = copy;
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
    }
}
