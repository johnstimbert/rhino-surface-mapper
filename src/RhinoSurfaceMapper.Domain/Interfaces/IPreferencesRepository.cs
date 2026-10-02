namespace RhinoSurfaceMapper.Domain.Interfaces;

/// <summary>
/// Persists application preferences and supports importing/exporting them to an arbitrary file,
/// ported from the reference application's settings persistence. Phase 1 defines this contract
/// only; Phase 2 supplies the JSON (de)serialisation and filesystem implementation.
/// </summary>
/// <remarks>
/// Preferences are represented as a loosely-typed dictionary, matching the reference
/// application's settings store, which holds a heterogeneous and still-growing set of simple
/// values (flags, numbers, strings) rather than a fixed schema.
/// </remarks>
public interface IPreferencesRepository
{
    /// <summary>Loads the current application preferences.</summary>
    Task<IReadOnlyDictionary<string, object?>> LoadAsync(CancellationToken ct = default);

    /// <summary>Persists <paramref name="preferences"/> as the current application preferences.</summary>
    Task SaveAsync(IReadOnlyDictionary<string, object?> preferences, CancellationToken ct = default);

    /// <summary>Reads a previously exported preferences file at <paramref name="path"/>.</summary>
    Task<IReadOnlyDictionary<string, object?>> ReadExportAsync(string path, CancellationToken ct = default);

    /// <summary>Writes <paramref name="settings"/> to an export file at <paramref name="path"/>.</summary>
    Task WriteExportAsync(string path, IReadOnlyDictionary<string, object?> settings, CancellationToken ct = default);
}
