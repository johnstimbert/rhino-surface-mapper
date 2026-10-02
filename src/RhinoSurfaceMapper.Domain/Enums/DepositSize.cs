namespace RhinoSurfaceMapper.Domain.Enums;

/// <summary>
/// Mining deposit size tier, ported from the four Portuguese literals
/// <c>MapperState.validate_map</c> originally accepted (<c>'Pequeno'</c>, <c>'Médio'</c>,
/// <c>'Grande'</c>, <c>'Enorme'</c>).
/// </summary>
/// <remarks>
/// The enum member names are plain ASCII identifiers for C# convenience and are unchanged by
/// decision D7. The persisted/validated string value is produced and parsed by
/// <c>Domain.Services.DepositSizeCodec</c> (used by <c>MapValidator</c>, and reused by the Phase
/// 2 JSON converter) rather than by <see cref="object.ToString()"/>: <c>DepositSizeCodec.Encode</c>
/// now writes the current English literal ("Small"/"Medium"/"Large"/"Huge"), while
/// <c>DepositSizeCodec.Decode</c> still accepts the legacy Portuguese literal named on each
/// member below for backward compatibility with maps saved before the migration. The UI layer
/// displays the same translated Small/Medium/Large/Huge labels; the enum itself carries no
/// display text, matching the Clean Architecture separation of domain rules from presentation.
/// </remarks>
public enum DepositSize
{
    /// <summary>Smallest deposit tier. Persisted as <c>"Small"</c> (legacy: <c>"Pequeno"</c>).</summary>
    Pequeno,

    /// <summary>Medium deposit tier. Persisted as <c>"Medium"</c> (legacy: <c>"Médio"</c>).</summary>
    Medio,

    /// <summary>Large deposit tier. Persisted as <c>"Large"</c> (legacy: <c>"Grande"</c>).</summary>
    Grande,

    /// <summary>Largest deposit tier. Persisted as <c>"Huge"</c> (legacy: <c>"Enorme"</c>).</summary>
    Enorme,
}
