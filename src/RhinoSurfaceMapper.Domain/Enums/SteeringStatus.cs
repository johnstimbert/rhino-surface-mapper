namespace RhinoSurfaceMapper.Domain.Enums;

/// <summary>
/// Steering-assist state machine phase, ported from the status values
/// <c>SteeringAssist</c> reports in <c>steering.py</c>. Not yet exercised by Phase 1 logic
/// (steering assistance is Phase 8); defined now so <c>SteeringDecisionEngine</c> does not need
/// a new Domain enum when that phase begins.
/// </summary>
public enum SteeringStatus
{
    /// <summary>No manoeuvre is in progress; the assist is idle.</summary>
    Stopped,

    /// <summary>The assist is waiting out a cooldown or staleness window before acting again.</summary>
    Waiting,

    /// <summary>The assist is braking because the target is within arrival range.</summary>
    Slowing,

    /// <summary>The assist is issuing a steering pulse to reduce heading error.</summary>
    Correcting,

    /// <summary>Heading error is within tolerance; no pulse is needed.</summary>
    OnCourse,

    /// <summary>The assist is releasing an in-progress pulse early because conditions changed.</summary>
    Easing,
}
