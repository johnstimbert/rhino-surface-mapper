using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Application.Services;

/// <summary>
/// Builds the lightweight <see cref="PmlCandidate"/> shape <c>PmlRules</c> needs from a live or
/// freshly loaded <see cref="MapSession"/>. Shared by <c>MapTransitionCoordinator</c> (matching a
/// prepared destination) and <c>Features.MapSession.EvaluateTelemetryPoll</c> (checking whether
/// an already-identified map still corresponds to incoming telemetry) so the two call sites
/// cannot drift apart on which fields matter for correspondence.
/// </summary>
public static class PmlCandidateFactory
{
    /// <summary>Snapshots the fields of <paramref name="session"/> that <c>PmlRules</c> reads.</summary>
    public static PmlCandidate FromSession(MapSession session) => new()
    {
        System = session.System,
        Body = session.Body,
        Radius = session.Radius,
        PmlId = session.PmlId,
        PmlCenterLat = session.PmlCenterLat,
        PmlCenterLon = session.PmlCenterLon,
        BodyKey = session.BodyKey,
        Marks = [.. session.Marks.Select(mark => new PmlCandidateMark(mark.Name, mark.Lat, mark.Lon))],
    };
}
