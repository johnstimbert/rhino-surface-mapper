namespace RhinoSurfaceMapper.Domain.ValueObjects;

/// <summary>
/// Result of <c>TelemetryProcessor.Process</c> handling one telemetry sample, ported from
/// Python's <c>StatusUpdate</c> dataclass in <c>mapper_core.py</c>.
/// </summary>
/// <remarks>
/// Python gives <c>StatusUpdate</c> a <c>__bool__</c> override so existing callers can treat the
/// result as a plain boolean; <see cref="Accepted"/> is the direct equivalent and callers should
/// test it explicitly instead of relying on any implicit truthiness, since C# records have no
/// such operator.
/// </remarks>
/// <param name="Accepted">
/// <see langword="true"/> when the sample was processed (the commander was in the SRV); mirrors
/// the historical boolean contract callers relied on before <c>StatusUpdate</c> existed.
/// </param>
/// <param name="LocationChanged">
/// <see langword="true"/> when the sample's system/body identity differs from the session's
/// current <c>BodyKey</c> — the caller (future map-open flow) must decide whether to switch map.
/// </param>
/// <param name="System">Star system name reported by this sample, or empty when unknown.</param>
/// <param name="Body">Body name reported by this sample, or empty when unknown.</param>
/// <param name="Latitude">Reported latitude in degrees, when available.</param>
/// <param name="Longitude">Reported longitude in degrees, when available.</param>
public sealed record StatusUpdate(
    bool Accepted,
    bool LocationChanged = false,
    string System = "",
    string Body = "",
    double? Latitude = null,
    double? Longitude = null)
{
    /// <summary>The canonical "sample rejected" result, used whenever the SRV flag is clear.</summary>
    public static readonly StatusUpdate Rejected = new(false);
}
