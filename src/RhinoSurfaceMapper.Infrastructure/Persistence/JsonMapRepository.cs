using System.Globalization;
using System.Text.Json;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Infrastructure.Persistence;

/// <summary>
/// <see cref="IMapRepository"/> implementation backed by portable JSON files under
/// <see cref="IAppPaths.MapsDirectory"/>, ported from <c>map_persistence.py</c>'s file
/// operations plus the timestamp/permission rules in <c>MapperState.save</c>.
/// </summary>
/// <remarks>
/// Reads and writes go through <see cref="MapDocumentDto"/>/<see cref="MapDocumentMapper"/> so
/// the wire format stays explicit and independent of <see cref="MapSession"/>; the actual
/// validation rules (ranges, required pairs, deposit size literals, …) remain entirely in
/// <c>Domain.Services.MapValidator</c>, reached through <see cref="MapSession.LoadFromDocument"/>.
/// This type performs only the I/O-specific decisions the design assigns to Infrastructure:
/// atomic writes, protected/read-only refusal, and mtime preservation on flag-only updates.
/// </remarks>
public sealed class JsonMapRepository : IMapRepository
{
    private readonly IAppPaths _appPaths;
    private readonly IClock _clock;

    /// <summary>Creates the repository with its path and clock dependencies.</summary>
    /// <param name="appPaths">Resolves <see cref="IAppPaths.MapsDirectory"/> for <see cref="EnumerateMaps"/>/<see cref="EnumerateSystems"/>.</param>
    /// <param name="clock">Supplies the current UTC time for <see cref="MapSession.PrepareForSave"/>; never read directly.</param>
    public JsonMapRepository(IAppPaths appPaths, IClock clock)
    {
        _appPaths = appPaths;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<MapSession> LoadAsync(string path, CancellationToken ct = default) =>
        (await LoadWithLegacyLiteralInfoAsync(path, ct).ConfigureAwait(false)).Session;

    /// <summary>
    /// Loads and validates the map document at <paramref name="path"/>, like
    /// <see cref="LoadAsync"/>, additionally reporting whether any deposit used a legacy
    /// Portuguese size literal (decision D7). Exposed only to
    /// <see cref="Migration.LegacyMapMigrationService"/> (via <c>internal</c> visibility plus
    /// <c>InternalsVisibleTo</c> for tests) — <see cref="IMapRepository"/> itself has no such
    /// member because the design froze that interface in Phase 1.
    /// </summary>
    /// <exception cref="FileNotFoundException"><paramref name="path"/> does not exist.</exception>
    /// <exception cref="JsonException">The file content is not valid JSON, or is missing a required field.</exception>
    /// <exception cref="Domain.Exceptions.MapValidationException">The document is structurally invalid or out of range.</exception>
    internal async Task<(MapSession Session, bool UsedLegacyLiterals)> LoadWithLegacyLiteralInfoAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Map file not found: '{path}'.", path);
        }

        MapDocumentDto? dto;
        try
        {
            await using var stream = File.OpenRead(path);
            dto = await JsonSerializer.DeserializeAsync<MapDocumentDto>(stream, MapJsonSerialization.Options, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // Wraps the underlying parser failure with the file path for diagnosability,
            // preserving the original cause per AGENTS.md's "preserve the actual cause" rule.
            throw new JsonException($"Map file '{path}' is not a valid map document.", ex);
        }

        if (dto is null)
        {
            throw new JsonException($"Map file '{path}' deserialized to an empty document.");
        }

        var rawDocument = MapDocumentMapper.ToRawDocument(dto, out bool usedLegacyLiteral);
        var session = new MapSession();
        session.LoadFromDocument(rawDocument);
        return (session, usedLegacyLiteral);
    }

