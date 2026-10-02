using FluentAssertions;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Services;
using RhinoSurfaceMapper.Domain.Entities;

namespace RhinoSurfaceMapper.UI.Components.Tests.Map;

/// <summary>
/// Correctness-critical concurrency tests for <see cref="MapSessionStore"/>: the design's
/// "Threading and concurrency model" requires that a reader's <see cref="IMapSessionStore.Snapshot"/>
/// "never blocks the telemetry loop and never observes a torn state". Exercised here directly
/// rather than only trusted by inspection, per the Phase 3 task's explicit instruction to test
/// this invariant: "start a long-running mutation and assert a concurrently-taken snapshot never
/// shows partial state".
/// </summary>
/// <remarks>
/// Placed in <c>UI.Components.Tests</c> (rather than a hypothetical <c>Application.Tests</c>
/// addition) because the Phase 3 task explicitly lists "<c>IMapSessionStore</c> concurrency
/// behaviour" among the bUnit/xUnit tests this project should contain, alongside the transform
/// math and scene construction tests above.
/// </remarks>
public sealed class MapSessionStoreConcurrencyTests
{
    /// <summary>
    /// Mutates by appending two trail points per call, with a yield between the two appends to
    /// maximise the window in which a reader observing the live <see cref="MapSession"/>
    /// directly (rather than a published snapshot) would catch an odd, "half-written" count.
    /// A concurrent reader looping on <see cref="IMapSessionStore.Snapshot"/> must only ever see
    /// an even <see cref="MapSessionSnapshot.Points"/> length, because a snapshot is only
    /// published once, after both appends have completed.
    /// </summary>
    [Fact]
    public async Task Snapshot_never_observes_a_torn_state_while_a_mutation_is_in_flight()
    {
        var store = new MapSessionStore();
        const int mutationCount = 200;
        using var readerStop = new CancellationTokenSource();
        var oddLengthObserved = false;
        var readerIterations = 0;

        var readerTask = Task.Run(() =>
        {
            while (!readerStop.IsCancellationRequested)
            {
                int length = store.Snapshot.Points.Length;
                Interlocked.Increment(ref readerIterations);
                if (length % 2 != 0)
                {
                    oddLengthObserved = true;
                    break;
                }
            }
        });

        for (int i = 0; i < mutationCount; i++)
        {
            int index = i;
            await store.MutateAsync(session =>
            {
                session.AddPoint(new TrailPoint(X: index, Y: index, Lat: 0, Lon: 0, T: index));
                Thread.Yield();
                session.AddPoint(new TrailPoint(X: index + 0.5, Y: index + 0.5, Lat: 0, Lon: 0, T: index));
            });
        }

        readerStop.Cancel();
        await readerTask;

        oddLengthObserved.Should().BeFalse("a reader must only ever observe a fully-published, even-length snapshot, never a torn one mid-mutation");
        readerIterations.Should().BeGreaterThan(0, "the reader must actually have raced the writer for this test to be meaningful");
        store.Snapshot.Points.Should().HaveCount(mutationCount * 2);
    }

    /// <summary>
    /// Runs many <see cref="IMapSessionStore.MutateAsync"/> calls concurrently and asserts every
    /// one of them was applied — proving the mutation semaphore actually serialises writers
    /// rather than merely slowing them down (a race here would manifest as a final count lower
    /// than <c>writerCount</c>, from two writers reading-then-writing the same
    /// <see cref="List{T}"/> state).
    /// </summary>
    [Fact]
    public async Task Concurrent_writers_are_serialised_so_no_mutation_is_lost()
    {
        var store = new MapSessionStore();
        const int writerCount = 100;

        var writers = Enumerable.Range(0, writerCount).Select(i => store.MutateAsync(session =>
            session.AddPoint(new TrailPoint(X: i, Y: i, Lat: 0, Lon: 0, T: i))));

        await Task.WhenAll(writers);

        store.Snapshot.Points.Should().HaveCount(writerCount);
        store.Snapshot.Points.Select(p => p.X).Should().BeEquivalentTo(Enumerable.Range(0, writerCount).Select(i => (double)i));
    }

    /// <summary>
    /// A snapshot reference captured before a concurrent mutation starts must keep reporting the
    /// pre-mutation state for as long as the caller holds that exact reference — it is a
    /// copy-on-write value, not a live view — demonstrating the "render never blocks... and never
    /// observes a torn state" guarantee from the opposite angle to the first test above.
    /// </summary>
    [Fact]
    public async Task A_previously_captured_snapshot_reference_is_never_mutated_after_the_fact()
    {
        var store = new MapSessionStore();
        await store.MutateAsync(session => session.AddPoint(new TrailPoint(1, 1, 0, 0, 1)));

        MapSessionSnapshot capturedBefore = store.Snapshot;
        capturedBefore.Points.Should().HaveCount(1);

        await store.MutateAsync(session => session.AddPoint(new TrailPoint(2, 2, 0, 0, 2)));

        capturedBefore.Points.Should().HaveCount(1, "the previously captured snapshot instance must remain exactly as it was when captured");
        store.Snapshot.Points.Should().HaveCount(2, "the store's current snapshot must reflect the new mutation");
    }

