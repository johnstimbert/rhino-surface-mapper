namespace RhinoSurfaceMapper.Domain.Exceptions;

/// <summary>
/// An operation that mutates map content was attempted while the session is read-only, ported
/// from the <c>PermissionError</c> that <c>MapperState.save</c>/<c>start_search</c>/
/// <c>skip_next</c> raise or refuse under <c>mining_only</c>/<c>protected</c>.
/// </summary>
/// <remarks>
/// Covers only the in-memory, session-state reason for refusal (<c>MapSession.ReadOnly</c>).
/// The on-disk "this file is protected" check (<c>is_map_file_protected</c>) requires reading
/// the target file and is therefore enforced by the Phase 2 <c>IMapRepository</c> implementation,
/// not by this exception.
/// </remarks>
public sealed class MapSessionReadOnlyException : InvalidOperationException
{
    /// <summary>Creates a read-only refusal with a message naming the attempted operation.</summary>
    public MapSessionReadOnlyException(string message)
        : base(message)
    {
    }
}
