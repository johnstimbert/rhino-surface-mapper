using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.Domain.Exceptions;

namespace RhinoSurfaceMapper.Domain.Services;

/// <summary>
/// Maps <see cref="DepositSize"/> to and from its persisted string literal. Per decision D7 in
/// the design document, <see cref="Encode"/> now writes the current English canonical literals
/// (<c>"Small"</c>, <c>"Medium"</c>, <c>"Large"</c>, <c>"Huge"</c>), while <see cref="Decode(string?, out bool)"/>
/// still accepts the legacy Portuguese literals <c>MapperState.validate_map</c> originally wrote
/// (<c>"Pequeno"</c>, <c>"Médio"</c>, <c>"Grande"</c>, <c>"Enorme"</c>) in addition to the new
/// English ones, so a map saved by the original Python app — or by an un-migrated .NET build —
/// still loads correctly.
/// </summary>
/// <remarks>
/// Centralised here (not duplicated between <c>MapValidator</c> and the Phase 2 JSON converter)
/// per AGENTS.md's DRY rule. Both literal sets are matched case-sensitively and exactly: no other
/// casing or translation is accepted, matching Python's <c>deposit['size'] not in (...)</c>
/// check. The two eight-literal sets (four legacy, four current) do not overlap, so decoding is
/// never ambiguous.
/// </remarks>
public static class DepositSizeCodec
{
    private static readonly IReadOnlyDictionary<string, DepositSize> LegacyLiterals = new Dictionary<string, DepositSize>(StringComparer.Ordinal)
    {
        ["Pequeno"] = DepositSize.Pequeno,
        ["Médio"] = DepositSize.Medio,
        ["Grande"] = DepositSize.Grande,
        ["Enorme"] = DepositSize.Enorme,
    };

    private static readonly IReadOnlyDictionary<string, DepositSize> EnglishLiterals = new Dictionary<string, DepositSize>(StringComparer.Ordinal)
    {
        ["Small"] = DepositSize.Pequeno,
        ["Medium"] = DepositSize.Medio,
        ["Large"] = DepositSize.Grande,
        ["Huge"] = DepositSize.Enorme,
    };

    private static readonly IReadOnlyDictionary<DepositSize, string> ToLiteralMap = new Dictionary<DepositSize, string>
    {
        [DepositSize.Pequeno] = "Small",
        [DepositSize.Medio] = "Medium",
        [DepositSize.Grande] = "Large",
        [DepositSize.Enorme] = "Huge",
    };

    /// <summary>
    /// Parses a persisted size literal, accepting either a current English literal or a legacy
    /// Portuguese one, and reports via <paramref name="wasLegacyLiteral"/> which set matched so a
    /// caller — the Phase 2 <c>LegacyMapMigrationService</c> — can tell whether the owning map
    /// needs re-saving to migrate its literals to English, without re-deriving either literal
    /// set itself.
    /// </summary>
    /// <param name="literal">The raw persisted literal.</param>
    /// <param name="wasLegacyLiteral">
    /// <see langword="true"/> when <paramref name="literal"/> matched one of the four legacy
    /// Portuguese literals; <see langword="false"/> when it matched one of the four current
    /// English literals.
    /// </param>
    /// <exception cref="MapValidationException">
    /// <paramref name="literal"/> is <see langword="null"/> or is not one of the eight recognised
    /// literals (four legacy, four current).
    /// </exception>
    public static DepositSize Decode(string? literal, out bool wasLegacyLiteral)
    {
        if (literal is not null && EnglishLiterals.TryGetValue(literal, out var englishSize))
        {
            wasLegacyLiteral = false;
            return englishSize;
        }

        if (literal is not null && LegacyLiterals.TryGetValue(literal, out var legacySize))
        {
            wasLegacyLiteral = true;
            return legacySize;
        }

        throw new MapValidationException("Invalid deposit size.");
    }

    /// <summary>
    /// Parses a persisted size literal exactly like <see cref="Decode(string?, out bool)"/>, for
    /// callers (such as <c>MapValidator</c>) that only need the parsed <see cref="DepositSize"/>
    /// and have no use for the legacy/current distinction.
    /// </summary>
    /// <exception cref="MapValidationException">See <see cref="Decode(string?, out bool)"/>.</exception>
    public static DepositSize Decode(string? literal) => Decode(literal, out _);

    /// <summary>Returns the current English literal a <see cref="DepositSize"/> persists as.</summary>
    public static string Encode(DepositSize size) => ToLiteralMap[size];
}