    /// <inheritdoc />
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="session"/> is in mining-only mode, or the existing file at
    /// <paramref name="path"/> has <c>"protected": true</c> — the .NET analogue of Python's
    /// <c>PermissionError</c> in <c>MapperState.save</c>.
    /// </exception>
    public async Task SaveAsync(MapSession session, string path, bool updateSavedAt = true, CancellationToken ct = default)
    {
        if (session.MiningOnly)
        {
            throw new UnauthorizedAccessException(
                "Mining-only mode does not allow saving changes. Open a new version to explore.");
        }

        if (await IsProtectedAsync(path, ct).ConfigureAwait(false))
        {
            throw new UnauthorizedAccessException(
                "This map is protected. Create a new version to continue exploring.");
        }

        // Only maps that already carry metadata receive a new saved timestamp; a map saved for
        // the very first time does not invent a creation date here (see PrepareForSave's remarks).
        session.PrepareForSave(_clock, updateSavedAt);

        var dto = MapDocumentMapper.ToDto(session.ToDocument());
        await WriteAtomicAsync(path, dto, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Matches Python's <c>bool(data.get("protected", False))</c> truthy coercion exactly,
    /// rather than requiring a strict JSON <c>true</c>: a hand-edited/corrupted file with
    /// <c>"protected": 1</c> or <c>"protected": "yes"</c> must still read as protected in both
    /// apps, since both writers always emit a real boolean and the only way this distinction is
    /// ever observed is a malformed file — and under-protecting a map on malformed input is the
    /// worse failure mode of the two.
    /// </remarks>
    public async Task<bool> IsProtectedAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        return document.RootElement.TryGetProperty("protected", out var protectedElement)
            && IsTruthy(protectedElement);
    }

    /// <summary>
    /// Reproduces Python's <see langword="bool"/>() truthiness rules for a parsed JSON value:
    /// <see langword="null"/>, <c>false</c>, zero, and an empty string/array/object are falsy;
    /// everything else (including a non-zero number or a non-empty string) is truthy.
    /// </summary>
    private static bool IsTruthy(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString() is { Length: > 0 },
        JsonValueKind.Number => element.GetDouble() != 0,
        JsonValueKind.Array => element.GetArrayLength() > 0,
        JsonValueKind.Object => element.EnumerateObject().Any(),
        _ => false,
    };

    /// <inheritdoc />
    public async Task SetFlagsAsync(string path, bool favorite, bool protectedFlag, CancellationToken ct = default)
    {
        // Captured before the rewrite so the Map Library's modification-time sort is unaffected
        // by a flag-only management change — ported from Python's `os.utime(..., ns=(atime, mtime))`.
        DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        DateTime lastAccessTimeUtc = File.GetLastAccessTimeUtc(path);

        MapDocumentDto dto;
        await using (var readStream = File.OpenRead(path))
        {
            dto = await JsonSerializer.DeserializeAsync<MapDocumentDto>(readStream, MapJsonSerialization.Options, ct).ConfigureAwait(false)
                ?? throw new JsonException($"Map file '{path}' deserialized to an empty document.");
        }

        dto.Favorite = favorite;
        dto.Protected = protectedFlag;

        await WriteAtomicAsync(path, dto, ct).ConfigureAwait(false);

        File.SetLastWriteTimeUtc(path, lastWriteTimeUtc);
        File.SetLastAccessTimeUtc(path, lastAccessTimeUtc);
    }

    /// <inheritdoc />
    public Task<(string CreatedAt, string LastSavedAt)> ReadTimestampsAsync(string path, CancellationToken ct = default)
    {
        // Local time with an explicit UTC offset reproduces Python's
        // `datetime.fromtimestamp(...).astimezone().isoformat(timespec='seconds')`, which is
        // timezone-aware but not a literal "Z" suffix (unlike MapSession.PrepareForSave's
        // always-UTC `LastSavedAt` stamp).
        var createdAt = new DateTimeOffset(File.GetCreationTime(path));
        var lastSavedAt = new DateTimeOffset(File.GetLastWriteTime(path));
        const string format = "yyyy-MM-dd'T'HH:mm:sszzz";
        return Task.FromResult((
            createdAt.ToString(format, CultureInfo.InvariantCulture),
            lastSavedAt.ToString(format, CultureInfo.InvariantCulture)));
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateMaps(string systemName)
    {
        string directory = Path.Combine(_appPaths.MapsDirectory, systemName);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    /// <inheritdoc />
    public IReadOnlyList<string> EnumerateSystems()
    {
        string root = _appPaths.MapsDirectory;
        if (!Directory.Exists(root))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// Atomically writes <paramref name="dto"/> as UTF-8 JSON to <paramref name="path"/>: a
    /// temporary file is created in the destination directory, then <see cref="File.Move(string, string, bool)"/>
    /// swaps it into place, exactly as <c>map_persistence.write_map_json</c>'s
    /// <c>tempfile.NamedTemporaryFile</c> + <c>os.replace</c> pair does. The temporary file is
    /// removed in <see langword="finally"/>, tolerating the case where the move already
    /// succeeded (so there is nothing left to delete).
    /// </summary>
    private static async Task WriteAtomicAsync(string path, MapDocumentDto dto, CancellationToken ct)
    {
        string directory = Path.GetDirectoryName(path) is { Length: > 0 } parent
            ? parent
            : throw new IOException($"Cannot determine a directory for map path '{path}'.");
        Directory.CreateDirectory(directory);

        string temporaryPath = Path.Combine(directory, $"{Path.GetRandomFileName()}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write))
            {
                await JsonSerializer.SerializeAsync(stream, dto, MapJsonSerialization.Options, ct).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Best-effort cleanup only — matches Python's `temporary.unlink()` call,
                    // which likewise tolerates a concurrent deletion racing this one.
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
