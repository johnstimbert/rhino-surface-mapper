using System.Text.Json;
using System.Text.Json.Serialization;
using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Infrastructure.Persistence;

/// <summary>
/// <see cref="IPreferencesRepository"/> implementation backed by <c>options.json</c> (internal
/// preferences) and an arbitrary versioned export file, ported from
/// <c>settings_persistence.py</c>.
/// </summary>
/// <remarks>
/// Internal preferences are tolerant by design — a missing or malformed <c>options.json</c>
/// degrades to an empty dictionary rather than blocking startup — while an explicit settings
/// export/import is strict, enforcing the <c>{"rhino_settings_version": 1, "settings": {...}}</c>
/// envelope. Both documents share <see cref="MapJsonSerialization.Options"/> so their scalar
/// values round-trip through <see cref="RawValueJsonConverter"/> exactly like a map document's
/// boxed fields do.
/// </remarks>
public sealed class JsonPreferencesRepository : IPreferencesRepository
{
    private const int SupportedSettingsVersion = 1;
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private readonly IAppPaths _appPaths;

    /// <summary>Creates the repository, resolving <c>options.json</c> from <see cref="IAppPaths.OptionsPath"/>.</summary>
    public JsonPreferencesRepository(IAppPaths appPaths)
    {
        _appPaths = appPaths;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never throws: a missing file, invalid JSON, a JSON document that is not an object, or a
    /// permission-denied/locked file all resolve to an empty dictionary, matching
    /// <c>load_preferences</c>'s tolerant contract (Python's bare <c>except OSError</c>, whose
    /// subclasses include <c>PermissionError</c>) so the application can always start and
    /// recreate <c>options.json</c> with defaults.
    /// </remarks>
    public async Task<IReadOnlyDictionary<string, object?>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_appPaths.OptionsPath))
        {
            return new Dictionary<string, object?>();
        }

        try
        {
            using var document = await ParseFileAsync(_appPaths.OptionsPath, ct).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? ToDictionary(document.RootElement)
                : new Dictionary<string, object?>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
        catch (IOException)
        {
            return new Dictionary<string, object?>();
        }
        catch (UnauthorizedAccessException)
        {
            // Mirrors Python's bare `except OSError` in `load_preferences`: `PermissionError`
            // is an `OSError` subclass there, so a locked/ACL-denied options.json degrades to
            // an empty dictionary in Python too. .NET's UnauthorizedAccessException does not
            // derive from IOException, so it needs its own catch clause here — this is
            // deliberately narrower than ReadExportAsync, which stays strict.
            return new Dictionary<string, object?>();
        }
    }

    /// <inheritdoc />
    public async Task SaveAsync(IReadOnlyDictionary<string, object?> preferences, CancellationToken ct = default)
    {
        await using var stream = new FileStream(_appPaths.OptionsPath, FileMode.Create, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, preferences, MapJsonSerialization.Options, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
    /// <exception cref="JsonException">The file content is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">
    /// The document is not a version-1 settings export, or its <c>settings</c> object is absent
    /// or empty — the .NET analogue of Python's <c>ValueError("Formato de configurações
    /// inválido.")</c> / <c>ValueError("O ficheiro não contém configurações.")</c>.
    /// </exception>
    public async Task<IReadOnlyDictionary<string, object?>> ReadExportAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Settings export file not found: '{path}'.", path);
        }

        using var document = await ParseFileAsync(path, ct).ConfigureAwait(false);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("rhino_settings_version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out int version)
            || version != SupportedSettingsVersion)
        {
            throw new InvalidDataException("Invalid settings export format.");
        }

        if (!root.TryGetProperty("settings", out var settingsElement) || settingsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The file does not contain any settings.");
        }

        var settings = ToDictionary(settingsElement);
        if (settings.Count == 0)
        {
            throw new InvalidDataException("The file does not contain any settings.");
        }

        return settings;
    }

    /// <inheritdoc />
    public async Task WriteExportAsync(string path, IReadOnlyDictionary<string, object?> settings, CancellationToken ct = default)
    {
        // A strongly-typed envelope (not a nested object-typed dictionary) so the "settings"
        // property serializes through the built-in dictionary converter rather than being
        // mistaken for one more scalar value by RawValueJsonConverter, which only ever expects
        // to see a JSON scalar on its Write side.
        var envelope = new SettingsExportDto
        {
            RhinoSettingsVersion = SupportedSettingsVersion,
            Settings = new Dictionary<string, object?>(settings),
        };

        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        await JsonSerializer.SerializeAsync(stream, envelope, MapJsonSerialization.Options, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads <paramref name="path"/> fully and parses it as JSON, stripping a leading UTF-8 byte
    /// order mark first — the .NET analogue of Python's <c>encoding='utf-8-sig'</c> tolerance,
    /// applied explicitly rather than relied upon implicitly since
    /// <see cref="JsonDocument.Parse(ReadOnlyMemory{byte}, JsonDocumentOptions)"/> does not skip
    /// a BOM on its own.
    /// </summary>
    private static async Task<JsonDocument> ParseFileAsync(string path, CancellationToken ct)
    {
        ReadOnlyMemory<byte> bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        if (bytes.Length >= Utf8Bom.Length && bytes.Span[..Utf8Bom.Length].SequenceEqual(Utf8Bom))
        {
            bytes = bytes[Utf8Bom.Length..];
        }

        return JsonDocument.Parse(bytes);
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement objectElement)
    {
        var result = new Dictionary<string, object?>();
        foreach (var property in objectElement.EnumerateObject())
        {
            result[property.Name] = RawValueJsonConverter.ToRawValue(property.Value);
        }

        return result;
    }

    /// <summary>
    /// The <c>{"rhino_settings_version": 1, "settings": {...}}</c> export envelope, typed
    /// explicitly (rather than as a nested <see cref="Dictionary{TKey, TValue}"/> of
    /// <see cref="object"/>) so <see cref="Settings"/> serializes through the built-in
    /// dictionary converter and only its individual values go through
    /// <see cref="RawValueJsonConverter"/>.
    /// </summary>
    private sealed class SettingsExportDto
    {
        /// <summary>The settings format version; only <c>1</c> is currently supported.</summary>
        [JsonPropertyName("rhino_settings_version")]
        public int RhinoSettingsVersion { get; set; }

        /// <summary>The exported flat settings values.</summary>
        [JsonPropertyName("settings")]
        public Dictionary<string, object?> Settings { get; set; } = [];
    }
}
