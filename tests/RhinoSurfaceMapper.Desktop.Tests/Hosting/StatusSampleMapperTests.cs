using System.Text.Json;
using FluentAssertions;
using RhinoSurfaceMapper.Desktop.Hosting;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Desktop.Tests.Hosting;

/// <summary>
/// Unit tests for <see cref="StatusSampleMapper"/>: the raw <c>Status.json</c>-to-
/// <see cref="TelemetryStatusSample"/> field mapping. Deliberately does not re-decide any
/// behaviour already covered by <c>Domain.Tests/Services/TelemetryProcessorTests</c> (bit-flag
/// rules, body-change detection, trail spacing) — this class owns only the JSON field
/// extraction/null-tolerance step that happens *before* a sample reaches
/// <see cref="RhinoSurfaceMapper.Domain.Services.TelemetryProcessor"/>.
/// </summary>
public sealed class StatusSampleMapperTests
{
    private static TelemetryStatusSample Map(string json, FakeClock? clock = null) =>
        StatusSampleMapper.Map(JsonDocument.Parse(json), clock ?? new FakeClock());

    [Fact]
    public void Map_reads_every_field_from_a_fully_populated_document()
    {
        var sample = Map("""
            {
                "Flags": 67108864,
                "Fuel": { "FuelReservoir": 0.42 },
                "Heading": 123.5,
                "Latitude": 10.25,
                "Longitude": -20.75,
                "StarSystem": "Col 123 Sector AB-C d1-2",
                "BodyName": "3 a",
                "PlanetRadius": 500000.0,
                "timestamp": 1700000000.0
            }
            """);

        sample.Flags.Should().Be(67108864);
        sample.FuelReservoir.Should().Be(0.42);
        sample.Heading.Should().Be(123.5);
        sample.Latitude.Should().Be(10.25);
        sample.Longitude.Should().Be(-20.75);
        sample.StarSystem.Should().Be("Col 123 Sector AB-C d1-2");
        sample.BodyName.Should().Be("3 a");
        sample.PlanetRadius.Should().Be(500000.0);
        sample.Timestamp.Should().Be(1700000000.0);
    }

    [Fact]
    public void Map_defaults_Flags_to_zero_when_the_document_has_no_Flags_field()
    {
        var sample = Map("{}");

        sample.Flags.Should().Be(0);
    }

    [Fact]
    public void Map_leaves_FuelReservoir_null_when_the_Fuel_object_is_absent()
    {
        var sample = Map("""{ "Flags": 1 }""");

        sample.FuelReservoir.Should().BeNull();
    }

    [Fact]
    public void Map_leaves_FuelReservoir_null_when_Fuel_is_not_a_JSON_object()
    {
        var sample = Map("""{ "Fuel": "not-an-object" }""");

        sample.FuelReservoir.Should().BeNull();
    }

    [Fact]
    public void Map_leaves_FuelReservoir_null_when_the_Fuel_object_has_no_FuelReservoir_key()
    {
        var sample = Map("""{ "Fuel": { "FuelMain": 1.0 } }""");

        sample.FuelReservoir.Should().BeNull();
    }

    [Theory]
    [InlineData("""{ "Heading": "not-a-number" }""")]
    [InlineData("{}")]
    public void Map_leaves_Heading_null_for_missing_or_non_numeric_values(string json)
    {
        Map(json).Heading.Should().BeNull();
    }

    [Fact]
    public void Map_leaves_Latitude_and_Longitude_null_when_absent_so_the_sample_is_rejected_downstream()
    {
        var sample = Map("""{ "Flags": 67108864 }""");

        sample.Latitude.Should().BeNull();
        sample.Longitude.Should().BeNull();
    }

    [Theory]
    [InlineData("""{ "StarSystem": 12345 }""")]
    [InlineData("""{ "StarSystem": null }""")]
    [InlineData("{}")]
    public void Map_leaves_StarSystem_null_when_absent_or_not_a_JSON_string(string json)
    {
        Map(json).StarSystem.Should().BeNull();
    }

    [Fact]
    public void Map_leaves_StarSystem_as_an_empty_string_when_Status_json_reports_one()
    {
        Map("""{ "StarSystem": "" }""").StarSystem.Should().Be(string.Empty);
    }

    [Theory]
    [InlineData("""{ "BodyName": true }""")]
    [InlineData("{}")]
    public void Map_leaves_BodyName_null_when_absent_or_not_a_JSON_string(string json)
    {
        Map(json).BodyName.Should().BeNull();
    }

    [Fact]
    public void Map_leaves_PlanetRadius_null_when_absent_so_the_domain_default_applies_downstream()
    {
        Map("{}").PlanetRadius.Should().BeNull();
    }

    [Fact]
    public void Map_falls_back_to_the_clocks_UtcNow_when_timestamp_is_absent()
    {
        var clock = new FakeClock { UtcNow = DateTimeOffset.FromUnixTimeSeconds(1_600_000_000) };

        var sample = Map("{}", clock);

        sample.Timestamp.Should().Be(1_600_000_000.0);
    }

    [Fact]
    public void Map_prefers_the_documents_own_timestamp_over_the_clock_fallback()
    {
        var clock = new FakeClock { UtcNow = DateTimeOffset.FromUnixTimeSeconds(1_600_000_000) };

        var sample = Map("""{ "timestamp": 42.5 }""", clock);

        sample.Timestamp.Should().Be(42.5);
    }

    [Fact]
    public void Map_truncates_a_fractional_Flags_value_to_a_long_rather_than_throwing()
    {
        // Elite Dangerous always writes Flags as an integer, but the reader's own contract
        // (TryGetInt64's documented fallback) tolerates a JSON number node that System.Text.Json
        // cannot represent as a plain Int64 (for example one written with a decimal point) by
        // reading it as a double and truncating — this test pins that fallback path rather than
        // leaving it unverified.
        var sample = Map("""{ "Flags": 67108864.9 }""");

        sample.Flags.Should().Be(67108864);
    }

    [Fact]
    public void Map_tolerates_a_completely_empty_document_without_throwing()
    {
        Func<TelemetryStatusSample> act = () => Map("{}");

        act.Should().NotThrow();
    }
}
