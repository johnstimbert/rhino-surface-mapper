using FluentAssertions;
using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Services;

namespace RhinoSurfaceMapper.Domain.Tests.Services;

/// <summary>
/// Ports decision D7's deposit-size literal migration: <see cref="DepositSizeCodec.Encode"/> now
/// writes the current English literals, while <see cref="DepositSizeCodec.Decode(string?, out bool)"/>
/// still accepts the legacy Portuguese literals the original Python app wrote, and reports which
/// set matched so the Phase 2 <c>LegacyMapMigrationService</c> can decide whether a loaded map
/// needs re-saving.
/// </summary>
public sealed class DepositSizeCodecTests
{
    public static IEnumerable<object[]> AllSizes()
    {
        yield return new object[] { DepositSize.Pequeno, "Small", "Pequeno" };
        yield return new object[] { DepositSize.Medio, "Medium", "Médio" };
        yield return new object[] { DepositSize.Grande, "Large", "Grande" };
        yield return new object[] { DepositSize.Enorme, "Huge", "Enorme" };
    }

    [Theory]
    [MemberData(nameof(AllSizes))]
    public void Encode_writes_the_current_english_literal(DepositSize size, string englishLiteral, string legacyLiteral)
    {
        _ = legacyLiteral;
        DepositSizeCodec.Encode(size).Should().Be(englishLiteral);
    }

    [Theory]
    [MemberData(nameof(AllSizes))]
    public void Decode_accepts_the_legacy_portuguese_literal_and_flags_it_as_legacy(DepositSize size, string englishLiteral, string legacyLiteral)
    {
        _ = englishLiteral;
        var decoded = DepositSizeCodec.Decode(legacyLiteral, out bool wasLegacyLiteral);

        decoded.Should().Be(size);
        wasLegacyLiteral.Should().BeTrue();
    }

    [Theory]
    [MemberData(nameof(AllSizes))]
    public void Decode_accepts_the_current_english_literal_and_flags_it_as_not_legacy(DepositSize size, string englishLiteral, string legacyLiteral)
    {
        _ = legacyLiteral;
        var decoded = DepositSizeCodec.Decode(englishLiteral, out bool wasLegacyLiteral);

        decoded.Should().Be(size);
        wasLegacyLiteral.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pequeno")]
    [InlineData("small")]
    [InlineData("Gigante")]
    public void Decode_rejects_anything_other_than_the_eight_recognised_literals(string? literal)
    {
        var act = () => DepositSizeCodec.Decode(literal, out _);

        act.Should().Throw<MapValidationException>().WithMessage("Invalid deposit size.");
    }

    [Fact]
    public void Decode_single_argument_overload_matches_the_out_parameter_overload()
    {
        DepositSizeCodec.Decode("Huge").Should().Be(DepositSize.Enorme);
        DepositSizeCodec.Decode("Enorme").Should().Be(DepositSize.Enorme);
    }
}
