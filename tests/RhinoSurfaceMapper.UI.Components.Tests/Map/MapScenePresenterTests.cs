using System.Collections.Concurrent;
using FluentAssertions;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Services;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.UI.Components.Map;

namespace RhinoSurfaceMapper.UI.Components.Tests.Map;

/// <summary>
/// Unit tests for <see cref="MapScenePresenter"/>: the coalesced ~20 Hz push loop and the
/// discrete/continuous split the design's "Rendering cadence" section describes. Exercised with
/// a real <see cref="MapSessionStore"/>/<see cref="MapSessionNotifier"/> pair (both already
/// covered independently by <see cref="MapSessionStoreConcurrencyTests"/>) so these tests focus
/// purely on the presenter's own timing/coalescing/lifecycle behaviour.
/// </summary>
public sealed class MapScenePresenterTests
{
    /// <summary>
    /// Appends <paramref name="count"/> trail points to <paramref name="store"/> as fast as the
    /// runtime allows, with no artificial delay between them — this is what "many rapid
    /// notifications/mutations within one 50 ms window" means in practice, since the presenter
    /// never re-pushes merely because <see cref="IMapSessionStore.MutateAsync"/> was called; it
    /// only reads whatever the latest <see cref="IMapSessionStore.Snapshot"/> happens to be once
    /// per timer tick.
    /// </summary>
    private static async Task MutateManyTimesRapidlyAsync(MapSessionStore store, int count)
    {
        // MapScene.FromSnapshot projects Empty whenever CenterLat/CenterLon are unset (no map
        // open yet), so the first mutation establishes a centre — otherwise every push below
        // would observe an empty trail regardless of how many points were appended.
        await store.MutateAsync(session =>
        {
            session.CenterLat = 0.0;
            session.CenterLon = 0.0;
        });

        for (int i = 0; i < count; i++)
        {
            int index = i;
            await store.MutateAsync(session => session.AddPoint(new TrailPoint(index, index, 0, 0, index)));
        }
    }

    [Fact]
    public async Task Presenter_coalesces_many_rapid_mutations_into_far_fewer_pushes_than_mutations()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var pushedGenerations = new ConcurrentQueue<int>();
        var pushedTrailLengths = new ConcurrentQueue<int>();

        ValueTask Push(MapScene scene, CancellationToken cancellationToken)
        {
            pushedGenerations.Enqueue(scene.Generation);
            pushedTrailLengths.Enqueue(scene.Trail.Length);
            return ValueTask.CompletedTask;
        }

        await using var presenter = new MapScenePresenter(store, notifier, Push, TimeSpan.FromMilliseconds(100));
        presenter.Start();

        const int mutationCount = 200;
        await MutateManyTimesRapidlyAsync(store, mutationCount);

        // Give the timer a couple of ticks to catch up and push at least once after the burst.
        await Task.Delay(350);

        pushedTrailLengths.Should().NotBeEmpty("the presenter must have pushed at least one scene during the wait");
        pushedTrailLengths.Count.Should().BeLessThan(mutationCount / 4,
            "200 mutations completed far faster than the 100 ms tick interval, so the presenter must coalesce them into only a handful of pushes, not one push per mutation");

        // The most recent push, once the burst has settled, must reflect every mutation applied
        // before it was read — proving coalescing reads the *latest* state, not a stale one.
        await Task.Delay(150);
        pushedTrailLengths.Last().Should().Be(mutationCount);
    }

    [Fact]
    public async Task Presenter_does_not_push_before_Start_is_called()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var pushCount = 0;

        ValueTask Push(MapScene scene, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref pushCount);
            return ValueTask.CompletedTask;
        }

        await using var presenter = new MapScenePresenter(store, notifier, Push, TimeSpan.FromMilliseconds(30));

        await Task.Delay(150);

        pushCount.Should().Be(0, "a presenter that was never Start()-ed must never push a scene");
    }

    [Fact]
    public async Task DisposeAsync_stops_the_push_loop_so_no_further_pushes_occur()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var pushCount = 0;

        ValueTask Push(MapScene scene, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref pushCount);
            return ValueTask.CompletedTask;
        }

        var presenter = new MapScenePresenter(store, notifier, Push, TimeSpan.FromMilliseconds(20));
        presenter.Start();

        // Let at least one tick happen so we know the loop was actually running.
        await Task.Delay(100);
        pushCount.Should().BeGreaterThan(0, "the loop must have ticked at least once before disposal for this test to be meaningful");

        await presenter.DisposeAsync();
        int countAtDisposal = pushCount;

        // If the PeriodicTimer/background Task leaked, further ticks would keep incrementing
        // pushCount during this wait.
        await Task.Delay(150);

        pushCount.Should().Be(countAtDisposal, "DisposeAsync must cancel the timer loop cleanly so no push happens after disposal");
    }

    [Fact]
    public async Task DisposeAsync_completes_without_throwing_even_though_the_loop_observes_cancellation()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();

        var presenter = new MapScenePresenter(store, notifier, (_, _) => ValueTask.CompletedTask, TimeSpan.FromMilliseconds(20));
        presenter.Start();
        await Task.Delay(50);

        // The OperationCanceledException thrown internally by PeriodicTimer.WaitForNextTickAsync
        // when the token is cancelled must be fully absorbed by the presenter; a caller awaiting
        // DisposeAsync must never see it propagate.
        Func<Task> act = async () => await presenter.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_unsubscribes_from_the_notifier_so_later_notifications_are_not_observed()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var discreteChangedCount = 0;

        var presenter = new MapScenePresenter(store, notifier, (_, _) => ValueTask.CompletedTask, TimeSpan.FromMilliseconds(20));
        presenter.DiscreteStateChanged += () => Interlocked.Increment(ref discreteChangedCount);
        presenter.Start();

        await presenter.DisposeAsync();

        notifier.NotifySessionChanged();
        notifier.NotifyTelemetryUpdated();

        discreteChangedCount.Should().Be(0, "a disposed presenter must not still be subscribed to the notifier's events");
    }

    [Fact]
    public void DiscreteStateChanged_fires_once_per_SessionChanged_notification_even_while_the_timer_is_ticking()
    {
        var store = new MapSessionStore();
        var notifier = new MapSessionNotifier();
        var discreteChangedCount = 0;

        using var presenterOwner = new PresenterOwner(new MapScenePresenter(
            store, notifier, (_, _) => ValueTask.CompletedTask, TimeSpan.FromMilliseconds(5)));
        presenterOwner.Presenter.DiscreteStateChanged += () => Interlocked.Increment(ref discreteChangedCount);
        presenterOwner.Presenter.Start();

        // Race many discrete notifications against the fast-ticking periodic push loop: the
        // discrete-event path must never be silently dropped just because a timer tick happens
        // to land at the same moment — each raised notification is a direct, synchronous event
        // invocation entirely independent of the timer, so every one of them must be observed.
        const int notificationCount = 500;
        for (int i = 0; i < notificationCount; i++)
        {
            notifier.NotifySessionChanged();
        }

        discreteChangedCount.Should().Be(notificationCount,
            "every discrete SessionChanged notification must reach the subscriber exactly once, regardless of concurrent timer ticks");
    }

    /// <summary>
    /// Tiny synchronous-dispose helper so the fast-ticking presenter above is torn down even if
    /// an assertion above throws, without making every test in this class <c>async</c> merely to
    /// call <see cref="IAsyncDisposable.DisposeAsync"/>.
    /// </summary>
    private sealed class PresenterOwner(MapScenePresenter presenter) : IDisposable
    {
        public MapScenePresenter Presenter { get; } = presenter;

        public void Dispose() => Presenter.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
