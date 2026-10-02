using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Application.Features.MapSession;

/// <summary>
/// Applies one telemetry sample to the live session and drives the pending-transition state
/// machine one step, ported from <c>MapperWindow.poll</c>. This is the single entry point
/// <c>TelemetryHostedService</c> calls every tick instead of mutating the session directly,
/// replacing the Phase 3-deferred "map-reload guards, map-open transitions, correspondence
/// checks" with the full Phase 4 orchestration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the live session is still updated via a direct <c>ProcessStatus</c> call here, not
/// only through the transition coordinator.</b>
/// <see cref="Domain.Services.TelemetryProcessor.Process"/> already safely handles two cases
/// without any PML lookup: (1) the very first telemetry sample of a process lifetime, where
/// <see cref="Domain.Entities.MapSession.BodyKey"/> is still <see langword="null"/> (nothing
/// established yet to look up a correspondence against — matching Python's own unguarded
/// first-assignment behaviour), and (2) every sample that is not a location change at all. Only
/// the third case — an *already-identified* map whose incoming sample reports a different
/// system/body — early-returns from <c>Process</c> without mutating anything, which is exactly
/// the signal (<see cref="StatusUpdate.LocationChanged"/>) this handler uses to hand off to
/// <see cref="IMapTransitionCoordinator"/> instead of ever letting a destructive reassignment
/// happen directly on the live, still-active map.
/// </para>
/// </remarks>
public static class EvaluateTelemetryPoll
{
    /// <summary>One telemetry poll tick to evaluate.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>The parsed <c>Status.json</c> sample for this tick.</summary>
        public required TelemetryStatusSample Sample { get; init; }
    }

    /// <summary>No fields require validation; a malformed sample is the telemetry reader's concern, not this handler's.</summary>
    public sealed class Validator : AbstractValidator<Command>;

    /// <summary>Applies the sample and advances the pending-transition state machine.</summary>
    public sealed class Handler(
        IMapSessionStore store,
        IMapSessionNotifier notifier,
        IMapTransitionCoordinator coordinator) : ICommandHandler<Command, Response>
    {
        /// <inheritdoc />
        public async Task<Response> HandleAsync(Command command, CancellationToken cancellationToken = default)
        {
            var (update, correspondence, effectiveSample) = await store.MutateAsync(
                async (session, _) =>
                {
                    var candidateBefore = ToCandidate(session);

                    // Mirrors Python's own "data['StarSystem'] = state.system" imputation,
                    // applied to poll()'s raw dict BEFORE either active_map_corresponds(...) or
                    // state.process_status(...) see it: Status.json may omit StarSystem for
                    // several seconds after certain transitions, and both correspondence
                    // evaluation and the pending-transition snapshot this sample becomes must
                    // agree with ProcessStatus's own imputed system, not the possibly-blank raw
                    // one.
                    var imputed = ImputeSystem(session, command.Sample);

                    // Correspondence must be computed BEFORE ProcessStatus mutates the session
                    // (against the still-active old map), exactly as Python computes
                    // active_map_corresponds(...) before calling state.process_status(...). This
                    // also lets a same-body "drifted far from the PML centre" sample (correspondence
                    // == false, LocationChanged == false) be detected at all — ProcessStatus never
                    // reports LocationChanged for pure lat/lon drift on the same body, so computing
                    // correspondence only when LocationChanged is already true (as an earlier
                    // revision of this handler did) made that path structurally unreachable.
                    bool? corresponds = EvaluateCorrespondence(session, candidateBefore, imputed);

                    // Python passes record_position=correspondence is not False into
                    // process_status: a sample that no longer corresponds to the active PML must
                    // not be recorded into that PML's trail/Rhino position, exactly like an actual
                    // body change withholds recording.
                    var result = session.ProcessStatus(imputed, recordPosition: corresponds != false);
                    return await Task.FromResult((result, corresponds, imputed)).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);

            if (!update.Accepted)
            {
                return new Response { Result = Result.Rejected };
            }

            coordinator.ObserveAcceptedSample(effectiveSample);

            // Called unconditionally for every accepted sample — not only when LocationChanged is
            // true — so the coordinator's own OR-based gate (LocationChanged || correspondence ==
            // false) can decide whether to start a transition. See MapTransitionCoordinator's
            // remarks for why this cannot be gated here first.
            coordinator.EvaluateStatusUpdate(update, correspondence, effectiveSample);

            // Discrete-vs-continuous StateHasChanged gating is the caller's (TelemetryHostedService's)
            // responsibility, not this handler's: routine, continuous telemetry must not trigger a
            // notification on every ~20 Hz tick (MapScenePresenter already redraws continuous
            // geometry on its own timer). The caller decides from this response's Result alone.

            if (!coordinator.TransitionRequired)
            {
                return new Response { Result = update.LocationChanged || correspondence == false ? Result.TransitionPending : Result.Continued };
            }

            bool oldMapResolved = await store.MutateAsync(
                (session, ct) => coordinator.TryResolveOldMapAsync(session, ct),
                cancellationToken).ConfigureAwait(false);
            if (!oldMapResolved)
            {
                return new Response { Result = Result.TransitionPending };
            }

            bool destinationPrepared = await coordinator.TryPrepareDestinationAsync(cancellationToken).ConfigureAwait(false);
            if (!destinationPrepared)
            {
                return new Response { Result = Result.TransitionPending };
            }

            bool activated = await coordinator.TryActivateAsync(store, postInstallAction: null, cancellationToken).ConfigureAwait(false);
            if (!activated)
            {
                return new Response { Result = Result.TransitionPending };
            }

            notifier.NotifySessionChanged();
            return new Response { Result = Result.Activated };
        }

        /// <summary>
        /// Evaluates whether <paramref name="sample"/> still corresponds to the active PML
        /// without mutating <paramref name="session"/>, ported from
        /// <c>MapperWindow.active_map_corresponds</c>. Returns <see langword="null"/> whenever
        /// correspondence cannot be meaningfully evaluated (not in the SRV, no lat/lon, no body
        /// ever established, or no PML identity/centre recorded yet) — mirroring Python's own
        /// three-valued result. <paramref name="sample"/> must already have gone through
        /// <see cref="ImputeSystem"/>.
        /// </summary>
        private static bool? EvaluateCorrespondence(Domain.Entities.MapSession session, Domain.ValueObjects.PmlCandidate candidateBefore, TelemetryStatusSample sample)
        {
            if ((sample.Flags & Domain.Constants.MapperConstants.SrvFlag) == 0)
            {
                return null;
            }

            if (sample.Latitude is not double lat || sample.Longitude is not double lon)
            {
                return null;
            }

            if (string.IsNullOrEmpty(session.BodyKey) || candidateBefore.PmlId.Trim().Length == 0
                || candidateBefore.PmlCenterLat is null || candidateBefore.PmlCenterLon is null)
            {
                return null;
            }

            string system = sample.StarSystem ?? string.Empty;
            string body = sample.BodyName ?? string.Empty;
            return PmlRules.CorrespondsToMap(candidateBefore, system, body, lat, lon);
        }

        /// <summary>
        /// Fills in a missing <see cref="TelemetryStatusSample.StarSystem"/> from
        /// <paramref name="session"/>'s own current system when the body is unchanged, ported
        /// from the same imputation <see cref="Domain.Services.TelemetryProcessor.Process"/>
        /// applies internally: Status.json may omit <c>StarSystem</c> for several seconds, and
        /// treating that as a body change would be wrong. Applied once, up front, so both
        /// <see cref="EvaluateCorrespondence"/> and the pending-transition snapshot
        /// (<see cref="IMapTransitionCoordinator.EvaluateStatusUpdate"/>'s <c>rawSample</c>) agree
        /// with the system <see cref="Domain.Entities.MapSession.ProcessStatus"/> itself uses.
        /// </summary>
        private static TelemetryStatusSample ImputeSystem(Domain.Entities.MapSession session, TelemetryStatusSample sample)
        {
            string system = sample.StarSystem ?? string.Empty;
            string body = sample.BodyName ?? string.Empty;
            return system.Length == 0 && session.System.Length > 0 && body == session.Body
                ? sample with { StarSystem = session.System }
                : sample;
        }

        private static Domain.ValueObjects.PmlCandidate ToCandidate(Domain.Entities.MapSession session) =>
            Services.PmlCandidateFactory.FromSession(session);
    }

    /// <summary>Outcome of one telemetry poll tick.</summary>
    public sealed record Response
    {
        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="EvaluateTelemetryPoll"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The sample was rejected (the commander is not in the SRV).</summary>
        Rejected,

        /// <summary>The sample belonged to the active map; telemetry/trail state was updated normally.</summary>
        Continued,

        /// <summary>A location transition is pending (awaiting old-map disposition, destination preparation, or activation).</summary>
        TransitionPending,

        /// <summary>A prepared destination was installed as the new live map this tick.</summary>
        Activated,
    }
}
