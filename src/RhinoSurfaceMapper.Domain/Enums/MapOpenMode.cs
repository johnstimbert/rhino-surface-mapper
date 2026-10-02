namespace RhinoSurfaceMapper.Domain.Enums;

/// <summary>
/// Mode under which an existing map file is reopened, ported from the protected-map choice
/// dialog (<c>choose_protected_map_mode</c> returning <c>'explore'</c> / <c>'mining'</c>) in
/// <c>rhino_surface_mapper_qt.py</c>. Not yet exercised by Phase 1 logic — the read/write gate
/// Phase 1 implements is <c>MapSession.ReadOnly</c> (<c>Protected || MiningOnly</c>); this enum
/// is defined now so the Phase 4 open-flow design does not need a new Domain type later.
/// </summary>
public enum MapOpenMode
{
    /// <summary>The map opens read/write: new trail, deposits, rigs and marks may be recorded.</summary>
    Editable,

    /// <summary>
    /// The map opens read-only for exploration purposes (<c>MapSession.MiningOnly</c>): existing
    /// records remain visible and navigable, but no new exploration data is recorded.
    /// </summary>
    MiningOnly,
}
