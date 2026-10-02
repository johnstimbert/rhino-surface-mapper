using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Domain.Entities;

namespace RhinoSurfaceMapper.Application.Services;

/// <summary>
/// <see cref="IMapSessionStore"/> implementation: a single long-lived <see cref="MapSession"/>
/// guarded by a <see cref="SemaphoreSlim"/> of capacity one, with a lock-free, immutable
/// <see cref="MapSessionSnapshot"/> published after every mutation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why lock-free reads.</b> The design requires that "a render never blocks the telemetry
/// loop and never observes a torn state". Taking the snapshot under the mutation semaphore and
/// having readers acquire that same semaphore would satisfy "never torn" but not "never
/// blocks" — a slow render would stall the next telemetry mutation. Instead, <see cref="Snapshot"/>
/// reads a single field via <see cref="Volatile.Read{T}"/>; because <see cref="MapSessionSnapshot"/>
/// is an immutable record whose every collection is itself an <see cref="System.Collections.Immutable.ImmutableArray{T}"/>,
/// the object graph reachable from any one snapshot reference can never change after
/// publication, so a reader that captured a reference before a concurrent
/// <see cref="Volatile.Write{T}"/> simply keeps observing the previous (complete, consistent)
/// generation — not a half-updated one.
/// </para>
/// <para>
/// Registered as a singleton (see <c>ConfigureServices.AddApplication</c>): exactly one
/// <see cref="MapSession"/> is live for the whole process, matching the design's "single
/// mutation gate" concurrency model (risk R10).
/// </para>
/// </remarks>
public sealed class MapSessionStore : IMapSessionStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MapSession _session = new();
    private MapSessionSnapshot _snapshot = MapSessionSnapshot.Empty;

    /// <inheritdoc />
    public MapSessionSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <inheritdoc />
    public async Task MutateAsync(Action<MapSession> mutate, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            mutate(_session);
            Volatile.Write(ref _snapshot, MapSessionSnapshot.Capture(_session));
        }
        finally
        {
            _gate.Release();
        }
    }
}
