using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FluentAssertions;
using RhinoSurfaceMapper.Infrastructure.Paths;
using RhinoSurfaceMapper.Infrastructure.Persistence;

namespace RhinoSurfaceMapper.Infrastructure.Tests.Persistence;

/// <summary>
/// Covers <see cref="JsonPreferencesRepository"/> against <c>python/tests/test_settings_persistence.py</c>'s
/// acceptance spec: <c>options.json</c> loading never throws, and the versioned export/import
/// envelope is strictly validated while tolerating a UTF-8 BOM on import.
/// </summary>
public sealed class JsonPreferencesRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RhinoSurfaceMapperTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private JsonPreferencesRepository CreateRepository() => new(new AppPaths(_root));

    [Fact]
    public async Task LoadAsync_returns_an_empty_dictionary_when_options_json_does_not_exist()
    {
        var repository = CreateRepository();

        var preferences = await repository.LoadAsync();

        preferences.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_returns_an_empty_dictionary_for_malformed_JSON_instead_of_throwing()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "options.json"), "{ not json");

        var preferences = await repository.LoadAsync();

        preferences.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_returns_an_empty_dictionary_when_the_document_is_not_a_JSON_object()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "options.json"), "[1, 2, 3]");

        var preferences = await repository.LoadAsync();

        preferences.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_Should_ReturnAnEmptyDictionary_When_TheDocumentIsAJsonStringInsteadOfAnObject()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "options.json"), "\"dark\"");

        var preferences = await repository.LoadAsync();

        preferences.Should().BeEmpty();
    }

    // ACL manipulation is Windows-only; this test suite only ever runs on Windows (see the
    // solution's TargetFramework/host). Scoped narrowly to this one test rather than suppressed
    // project-wide.
#pragma warning disable CA1416
    [Fact]
    public async Task LoadAsync_returns_an_empty_dictionary_when_options_json_is_permission_denied()
    {
        // Python's `load_preferences` catches a bare `OSError`, whose subclasses include
        // `PermissionError` — a permission-denied/locked options.json degrades to {} there too.
        // UnauthorizedAccessException does not derive from IOException in .NET, so this test
        // exercises a dedicated catch clause distinct from the malformed-JSON/IOException cases
        // covered above. A deny ACE (rather than a shared file lock, which raises IOException
        // instead) is the only deterministic way to provoke UnauthorizedAccessException here.
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "options.json");
        await File.WriteAllTextAsync(path, "{}");

        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var denyRule = new FileSystemAccessRule(currentUser, FileSystemRights.Read, AccessControlType.Deny);
        security.AddAccessRule(denyRule);
        fileInfo.SetAccessControl(security);

        try
        {
            var repository = CreateRepository();

            var preferences = await repository.LoadAsync();

            preferences.Should().BeEmpty();
        }
        finally
        {
            // Must remove the deny rule before the fixture's Dispose() can delete the directory.
            security.RemoveAccessRule(denyRule);
            fileInfo.SetAccessControl(security);
        }
    }
#pragma warning restore CA1416

    [Fact]
    public async Task SaveAsync_then_LoadAsync_round_trips_a_flat_preferences_dictionary()
    {
        Directory.CreateDirectory(_root);
        var repository = CreateRepository();
        var preferences = new Dictionary<string, object?>
        {
            ["theme"] = "dark",
            ["volume"] = 7,
            ["muted"] = false,
            ["scale"] = 1.5,
            ["label"] = null,
        };

        await repository.SaveAsync(preferences);
        var loaded = await repository.LoadAsync();

        loaded.Should().BeEquivalentTo(preferences);
    }

    [Fact]
    public async Task WriteExportAsync_then_ReadExportAsync_round_trips_the_settings_envelope()
    {
        var repository = CreateRepository();
        string path = Path.Combine(_root, "export.json");
        Directory.CreateDirectory(_root);
        var settings = new Dictionary<string, object?> { ["theme"] = "dark", ["volume"] = 7 };

        await repository.WriteExportAsync(path, settings);
        var imported = await repository.ReadExportAsync(path);

        imported.Should().BeEquivalentTo(settings);
    }

    [Fact]
    public async Task ReadExportAsync_throws_FileNotFoundException_for_a_missing_file()
    {
        var repository = CreateRepository();

        var act = async () => await repository.ReadExportAsync(Path.Combine(_root, "missing.json"));

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task ReadExportAsync_rejects_a_settings_version_other_than_1()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "export.json");
        await File.WriteAllTextAsync(path, """{ "rhino_settings_version": 2, "settings": { "a": 1 } }""");

        var act = async () => await repository.ReadExportAsync(path);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadExportAsync_Should_ThrowInvalidDataException_When_TheVersionMarkerIsMissing()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "export.json");
        await File.WriteAllTextAsync(path, """{ "settings": { "a": 1 } }""");

        var act = async () => await repository.ReadExportAsync(path);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadExportAsync_rejects_an_empty_settings_object()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "export.json");
        await File.WriteAllTextAsync(path, """{ "rhino_settings_version": 1, "settings": {} }""");

        var act = async () => await repository.ReadExportAsync(path);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadExportAsync_rejects_a_document_whose_settings_value_is_not_an_object()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "export.json");
        await File.WriteAllTextAsync(path, """{ "rhino_settings_version": 1, "settings": [] }""");

        var act = async () => await repository.ReadExportAsync(path);

        await act.Should().ThrowAsync<InvalidDataException>();
    }

    [Fact]
    public async Task ReadExportAsync_tolerates_a_leading_UTF8_byte_order_mark()
    {
        var repository = CreateRepository();
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "export.json");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] content = Encoding.UTF8.GetBytes("""{ "rhino_settings_version": 1, "settings": { "a": 1 } }""");
        await File.WriteAllBytesAsync(path, [.. bom, .. content]);

        var imported = await repository.ReadExportAsync(path);

        imported.Should().ContainKey("a").WhoseValue.Should().Be(1);
    }
}
