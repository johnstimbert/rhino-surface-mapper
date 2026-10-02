namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// The star system/body identity a <see cref="MapSession"/> currently tracks, ported from the
/// <c>system</c>, <c>body</c> and derived <c>body_key</c> fields of <c>MapperState</c>.
/// </summary>
/// <remarks>
/// <paramref name="BodyKey"/> combines system and body as <c>"{system}|{body}"</c> and is the
/// value <c>TelemetryProcessor</c> compares to detect a body change; it is recomputed whenever
/// <see cref="System"/> or <see cref="Body"/> changes rather than being an independent field a
/// caller could desynchronise.
/// </remarks>
/// <param name="System">Current star system name, or <see cref="string.Empty"/> until known.</param>
/// <param name="Body">Current body name, or <see cref="string.Empty"/> until known.</param>
/// <param name="BodyKey">
/// <c>"{System}|{Body}"</c>, or <see langword="null"/> before the first body has ever been
/// established (matching Python's <c>body_key: str | None = None</c> initial state).
/// </param>
public sealed record MapIdentity(string System, string Body, string? BodyKey)
{
    /// <summary>Builds the identity for a given system/body pair, computing <see cref="BodyKey"/>.</summary>
    public static MapIdentity For(string system, string body) => new(system, body, $"{system}|{body}");

    /// <summary>The identity of a brand-new, not-yet-located map session.</summary>
    public static readonly MapIdentity Unknown = new(string.Empty, string.Empty, null);
}
