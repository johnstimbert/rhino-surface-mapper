using RhinoSurfaceMapper.Domain.Entities;

namespace RhinoSurfaceMapper.Domain.Interfaces;

/// <summary>
/// Persists and discovers map documents on disk, ported from <c>map_persistence.py</c>'s file
/// operations. Phase 1 defines this contract only; Phase 2 supplies the JSON (de)serialisation
/// and filesystem implementation using <see cref="Services.MapValidator"/> and
/// <see cref="Services.PmlRules"/> for the pure logic.
/// </summary>
public interface IMapRepository
{
    /// <summary>Loads and validates the map document at <paramref name="path"/>.</summary>
    Task<MapSession> LoadAsync(string path, CancellationToken ct = default);

    /// <summary>Persists <paramref name="session"/> to <paramref name="path"/>, optionally refreshing its last-saved timestamp.</summary>
    Task SaveAsync(MapSession session, string path, bool updateSavedAt = true, CancellationToken ct = default);

    /// <summary>Returns whether the file at <paramref name="path"/> is marked read-only/protected on disk.</summary>
    Task<bool> IsProtectedAsync(string path, CancellationToken ct = default);

    /// <summary>Updates the persisted favourite/protected flags for the file at <paramref name="path"/> without a full load/save round-trip.</summary>
    Task SetFlagsAsync(string path, bool favorite, bool protectedFlag, CancellationToken ct = default);

    /// <summary>Reads only the created-at/last-saved-at timestamps from the file at <paramref name="path"/>, for timestamp migration.</summary>
    Task<(string CreatedAt, string LastSavedAt)> ReadTimestampsAsync(string path, CancellationToken ct = default);

    /// <summary>Lists the map file paths stored for the given star system.</summary>
    IReadOnlyList<string> EnumerateMaps(string systemName);

    /// <summary>Lists the star system names that have at least one stored map.</summary>
    IReadOnlyList<string> EnumerateSystems();
}
