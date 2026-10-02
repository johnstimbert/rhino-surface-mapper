using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Application.Interfaces;

/// <summary>
/// How the user chose to dispose of unsaved changes to the previously active map before a
/// pending location transition can proceed, ported from the three actionable buttons of
/// Python's <c>prepare_to_replace_current_map</c> dialog (its fourth button, Cancel, has no
/// enum member here: "cancel" is simply never calling
/// <see cref="IMapTransitionCoordinator.ResolveOldMapDisposition"/>, leaving the transition
/// blocked exactly as Python's <c>return False</c> does).
/// </summary>
public enum OldMapDisposition
{
    /// <summary>Continue without writing or deleting the previous map's file.</summary>
    Discard,

    /// <summary>Overwrite the previous map's own file with its current in-memory state.</summary>
    SaveReplace,

    /// <summary>Write the previous map's current in-memory state to a new numbered version file.</summary>
    SaveNewVersion,
}

/// <summary>
/// A fully prepared, detached destination map ready to be installed into the live session,
/// ported from the <c>dict(state=..., path=..., source_text=...)</c> literal Python's
/// <c>prepare_pending_destination</c>/<c>nearby_pml_maps</c> flow returns.
/// </summary>
/// <param name="State">The detached candidate session — never the live session instance.</param>
/// <param name="Path">The file path this candidate was loaded from or will be saved to, or <see langword="null"/> for a map not yet persisted.</param>
/// <param name="SourceText">Human-readable provenance text surfaced to the UI (for example <c>"PML [JD3] created"</c>).</param>
public sealed record PendingDestination(MapSession State, string? Path, string SourceText);

/// <summary>
/// Orchestrates the active-map lifecycle / pending-transition state machine ported from
/// <c>rhino_surface_mapper_qt.MapperWindow</c>'s <c>evaluate_status_update</c>/
/// <c>resolve_pending_transition</c>/<c>prepare_pending_destination</c>/
/// <c>activate_pending_destination</c> family of methods. See
/// <c>Application.Services.MapTransitionCoordinator</c>'s type-level remarks for the full design
/// rationale, including the one deliberate adaptation (an interactive Qt dialog replaced by a
/// gate on <see cref="ResolveOldMapDisposition"/>) this phase's summary calls out explicitly.
/// </summary>
public interface IMapTransitionCoordinator
{
    /// <summary>Whether an accepted telemetry sample's identity no longer matches the active map and a transition is pending.</summary>
    bool TransitionRequired { get; }

    /// <summary>Whether the previously active map's unsaved-changes disposition has already been resolved for the current transition.</summary>
    bool PendingOldMapResolved { get; }

    /// <summary>The fully prepared destination, once <see cref="TryPrepareDestinationAsync"/> has succeeded; otherwise <see langword="null"/>.</summary>
    PendingDestination? PendingDestination { get; }

    /// <summary>
    /// Records the most recent accepted telemetry sample, ported from Python's
    /// <c>latest_status_snapshot</c> assignment in <c>poll()</c>. Must be called for every
    /// accepted sample, independent of whether a transition is active, so a freshly prepared
    /// destination can later be revalidated against genuinely fresh telemetry.
    /// </summary>
    void ObserveAcceptedSample(TelemetryStatusSample sample);

    /// <summary>
    /// Captures the first sample whose identity no longer matches the active map, ported from
    /// <c>evaluate_status_update</c>. Idempotent while a transition is already pending: only the
    /// first mismatch after a transition is cleared is recorded, exactly as Python's
    /// <c>not self.transition_required</c> guard requires.
    /// </summary>
    /// <param name="update">The <see cref="StatusUpdate"/> produced by applying the sample to the (still-active) old map.</param>
    /// <param name="correspondence">
    /// <see langword="true"/>/<see langword="false"/> from <c>PmlRules.CorrespondsToMap</c>, or
    /// <see langword="null"/> when correspondence could not be evaluated (no PML identity yet,
    /// or the sample was rejected) — mirrors <c>active_map_corresponds</c>'s three-valued result.
    /// </param>
    /// <param name="rawSample">The raw sample, retained verbatim as the pending snapshot even while <paramref name="update"/> only reflects the no-op "do not record position" pass.</param>
    void EvaluateStatusUpdate(StatusUpdate update, bool? correspondence, TelemetryStatusSample rawSample);

    /// <summary>
    /// Records the user's disposition for the previous map's unsaved changes, unblocking
    /// <see cref="TryResolveOldMapAsync"/> for the current transition. Calling this while no
    /// transition is pending has no effect.
    /// </summary>
    void ResolveOldMapDisposition(OldMapDisposition disposition);

    /// <summary>
    /// Attempts to resolve the previous map's unsaved-changes disposition exactly once per
    /// transition, ported from <c>resolve_pending_transition</c>'s <c>pending_old_map_resolved</c>
    /// gate. Returns immediately (without I/O) once already resolved for this transition.
    /// </summary>
    /// <param name="activeSession">The still-live (old) session, read only to decide whether resolution is even needed.</param>
    /// <param name="cancellationToken">Cancels the save I/O issued when a <c>SaveReplace</c>/<c>SaveNewVersion</c> disposition has been recorded.</param>
    /// <returns><see langword="true"/> once resolved (nothing to save, read-only, or the user already chose a disposition); <see langword="false"/> while still awaiting a user decision.</returns>
    Task<bool> TryResolveOldMapAsync(MapSession activeSession, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds (or locates) the candidate destination map for the pending transition, ported from
    /// <c>prepare_pending_destination</c>. Idempotent: returns immediately once
    /// <see cref="PendingDestination"/> is already set.
    /// </summary>
    /// <returns><see langword="true"/> once a destination is prepared; <see langword="false"/> when the latest telemetry is not yet usable or preparation failed.</returns>
    Task<bool> TryPrepareDestinationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the prepared destination into the live session after revalidating it against the
    /// freshest telemetry, ported from <c>activate_pending_destination</c>.
    /// </summary>
    /// <param name="store">The live session store to install the destination into.</param>
    /// <param name="postInstallAction">
    /// An optional hook run, under the same mutation, immediately after the destination is
    /// installed and before transition state is cleared. Exists so the one Python test
    /// (<c>test_post_commit_activation_failure_rolls_back_live_state</c>) that exercises a
    /// failure <em>after</em> the live session has already been overwritten has a .NET
    /// equivalent to inject; production callers pass <see langword="null"/>. See
    /// <c>MapTransitionCoordinator</c>'s remarks for why this seam exists.
    /// </param>
    /// <param name="cancellationToken">Cancels the mutation before it is applied to the live session store.</param>
    /// <returns><see langword="true"/> once installed and transition state cleared; <see langword="false"/> when blocked, stale, or the destination was rejected (state remains pending, unchanged).</returns>
    /// <exception cref="Exception">
    /// Propagates <paramref name="postInstallAction"/>'s exception after rolling the live session
    /// back to its pre-activation state, exactly as Python's <c>install_prepared_map</c> restores
    /// its snapshot and re-raises.
    /// </exception>
    Task<bool> TryActivateAsync(IMapSessionStore store, Action<MapSession>? postInstallAction = null, CancellationToken cancellationToken = default);

    /// <summary>Clears every lifecycle field, ported from <c>clear_transition_state</c>.</summary>
    void Reset();
}
