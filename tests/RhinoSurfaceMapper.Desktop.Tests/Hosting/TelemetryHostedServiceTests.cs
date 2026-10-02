using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using RhinoSurfaceMapper.Application.Interfaces;
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

    private static TelemetryHostedService CreateService(
        MapSessionStore store,
        MapSessionNotifier notifier,
        Mock<IStatusTelemetryReader> statusReader,
        Mock<IJournalIdentityReader> journalReader,
        Mock<IGameProcessCheck> gameProcessCheck,
        IClock clock,
        FakeLogger<TelemetryHostedService> logger) =>
        new(
            store,
            notifier,
            statusReader.Object,
            journalReader.Object,
            gameProcessCheck.Object,
            clock,
            Options.Create(new TelemetryOptions { StatusPath = "unused-mocked-path" }),
            logger);

    private static Mock<IGameProcessCheck> GameAlwaysRunning()
    {
        var mock = new Mock<IGameProcessCheck>();
        mock.Setup(g => g.IsRunning()).Returns(true);
        return mock;
    }

    private static string ValidStatusJson(string? starSystem = "Col 123 Sector", string bodyName = "A 1") =>
        $$"""
        {
            "Flags": {{SrvFlag}},
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
}
