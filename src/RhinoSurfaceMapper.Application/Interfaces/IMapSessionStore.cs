using System.Collections.Immutable;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;

namespace RhinoSurfaceMapper.Application.Interfaces;

/// <summary>
/// Owns the single live <see cref="MapSession"/> shared by every loop (telemetry, radar,
/// steering — later phases) and by the UI, per the design's "Threading and concurrency model":
/// a <see cref="System.Threading.SemaphoreSlim"/> of capacity one serialises every mutation,
/// while readers (rendering, overlay) take an immutable <see cref="MapSessionSnapshot"/> without
/// ever acquiring that semaphore, so a render never blocks the telemetry loop and never observes
/// a torn state partway through a mutation.
/// </summary>
/// <remarks>
/// Declared in <c>Application</c> (not <c>Domain</c>) because it is a singleton service
/// boundary coordinating access to a <c>Domain</c> aggregate across threads/subsystems — the
/// same reasoning the design applies to <see cref="IStatusTelemetryReader"/> and
/// <see cref="IJournalIdentityReader"/>. The design's "Application services and interfaces"
/// table assigns it here explicitly, with an Application-provided singleton implementation
/// (<c>MapSessionStore</c>), consumed by the Desktop hosted services and by
/// <c>UI.Components</c>' map canvas presenter.
/// </remarks>
public interface IMapSessionStore
{
    /// <summary>
    /// Gets the most recently published immutable snapshot of the live session. A plain field
    /// read with no locking: see <see cref="MapSessionSnapshot"/> for why this can never observe
    /// a torn state.
    /// </summary>
    MapSessionSnapshot Snapshot { get; }

    /// <summary>
    /// Runs <paramref name="mutate"/> against the live <see cref="MapSession"/> under the
    /// store's mutation semaphore, then atomically publishes a fresh <see cref="Snapshot"/>
    /// captured from the result.
    /// </summary>
    /// <param name="mutate">
    /// A callback that mutates the live session in place. Must not retain the
    /// <see cref="MapSession"/> instance it is given beyond the call — every mutation must go
    /// through this method so the semaphore actually serialises every writer.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels waiting for the mutation semaphore; <paramref name="mutate"/> itself is run
    /// to completion once the semaphore is acquired, since partially applying a mutation would
    /// risk leaving the session in a state no caller ever intended.
    /// </param>
    Task MutateAsync(Action<MapSession> mutate, CancellationToken cancellationToken = default);
}

/// <summary>
/// An immutable, copy-on-write point-in-time view of a <see cref="MapSession"/>, captured
/// atomically under <see cref="IMapSessionStore"/>'s mutation semaphore and then published as a
/// single reference assignment. Every collection is an <see cref="ImmutableArray{T}"/> copy, so
/// a reader can enumerate it freely while a writer is already mutating the next generation of
/// the live session — the "copy-on-write... never blocks... never observes a torn state"
/// guarantee from the design's concurrency model, applied uniformly to every collection the
/// session owns, not only the trail, so later phases (radar coverage, deposits, rigs, marks
/// rendering) can extend their consumers without this type's shape changing.
/// </summary>
/// <param name="MapGeneration">Mirrors <see cref="MapSession.MapGeneration"/> at capture time.</param>
/// <param name="System">Mirrors <see cref="MapSession.System"/> at capture time.</param>
/// <param name="Body">Mirrors <see cref="MapSession.Body"/> at capture time.</param>
/// <param name="PmlId">Mirrors <see cref="MapSession.PmlId"/> at capture time, used only for log scope context.</param>
/// <param name="InSrv">Mirrors <see cref="MapSession.InSrv"/> at capture time.</param>
/// <param name="CenterLat">Mirrors <see cref="MapSession.CenterLat"/> at capture time.</param>
/// <param name="CenterLon">Mirrors <see cref="MapSession.CenterLon"/> at capture time.</param>
/// <param name="Radius">Mirrors <see cref="MapSession.Radius"/> at capture time.</param>
/// <param name="RhinoLat">Mirrors <see cref="MapSession.RhinoLat"/> at capture time.</param>
/// <param name="RhinoLon">Mirrors <see cref="MapSession.RhinoLon"/> at capture time.</param>
/// <param name="RhinoHeading">Mirrors <see cref="MapSession.RhinoHeading"/> at capture time.</param>
/// <param name="Points">Copy-on-write snapshot of <see cref="MapSession.Points"/>.</param>
/// <param name="RadarCoverage">Copy-on-write snapshot of <see cref="MapSession.RadarCoverage"/>.</param>
/// <param name="Deposits">Copy-on-write snapshot of <see cref="MapSession.Deposits"/>.</param>
/// <param name="Rigs">Copy-on-write snapshot of <see cref="MapSession.Rigs"/>.</param>
/// <param name="Marks">Copy-on-write snapshot of <see cref="MapSession.Marks"/>.</param>
public sealed record MapSessionSnapshot(
    int MapGeneration,
    string System,
    string Body,
    string PmlId,
    bool InSrv,
    double? CenterLat,
    double? CenterLon,
    double Radius,
    double? RhinoLat,
    double? RhinoLon,
    double? RhinoHeading,
    ImmutableArray<TrailPoint> Points,
    ImmutableArray<RadarCoverageDisc> RadarCoverage,
    ImmutableArray<Deposit> Deposits,
    ImmutableArray<Rig> Rigs,
    ImmutableArray<MapMark> Marks)
{
    /// <summary>The snapshot of a brand-new, never-mutated session: no map, no telemetry, every collection empty.</summary>
    public static MapSessionSnapshot Empty { get; } = new(
        MapGeneration: 0,
        System: string.Empty,
        Body: string.Empty,
        PmlId: string.Empty,
        InSrv: false,
        CenterLat: null,
        CenterLon: null,
        Radius: MapperConstants.DefaultRadiusMetres,
        RhinoLat: null,
        RhinoLon: null,
        RhinoHeading: null,
        Points: [],
        RadarCoverage: [],
        Deposits: [],
        Rigs: [],
        Marks: []);

    /// <summary>
    /// Captures a <see cref="MapSessionSnapshot"/> from the live <paramref name="session"/>.
    /// Must only be called while holding the owning store's mutation semaphore, so every
    /// collection copy below observes the same, fully-settled generation of the session.
    /// </summary>
    public static MapSessionSnapshot Capture(MapSession session) => new(
        MapGeneration: session.MapGeneration,
        System: session.System,
        Body: session.Body,
        PmlId: session.PmlId,
        InSrv: session.InSrv,
        CenterLat: session.CenterLat,
        CenterLon: session.CenterLon,
        Radius: session.Radius,
        RhinoLat: session.RhinoLat,
        RhinoLon: session.RhinoLon,
        RhinoHeading: session.RhinoHeading,
        Points: [.. session.Points],
        RadarCoverage: [.. session.RadarCoverage],
        Deposits: [.. session.Deposits],
        Rigs: [.. session.Rigs],
        Marks: [.. session.Marks]);
}