    /// <summary>
    /// If <c>mutate</c> throws, <see cref="IMapSessionStore.MutateAsync"/> must still release the
    /// mutation semaphore (otherwise every later writer, including the telemetry loop itself,
    /// would deadlock forever waiting on it) and must not publish a new snapshot from whatever
    /// partially-applied state the live session is left in.
    /// </summary>
    [Fact]
    public async Task MutateAsync_releases_the_semaphore_and_leaves_the_published_snapshot_unchanged_when_the_callback_throws()
    {
        var store = new MapSessionStore();
        await store.MutateAsync(session => session.AddPoint(new TrailPoint(1, 1, 0, 0, 1)));
        MapSessionSnapshot snapshotBeforeFailure = store.Snapshot;

        Func<Task> throwingMutation = () => store.MutateAsync(_ => throw new InvalidOperationException("boom"));

        await throwingMutation.Should().ThrowAsync<InvalidOperationException>("the exception must propagate to the caller, not be swallowed");

        store.Snapshot.Should().BeSameAs(snapshotBeforeFailure,
            "a failed mutation must not publish a new snapshot, so readers keep observing the last successfully-published, consistent generation");

        // Prove the semaphore was actually released: a subsequent mutation must complete promptly
        // rather than hang forever waiting on a gate the failed call never released.
        var nextMutation = store.MutateAsync(session => session.AddPoint(new TrailPoint(2, 2, 0, 0, 2)));
        Task completed = await Task.WhenAny(nextMutation, Task.Delay(TimeSpan.FromSeconds(5)));

        completed.Should().BeSameAs(nextMutation, "MutateAsync must not deadlock after a previous callback threw");
        store.Snapshot.Points.Should().HaveCount(2);
    }

    /// <summary>
    /// A burst of concurrently-queued writers must all eventually complete — none starves another
    /// forever — and, since every writer appends exactly one distinguishable point, the final
    /// snapshot must contain all of them exactly once, proving the semaphore's FIFO-ish fairness
    /// is sufficient in practice even under heavy writer contention (the .NET
    /// <see cref="SemaphoreSlim"/> used internally is not strictly FIFO, but it is starvation-free).
    /// </summary>
    [Fact]
    public async Task Many_concurrent_writers_queued_at_once_all_eventually_complete_with_no_writer_starved()
    {
        var store = new MapSessionStore();
        const int writerCount = 500;

        var writers = new Task[writerCount];
        for (int i = 0; i < writerCount; i++)
        {
            int index = i;
            writers[i] = store.MutateAsync(session => session.AddPoint(new TrailPoint(index, index, 0, 0, index)));
        }

        Task all = Task.WhenAll(writers);
        Task completed = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));

        completed.Should().BeSameAs(all, "every queued writer must complete within a bounded time, with none starved indefinitely");
        store.Snapshot.Points.Should().HaveCount(writerCount);
        store.Snapshot.Points.Select(p => p.X).Distinct().Should().HaveCount(writerCount,
            "every writer's distinguishing point must appear exactly once — no mutation lost, none applied twice");
    }

    /// <summary>
    /// A continuous stream of lock-free <see cref="IMapSessionStore.Snapshot"/> reads running
    /// concurrently with writers must never prevent a writer from acquiring the mutation
    /// semaphore — readers never take that semaphore at all, so "reader-starves-writer" is
    /// structurally impossible here, but this test exercises the scenario end-to-end rather than
    /// only trusting the implementation's design intent.
    /// </summary>
    [Fact]
    public async Task A_continuous_flood_of_reads_never_starves_a_concurrent_writer()
    {
        var store = new MapSessionStore();
        using var readerStop = new CancellationTokenSource();

        var readerTask = Task.Run(() =>
        {
            while (!readerStop.IsCancellationRequested)
            {
                _ = store.Snapshot;
            }
        });

        Task writer = store.MutateAsync(session => session.AddPoint(new TrailPoint(1, 1, 0, 0, 1)));
        Task completed = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromSeconds(5)));

        readerStop.Cancel();
        await readerTask;

        completed.Should().BeSameAs(writer, "a flood of concurrent lock-free reads must never block a writer from acquiring the mutation semaphore");
        store.Snapshot.Points.Should().HaveCount(1);
    }

    /// <summary>
    /// Concurrent writers queued against the same store must be applied in some well-defined
    /// serial order — never interleaved at the statement level — demonstrated here by having
    /// every writer append two points it expects to find adjacent; any interleaving would split
    /// a writer's pair with another writer's point.
    /// </summary>
    [Fact]
    public async Task Concurrent_writers_are_never_interleaved_mid_mutation()
    {
        var store = new MapSessionStore();
        const int writerCount = 50;

        var writers = Enumerable.Range(0, writerCount).Select(i => store.MutateAsync(session =>
        {
            session.AddPoint(new TrailPoint(i, i, 0, 0, i));
            Thread.Yield();
            session.AddPoint(new TrailPoint(i, i, 0, 0, i));
        }));

        await Task.WhenAll(writers);

        var points = store.Snapshot.Points;
        points.Should().HaveCount(writerCount * 2);
        for (int i = 0; i < points.Length; i += 2)
        {
            points[i].X.Should().Be(points[i + 1].X, "each writer's two appended points must remain adjacent, proving no other writer's mutation was interleaved between them");
        }
    }
}
