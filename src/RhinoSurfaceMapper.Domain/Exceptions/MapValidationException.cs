namespace RhinoSurfaceMapper.Domain.Exceptions;

/// <summary>
/// A persisted map document failed <c>MapValidator</c>'s structural, range or schema rules.
/// Ported from the mix of <c>ValueError</c>/<c>TypeError</c>/<c>KeyError</c> exceptions
/// <c>MapperState.validate_map</c> raises; the port uses one specific exception type instead so
/// callers (the Phase 2 repository) have a single, predictable failure mode to catch, per
/// AGENTS.md's "robust exception handling" rule against ambiguous generic exceptions.
/// </summary>
/// <remarks>
/// A failed validation must leave the previously loaded <see cref="Entities.MapSession"/>
/// untouched. Callers must validate into a candidate (<c>MapValidator.Validate</c>,
/// <c>MapSession.LoadFromDocument</c>) and only swap state in on success, matching the Python
/// "load into a candidate, validate, then swap" pattern.
/// </remarks>
public sealed class MapValidationException : Exception
{
    /// <summary>Creates a validation failure with a message describing the violated rule.</summary>
    public MapValidationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a validation failure wrapping the numeric/format coercion error that caused it,
    /// preserving the original cause per AGENTS.md's "preserve the actual cause" rule.
    /// </summary>
    public MapValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
