using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Infrastructure.Migration;
using RhinoSurfaceMapper.Infrastructure.Paths;
using RhinoSurfaceMapper.Infrastructure.Persistence;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Migration;

/// <summary>
/// Covers <see cref="LegacyMapMigrationService"/>'s decision-D7 startup pass: a legacy-literal
/// map is rewritten in English without disturbing persisted timestamps, an already-English map is
/// left completely untouched on disk, malformed JSON is logged and skipped, and the final summary
/// reports accurate migrated/total counts.
/// </summary>
public sealed class LegacyMapMigrationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));
    private readonly RecordingLogger<LegacyMapMigrationService> _logger = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private JsonMapRepository CreateMapRepository() => new(new AppPaths(_root), _clock);

    private LegacyMapMigrationService CreateService() => new(CreateMapRepository(), _logger);

    private static string MinimalMapJson(string size, string? createdAt = null, string? lastSavedAt = null) => $$"""
        {
          "system": "Wytheville",
          "body": "Alpha 1",
          "created_at": {{AsJsonStringOrNull(createdAt)}},
          "last_saved_at": {{AsJsonStringOrNull(lastSavedAt)}},
          "center_lat": 10.0,
          "center_lon": 20.0,
          "deposits": [ { "x": 1.0, "y": 2.0, "lat": 10.1, "lon": 20.1, "name": "d", "size": "{{size}}", "rigs": 1 } ]
        }
        """;

    private static string AsJsonStringOrNull(string? value) => value is null
        ? "null"
        : $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string ExtractRawJsonValue(string json, string propertyName)
    {
        Match match = Regex.Match(
            json,
            $@"""{Regex.Escape(propertyName)}""\s*:\s*(null|""(?:\\.|[^""])*"")",
            RegexOptions.CultureInvariant);

        match.Success.Should().BeTrue($"the JSON should contain a '{propertyName}' property");
        return match.Groups[1].Value;
    }

    [Fact]
    public async Task StartAsync_rewrites_a_map_with_a_legacy_deposit_size_literal_to_English()
    {
        var mapRepository = CreateMapRepository();
        string directory = Path.Combine(new AppPaths(_root).MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "legacy.json");
        await File.WriteAllTextAsync(path, MinimalMapJson("Pequeno"));

        var service = new LegacyMapMigrationService(mapRepository, _logger);
        await service.StartAsync(CancellationToken.None);

        string rewritten = await File.ReadAllTextAsync(path);
        rewritten.Should().Contain("\"Small\"");
        rewritten.Should().NotContain("Pequeno");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("legacy.json"));
    }

    [Fact]
    public async Task StartAsync_leaves_an_already_English_map_untouched()
    {
        var mapRepository = CreateMapRepository();
        string directory = Path.Combine(new AppPaths(_root).MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "english.json");
        await File.WriteAllTextAsync(path, MinimalMapJson("Small"));
        var originalWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        string originalContent = await File.ReadAllTextAsync(path);

        var service = new LegacyMapMigrationService(mapRepository, _logger);
        await service.StartAsync(CancellationToken.None);

        File.GetLastWriteTimeUtc(path).Should().Be(originalWriteTimeUtc, "an already-English map must not be rewritten at all");
        (await File.ReadAllTextAsync(path)).Should().Be(originalContent);
        _logger.Entries.Should().NotContain(e => e.Level == LogLevel.Information && e.Message.Contains("english.json"));
    }

    [Fact]
    public async Task StartAsync_continues_past_one_failing_file_and_still_migrates_the_rest()
    {
        var mapRepository = CreateMapRepository();
        string directory = Path.Combine(new AppPaths(_root).MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string badPath = Path.Combine(directory, "a-bad.json");
        string goodPath = Path.Combine(directory, "b-legacy.json");
        await File.WriteAllTextAsync(badPath, "{ not json");
        await File.WriteAllTextAsync(goodPath, MinimalMapJson("Grande"));

        var service = new LegacyMapMigrationService(mapRepository, _logger);
        await service.StartAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(goodPath)).Should().Contain("\"Large\"");
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("a-bad.json"));
        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Information && e.Message.Contains("b-legacy.json"));
    }

    [Fact]
    public async Task StartAsync_logs_a_single_summary_line_after_the_full_pass()
    {
        var mapRepository = CreateMapRepository();
        string directory = Path.Combine(new AppPaths(_root).MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "legacy.json"), MinimalMapJson("Pequeno"));
        await File.WriteAllTextAsync(Path.Combine(directory, "english.json"), MinimalMapJson("Small"));

        var service = new LegacyMapMigrationService(mapRepository, _logger);
        await service.StartAsync(CancellationToken.None);

        _logger.Entries.Should().ContainSingle(e => e.Message.Contains("of") && e.Message.Contains("maps migrated"));
    }

    [Fact]
    public async Task StartAsync_Should_PreserveCreatedAtAndLastSavedAtJsonStrings_When_MigratingLegacyDepositLiterals()
    {
        string directory = Path.Combine(new AppPaths(_root).MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "legacy-with-timestamps.json");
        const string createdAt = "2026-02-03T04:05:06+01:30";
        const string lastSavedAt = "2026-02-03T07:08:09-04:00";
        string originalJson = MinimalMapJson("Pequeno", createdAt, lastSavedAt);
        await File.WriteAllTextAsync(path, originalJson);

        await CreateService().StartAsync(CancellationToken.None);

        string migratedJson = await File.ReadAllTextAsync(path);
        ExtractRawJsonValue(migratedJson, "created_at").Should().Be(ExtractRawJsonValue(originalJson, "created_at"));
        ExtractRawJsonValue(migratedJson, "last_saved_at").Should().Be(ExtractRawJsonValue(originalJson, "last_saved_at"));
        ExtractRawJsonValue(migratedJson, "created_at").Should().Be($"\"{createdAt}\"");
        ExtractRawJsonValue(migratedJson, "last_saved_at").Should().Be($"\"{lastSavedAt}\"");
        migratedJson.Should().Contain("\"Small\"");
        migratedJson.Should().NotContain("Pequeno");
    }

    [Fact]
    public async Task StartAsync_Should_NotTouchFileBytesOrLastWriteTime_When_MapAlreadyUsesEnglishLiterals()
    {
        string directory = Path.Combine(new AppPaths(_root).MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "already-english.json");
        const string originalJson = """
            { "body":"Alpha 1","system":"Wytheville","center_lon":20.0,"center_lat":10.0,"deposits":[{"rigs":1,"size":"Small","name":"d","lon":20.1,"lat":10.1,"y":2.0,"x":1.0}]}
            """;
        await File.WriteAllTextAsync(path, originalJson);
        var expectedLastWriteTimeUtc = new DateTime(2020, 5, 1, 1, 2, 3, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, expectedLastWriteTimeUtc);
        byte[] originalBytes = await File.ReadAllBytesAsync(path);
        DateTime originalLastWriteTimeUtc = File.GetLastWriteTimeUtc(path);

        await CreateService().StartAsync(CancellationToken.None);

        File.GetLastWriteTimeUtc(path).Should().Be(originalLastWriteTimeUtc);
        (await File.ReadAllBytesAsync(path)).Should().Equal(originalBytes);
    }

    [Fact]
    public async Task StartAsync_Should_LogWarningAndAccurateSummaryAndContinue_When_OneMapFileIsMalformed()
    {
        string directory = Path.Combine(new AppPaths(_root).MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string malformedPath = Path.Combine(directory, "malformed.json");
        string firstLegacyPath = Path.Combine(directory, "legacy-a.json");
        string secondLegacyPath = Path.Combine(directory, "legacy-b.json");
        await File.WriteAllTextAsync(malformedPath, """
            { "system": "Wytheville", "body": "Alpha 1", "center_lat": 10.0, "center_lon": 20.0,
            """);
        await File.WriteAllTextAsync(firstLegacyPath, MinimalMapJson("Grande"));
        await File.WriteAllTextAsync(secondLegacyPath, MinimalMapJson("Enorme"));

        await CreateService().StartAsync(CancellationToken.None);

        (await File.ReadAllTextAsync(firstLegacyPath)).Should().Contain("\"Large\"");
        (await File.ReadAllTextAsync(secondLegacyPath)).Should().Contain("\"Huge\"");

        _logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Warning
            && entry.EventId.Id == LogEvents.LegacyMapMigrationFailed
            && entry.Message.Contains("malformed.json", StringComparison.Ordinal)
            && entry.Exception is JsonException);

        _logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Information
            && entry.EventId.Id == LogEvents.LegacyMapMigrationCompleted
            && entry.Message == "2 of 3 maps migrated.");
    }

    [Fact]
    public async Task StartAsync_Should_CatchAndLogJsonException_When_MapFileContainsMalformedJson()
    {
        string directory = Path.Combine(new AppPaths(_root).MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string malformedPath = Path.Combine(directory, "truncated.json");
        string validLegacyPath = Path.Combine(directory, "legacy.json");
        await File.WriteAllTextAsync(malformedPath, """
            {
              "system": "Wytheville",
              "body": "Alpha 1",
              "center_lat": 10.0,
              "center_lon": 20.0,
              "deposits": [ { "x": 1.0, "y": 2.0, "lat": 10.1, "lon": 20.1, "name": "d", "size": "Pequeno", "rigs": 1 } ]
            """);
        await File.WriteAllTextAsync(validLegacyPath, MinimalMapJson("Pequeno"));

        await CreateService().StartAsync(CancellationToken.None);

        var warningEntry = _logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning
            && entry.EventId.Id == LogEvents.LegacyMapMigrationFailed
            && entry.Message.Contains("truncated.json", StringComparison.Ordinal))
            .Subject;

        warningEntry.Exception.Should().BeOfType<JsonException>();
        warningEntry.Exception!.InnerException.Should().BeOfType<JsonException>();
        (await File.ReadAllTextAsync(validLegacyPath)).Should().Contain("\"Small\"");
    }
}
