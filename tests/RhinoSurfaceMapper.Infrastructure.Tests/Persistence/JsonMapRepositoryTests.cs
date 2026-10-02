using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Infrastructure.Paths;
using RhinoSurfaceMapper.Infrastructure.Persistence;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Persistence;

/// <summary>
/// Covers <see cref="JsonMapRepository"/> against the acceptance spec in
/// <c>python/tests/test_map_persistence.py</c>, <c>test_map_protection.py</c>, and
/// <c>test_maps_directory.py</c>.
/// </summary>
public sealed class JsonMapRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    private IAppPaths AppPaths => new AppPaths(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private JsonMapRepository CreateRepository() => new(AppPaths, _clock);

    private static MapSession BuildGoldenSession(bool protectedFlag = false)
    {
        var session = new MapSession
        {
            System = "Wytheville",
            Body = "Alpha 1",
            CreatedAt = "2026-01-01T00:00:00Z",
            LastSavedAt = "2026-01-01T00:00:00Z",
            Favorite = true,
            Protected = protectedFlag,
            PmlId = "JD42",
            PmlCenterLat = 10.5,
            PmlCenterLon = -20.25,
            CenterLat = 12.345,
            CenterLon = -45.678,
            Radius = 6_000_000.0,
            CoverageWidthMetres = 1_500.0,
            ScannerRangeMetres = 2_500.0,
            SearchStarted = true,
            DatumLat = 12.444,
            DatumLon = -45.777,
            SearchAzimuth = 90,
            RouteIndex = 2,
        };

        session.AddPoint(new TrailPoint(10.0, 20.0, 12.4, -45.6, 1_000.0));
        session.AddPoint(new TrailPoint(30.0, 40.0, 12.5, -45.5, 2_000.0, BreakBefore: true));

        session.AddDeposit(new Deposit(Guid.NewGuid(), "Legacy-small vein", DepositSize.Pequeno, 1, 50.0, 60.0, 12.6, -45.4));
        session.AddDeposit(new Deposit(Guid.NewGuid(), "Medium vein", DepositSize.Medio, 2, 70.0, 80.0, 12.7, -45.3));
        session.AddDeposit(new Deposit(Guid.NewGuid(), "Large vein", DepositSize.Grande, 3, 90.0, 100.0, 12.8, -45.2));
        session.AddDeposit(new Deposit(Guid.NewGuid(), "Huge vein", DepositSize.Enorme, 4, 110.0, 120.0, 12.9, -45.1));

        session.AddRig(new Rig(Guid.NewGuid(), 15.0, 25.0, 12.45, -45.55));
        session.AddRig(new Rig(Guid.NewGuid(), -15.5, 26.5, 12.46, -45.56));
        session.AddMark(new MapMark(Guid.NewGuid(), "Datum", 5.0, 5.0, 12.35, -45.65));
        session.AddMark(new MapMark(Guid.NewGuid(), "North ridge", 6.0, 7.0, 12.36, -45.66));
        session.AddRouteHistory(new RouteHistoryEntry(1, 100.0, 200.0, RouteStatus.Reached));
        session.AddRouteHistory(new RouteHistoryEntry(2, 300.0, 400.0, RouteStatus.Skipped));
        session.AddRadarCoverage(new RadarCoverageDisc(1.0, 2.0, 500.0));
        session.AddRadarCoverage(new RadarCoverageDisc(3.0, 4.0, 750.0));

        return session;
    }

    [Fact]
    public async Task Saving_then_loading_a_fully_populated_session_round_trips_every_field()
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Wytheville", "map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = BuildGoldenSession(protectedFlag: true);
        var expectedDocument = original.ToDocument();

        await repository.SaveAsync(original, path, updateSavedAt: false);

        string writtenJson = await File.ReadAllTextAsync(path);
        writtenJson.Should().Contain("\"Small\"");
        writtenJson.Should().Contain("\"Medium\"");
        writtenJson.Should().Contain("\"Large\"");
        writtenJson.Should().Contain("\"Huge\"");

        var dto = JsonSerializer.Deserialize<MapDocumentDto>(writtenJson, MapJsonSerialization.Options)!;
        dto.Deposits[0].Size = "Pequeno";
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(dto, MapJsonSerialization.Options));

        var loaded = await repository.LoadAsync(path);

        loaded.ToDocument().Should().BeEquivalentTo(expectedDocument,
            "the golden-file round trip must reproduce every persisted field exactly, even when one deposit still uses a legacy Portuguese literal on disk");
    }

    [Fact]
    public async Task Saving_is_atomic_and_leaves_no_temporary_file_behind_on_a_failed_move()
    {
        var repository = CreateRepository();
        string directory = Path.Combine(AppPaths.MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "map.json");

        await repository.SaveAsync(BuildGoldenSession(), path, updateSavedAt: false);
        byte[] originalBytes = await File.ReadAllBytesAsync(path);

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var act = async () => await repository.SaveAsync(BuildGoldenSession(), path, updateSavedAt: false);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.EnumerateFiles(directory, "*.tmp").Should().BeEmpty("a failed atomic replacement must clean up its same-directory temporary file");
        (await File.ReadAllBytesAsync(path)).Should().Equal(originalBytes, "an interrupted write must leave the original file untouched");

        var updated = BuildGoldenSession();
        updated.Favorite = false;
        updated.ScannerRangeMetres = 1_750.0;

        await repository.SaveAsync(updated, path, updateSavedAt: false);

        Directory.EnumerateFiles(directory, "*.tmp").Should().BeEmpty("a successful retry must also leave no temporary file behind");
        var reloaded = await repository.LoadAsync(path);
        reloaded.Favorite.Should().BeFalse();
        reloaded.ScannerRangeMetres.Should().Be(1_750.0);
    }

    [Fact]
    public async Task SetFlagsAsync_restores_the_last_write_and_access_times_after_rewriting_the_file()
    {
        var repository = CreateRepository();
        string directory = Path.Combine(AppPaths.MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "map.json");
        await repository.SaveAsync(BuildGoldenSession(), path, updateSavedAt: false);

        var originalWriteTime = new DateTime(2020, 5, 1, 1, 2, 3, DateTimeKind.Utc);
        var originalAccessTime = new DateTime(2020, 5, 2, 4, 5, 6, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, originalWriteTime);
        File.SetLastAccessTimeUtc(path, originalAccessTime);

        await repository.SetFlagsAsync(path, favorite: true, protectedFlag: true);

        File.GetLastWriteTimeUtc(path).Should().Be(originalWriteTime);
        File.GetLastAccessTimeUtc(path).Should().Be(originalAccessTime);
        (await repository.IsProtectedAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_refuses_to_overwrite_a_file_marked_protected_on_disk()
    {
        var repository = CreateRepository();
        string directory = Path.Combine(AppPaths.MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "map.json");
        var session = BuildGoldenSession(protectedFlag: true);
        await repository.SaveAsync(session, path, updateSavedAt: false);

        var act = async () => await repository.SaveAsync(BuildGoldenSession(), path, updateSavedAt: false);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task SaveAsync_refuses_to_save_a_mining_only_session()
    {
        var repository = CreateRepository();
        string directory = Path.Combine(AppPaths.MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "map.json");
        var session = BuildGoldenSession();
        session.EnterMiningMode();

        var act = async () => await repository.SaveAsync(session, path, updateSavedAt: false);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task LoadAsync_defaults_a_missing_coverage_width_m_key_to_500_even_though_a_new_map_writes_2000()
    {
        var repository = CreateRepository();
        string directory = Path.Combine(AppPaths.MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "map.json");
        await File.WriteAllTextAsync(path, """
            {
              "system": "Wytheville",
              "body": "Alpha 1",
              "center_lat": 10.0,
              "center_lon": 20.0
            }
            """);

        var loaded = await repository.LoadAsync(path);

        loaded.CoverageWidthMetres.Should().Be(500.0);
    }

    [Fact]
    public async Task LoadAsync_exposes_whether_a_deposit_used_a_legacy_Portuguese_size_literal()
    {
        var repository = CreateRepository();
        string directory = Path.Combine(AppPaths.MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string legacyPath = Path.Combine(directory, "legacy.json");
        string englishPath = Path.Combine(directory, "english.json");

        await File.WriteAllTextAsync(legacyPath, """
            {
              "system": "Wytheville",
              "body": "Alpha 1",
              "center_lat": 10.0,
              "center_lon": 20.0,
              "deposits": [ { "x": 1.0, "y": 2.0, "lat": 10.1, "lon": 20.1, "name": "d", "size": "Pequeno", "rigs": 1 } ]
            }
            """);
        await File.WriteAllTextAsync(englishPath, """
            {
              "system": "Wytheville",
              "body": "Alpha 1",
              "center_lat": 10.0,
              "center_lon": 20.0,
              "deposits": [ { "x": 1.0, "y": 2.0, "lat": 10.1, "lon": 20.1, "name": "d", "size": "Small", "rigs": 1 } ]
            }
            """);

        var (_, legacyUsed) = await repository.LoadWithLegacyLiteralInfoAsync(legacyPath);
        var (_, englishUsed) = await repository.LoadWithLegacyLiteralInfoAsync(englishPath);

        legacyUsed.Should().BeTrue();
        englishUsed.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_reconstructs_a_legacy_markers_missing_lat_lon_from_its_local_xy()
    {
        var repository = CreateRepository();
        string directory = Path.Combine(AppPaths.MapsDirectory, "Wytheville");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "legacy-mark.json");
        const double centerLat = 10.0;
        const double centerLon = 20.0;
        const double x = 123.0;
        const double y = 456.0;
        await File.WriteAllTextAsync(path, $$"""
            {
              "system": "Wytheville",
              "body": "Alpha 1",
              "center_lat": {{centerLat}},
              "center_lon": {{centerLon}},
              "marks": [ { "x": {{x}}, "y": {{y}}, "name": "No coordinates" } ]
            }
            """);
        double expectedLat = centerLat + ((y / MapperConstants.DefaultRadiusMetres) * (180.0 / Math.PI));
        double expectedLon = centerLon + ((x / (MapperConstants.DefaultRadiusMetres * Math.Cos(centerLat * Math.PI / 180.0))) * (180.0 / Math.PI));

        var loaded = await repository.LoadAsync(path);

        loaded.Marks.Should().ContainSingle();
        loaded.Marks[0].Lat.Should().BeApproximately(expectedLat, 1e-12);
        loaded.Marks[0].Lon.Should().BeApproximately(expectedLon, 1e-12);
    }

    [Fact]
    public async Task LoadAsync_throws_FileNotFoundException_for_a_missing_file()
    {
        var repository = CreateRepository();

        var act = async () => await repository.LoadAsync(Path.Combine(_root, "does-not-exist.json"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadAsync_wraps_malformed_JSON_in_a_JsonException_with_the_file_path()
    {
        var repository = CreateRepository();
        string path = Path.Combine(_root, "malformed.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(path, "{ not json");

        var act = async () => await repository.LoadAsync(path);

        (await act.Should().ThrowAsync<JsonException>()).Which.Message.Should().Contain(path);
    }

    [Fact]
    public void EnumerateSystems_and_EnumerateMaps_reflect_a_constructed_directory_tree()
    {
        var repository = CreateRepository();
        string mapsDirectory = AppPaths.MapsDirectory;
        Directory.CreateDirectory(Path.Combine(mapsDirectory, "Sol"));
        Directory.CreateDirectory(Path.Combine(mapsDirectory, "Wytheville"));
        File.WriteAllText(Path.Combine(mapsDirectory, "Sol", "a.json"), "{}");
        File.WriteAllText(Path.Combine(mapsDirectory, "Sol", "b.json"), "{}");
        File.WriteAllText(Path.Combine(mapsDirectory, "Wytheville", "c.json"), "{}");

        var systems = repository.EnumerateSystems();
        var solMaps = repository.EnumerateMaps("Sol");

        systems.Should().Equal("Sol", "Wytheville");
        solMaps.Should().HaveCount(2);
        repository.EnumerateMaps("Unknown-System").Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsync_Should_PreserveOriginalBytesAndLeaveNoTemporaryFiles_When_AtomicReplacementFails()
    {
        var repository = CreateRepository();
        string directory = Path.Combine(AppPaths.MapsDirectory, "Atomic");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "map.json");

        await repository.SaveAsync(BuildGoldenSession(), path, updateSavedAt: false);
        byte[] originalBytes = await File.ReadAllBytesAsync(path);

        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var updated = BuildGoldenSession();
            updated.Body = "Updated body";

            var act = async () => await repository.SaveAsync(updated, path, updateSavedAt: false);

            await act.Should().ThrowAsync<UnauthorizedAccessException>();
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        (await File.ReadAllBytesAsync(path)).Should().Equal(originalBytes);
        Directory.EnumerateFiles(directory, "*.tmp").Should().BeEmpty();

        var replacement = BuildGoldenSession();
        replacement.Body = "Replacement body";
        await repository.SaveAsync(replacement, path, updateSavedAt: false);

        Directory.EnumerateFiles(directory, "*.tmp").Should().BeEmpty();
        (await repository.LoadAsync(path)).Body.Should().Be("Replacement body");
    }

    [Fact]
    public async Task SaveAsync_Should_WriteCoverageWidthOf2000_When_SavingANewMapWithDefaultSettings()
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Defaults", "new-map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var session = new MapSession
        {
            CenterLat = 10.0,
            CenterLon = 20.0,
        };

        await repository.SaveAsync(session, path, updateSavedAt: false);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        document.RootElement.GetProperty("coverage_width_m").GetDouble().Should().Be(2_000.0,
            "a brand-new map starts with the UI's 2000 m default even though legacy reads default a missing key to 500 m");
    }

    [Fact]
    public async Task SetFlagsAsync_Should_RestoreExactFileTimestamps_When_TogglingFavoriteAndProtectedFlags()
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Flags", "map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await repository.SaveAsync(BuildGoldenSession(), path, updateSavedAt: false);

        File.SetLastWriteTimeUtc(path, new DateTime(2024, 7, 8, 9, 10, 11, DateTimeKind.Utc).AddTicks(4_567));
        File.SetLastAccessTimeUtc(path, new DateTime(2024, 7, 8, 9, 10, 12, DateTimeKind.Utc).AddTicks(7_654));
        DateTime originalWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        DateTime originalAccessTimeUtc = File.GetLastAccessTimeUtc(path);

        await repository.SetFlagsAsync(path, favorite: false, protectedFlag: true);

        File.GetLastWriteTimeUtc(path).Should().Be(originalWriteTimeUtc);
        File.GetLastAccessTimeUtc(path).Should().Be(originalAccessTimeUtc);
    }

    [Fact]
    public async Task SaveAsync_Should_ThrowUnauthorizedAccessExceptionAndLeaveBytesUntouched_When_TargetFileIsProtected()
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Protected", "map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await repository.SaveAsync(BuildGoldenSession(protectedFlag: true), path, updateSavedAt: false);
        byte[] originalBytes = await File.ReadAllBytesAsync(path);

        var updated = BuildGoldenSession();
        updated.Body = "Should not persist";
        var act = async () => await repository.SaveAsync(updated, path, updateSavedAt: false);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await File.ReadAllBytesAsync(path)).Should().Equal(originalBytes);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"yes\"")]
    [InlineData("[1]")]
    public async Task IsProtectedAsync_Should_ReturnTrue_When_ProtectedFieldIsTruthyButNotABooleanTrue(string rawProtectedValue)
    {
        // Matches Python's `bool(data.get("protected", False))` truthy coercion: a hand-edited
        // or corrupted file can carry a non-boolean truthy value, and under-protecting a map in
        // that case is the worse failure mode, so it must still count as protected.
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Truthy", "map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $$"""{"protected": {{rawProtectedValue}}}""");

        (await repository.IsProtectedAsync(path)).Should().BeTrue();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"\"")]
    [InlineData("[]")]
    [InlineData("null")]
    public async Task IsProtectedAsync_Should_ReturnFalse_When_ProtectedFieldIsFalsyButNotABooleanFalse(string rawProtectedValue)
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Falsy", "map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, $$"""{"protected": {{rawProtectedValue}}}""");

        (await repository.IsProtectedAsync(path)).Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_Should_ThrowUnauthorizedAccessExceptionAndLeaveBytesUntouched_When_SessionIsMiningOnly()
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Mining", "map.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await repository.SaveAsync(BuildGoldenSession(), path, updateSavedAt: false);
        byte[] originalBytes = await File.ReadAllBytesAsync(path);

        var miningOnly = BuildGoldenSession();
        miningOnly.Body = "Should not persist";
        miningOnly.EnterMiningMode();

        var act = async () => await repository.SaveAsync(miningOnly, path, updateSavedAt: false);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        (await File.ReadAllBytesAsync(path)).Should().Equal(originalBytes);
    }

    [Fact]
    public async Task LoadWithLegacyLiteralInfoAsync_Should_ReportLegacyLiteralUsage_When_AnyDepositUsesALegacySize()
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Legacy", "mixed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {
              "system": "Wytheville",
              "body": "Alpha 1",
              "center_lat": 10.0,
              "center_lon": 20.0,
              "deposits": [
                { "x": 1.0, "y": 2.0, "lat": 10.1, "lon": 20.1, "name": "legacy", "size": "Pequeno", "rigs": 1 },
                { "x": 3.0, "y": 4.0, "lat": 10.2, "lon": 20.2, "name": "current", "size": "Large", "rigs": 2 }
              ]
            }
            """);

        var (_, usedLegacyLiterals) = await repository.LoadWithLegacyLiteralInfoAsync(path);

        usedLegacyLiterals.Should().BeTrue();
    }

    [Fact]
    public async Task LoadWithLegacyLiteralInfoAsync_Should_ReportNoLegacyLiteralUsage_When_AllDepositsUseCurrentEnglishSizes()
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Legacy", "english-only.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {
              "system": "Wytheville",
              "body": "Alpha 1",
              "center_lat": 10.0,
              "center_lon": 20.0,
              "deposits": [
                { "x": 1.0, "y": 2.0, "lat": 10.1, "lon": 20.1, "name": "one", "size": "Small", "rigs": 1 },
                { "x": 3.0, "y": 4.0, "lat": 10.2, "lon": 20.2, "name": "two", "size": "Huge", "rigs": 2 }
              ]
            }
            """);

        var (_, usedLegacyLiterals) = await repository.LoadWithLegacyLiteralInfoAsync(path);

        usedLegacyLiterals.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_Should_ReconstructLegacyMarkerCoordinates_When_LatitudeAndLongitudeAreMissing()
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "LegacyCoordinates", "rig.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const double centerLat = 41.125;
        const double centerLon = -7.75;
        const double x = 321.123;
        const double y = -654.987;
        await File.WriteAllTextAsync(path, $$"""
            {
              "system": "Gamma",
              "body": "Gamma 2",
              "center_lat": {{centerLat}},
              "center_lon": {{centerLon}},
              "rigs": [ { "x": {{x}}, "y": {{y}} } ]
            }
            """);

        double expectedLat = centerLat + ((y / MapperConstants.DefaultRadiusMetres) * (180.0 / Math.PI));
        double expectedLon = centerLon + ((x / (MapperConstants.DefaultRadiusMetres * Math.Cos(centerLat * Math.PI / 180.0))) * (180.0 / Math.PI));

        var loaded = await repository.LoadAsync(path);

        loaded.Rigs.Should().ContainSingle();
        loaded.Rigs[0].Lat.Should().BeApproximately(expectedLat, 1e-12);
        loaded.Rigs[0].Lon.Should().BeApproximately(expectedLon, 1e-12);
    }

    [Fact]
    public void EnumerateMapsAndSystems_Should_IgnoreIrrelevantFiles_When_ScanningAMultiSystemTree()
    {
        var repository = CreateRepository();
        string mapsDirectory = AppPaths.MapsDirectory;
        string alphaDirectory = Path.Combine(mapsDirectory, "Alpha");
        string betaDirectory = Path.Combine(mapsDirectory, "Beta");
        Directory.CreateDirectory(alphaDirectory);
        Directory.CreateDirectory(betaDirectory);
        Directory.CreateDirectory(Path.Combine(alphaDirectory, "notes"));
        File.WriteAllText(Path.Combine(mapsDirectory, "readme.txt"), "ignore");
        File.WriteAllText(Path.Combine(alphaDirectory, "alpha-1.json"), "{}");
        File.WriteAllText(Path.Combine(alphaDirectory, "alpha-2.json"), "{}");
        File.WriteAllText(Path.Combine(alphaDirectory, "alpha-2.bak"), "{}");
        File.WriteAllText(Path.Combine(betaDirectory, "beta-1.json"), "{}");
        File.WriteAllText(Path.Combine(betaDirectory, "beta-2.json"), "{}");
        File.WriteAllText(Path.Combine(betaDirectory, "notes.txt"), "ignore");

        var systems = repository.EnumerateSystems();
        var alphaMaps = repository.EnumerateMaps("Alpha");
        var betaMaps = repository.EnumerateMaps("Beta");

        systems.Should().Equal("Alpha", "Beta");
        alphaMaps.Should().Equal(
            Path.Combine(alphaDirectory, "alpha-1.json"),
            Path.Combine(alphaDirectory, "alpha-2.json"));
        betaMaps.Should().Equal(
            Path.Combine(betaDirectory, "beta-1.json"),
            Path.Combine(betaDirectory, "beta-2.json"));
    }

    [Fact]
    public async Task LoadAsync_Should_ApplyPythonCompatibleDefaults_When_OptionalKeysAreMissing()
    {
        var repository = CreateRepository();
        string path = Path.Combine(AppPaths.MapsDirectory, "Defaults", "missing-keys.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """
            {
              "center_lat": 10.0,
              "center_lon": 20.0
            }
            """);

        var loaded = await repository.LoadAsync(path);

        loaded.System.Should().BeEmpty();
        loaded.Body.Should().BeEmpty();
        loaded.CreatedAt.Should().BeNull();
        loaded.LastSavedAt.Should().BeNull();
        loaded.Favorite.Should().BeFalse();
        loaded.Protected.Should().BeFalse();
        loaded.PmlId.Should().BeEmpty();
        loaded.Radius.Should().Be(MapperConstants.DefaultRadiusMetres);
        loaded.CoverageWidthMetres.Should().Be(500.0);
        loaded.ScannerRangeMetres.Should().Be(2_000.0);
        loaded.SearchStarted.Should().BeFalse();
        loaded.SearchAzimuth.Should().Be(0);
        loaded.RouteIndex.Should().Be(0);
        loaded.Points.Should().NotBeNull().And.BeEmpty();
        loaded.Deposits.Should().NotBeNull().And.BeEmpty();
        loaded.Rigs.Should().NotBeNull().And.BeEmpty();
        loaded.Marks.Should().NotBeNull().And.BeEmpty();
        loaded.RouteHistory.Should().NotBeNull().And.BeEmpty();
        loaded.RadarCoverage.Should().NotBeNull().And.BeEmpty();
    }
}

/// <summary>
/// Covers the boxed-scalar JSON converter used by map DTO fields that must preserve exact runtime types.
/// </summary>
public sealed class RawValueJsonConverterTests
{
    [Fact]
    public void DeserializeAndSerialize_Should_PreserveSystemInt32_When_RawJsonContainsABareIntegerLiteral()
    {
        const string json = """{ "some_raw_field": 42 }""";
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        options.Converters.Add(new RawValueJsonConverter());

        var holder = JsonSerializer.Deserialize<RawValueHolder>(json, options);
        string roundTripped = JsonSerializer.Serialize(holder, options);

        holder.Should().NotBeNull();
        holder!.SomeRawField.Should().BeOfType<int>().Which.Should().Be(42);
        roundTripped.Should().Contain("\"some_raw_field\":42");
        roundTripped.Should().NotContain("42.0");
    }

    private sealed class RawValueHolder
    {
        [JsonPropertyName("some_raw_field")]
        public object? SomeRawField { get; set; }
    }
}
