using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Desktop.Hosting;

/// <summary>
/// Throttled "is Elite Dangerous running" check, ported from the exact-name match in
/// <c>elite_dangerous/status.py</c>'s <c>elite_dangerous_is_running</c>
/// (<c>EliteDangerous64.exe</c>, case-insensitive).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately placed here, not in <c>RhinoSurfaceMapper.Platform.Windows</c>.</b> The
/// design's "Application services and interfaces" table assigns the fuller
/// <c>IGameProcessService</c> (which also covers foreground-window focus, needed only once
/// input injection/steering exists) to <c>Platform.Windows</c>. This phase (read-only map
/// display) needs only the cheap "is the process running at all" signal, via the
/// cross-platform-compiling <see cref="System.Diagnostics.Process.GetProcessesByName(string)"/>
/// API rather than the Tool-Help/<c>CreateToolhelp32Snapshot</c> P/Invoke surface the Python
/// original and the design's <c>Platform.Windows</c> table use. Introducing the
/// focus-detection, P/Invoke-based implementation now, before any feature actually needs
/// window-focus, would be scope creep for this phase — flagged explicitly here (and in the
/// phase summary) so a later phase (8: steering assistance) can retire this type in favour of
/// <c>Platform.Windows</c>'s <c>IGameProcessService</c> without surprise.
/// </para>
/// <para>
/// The 1-second throttle matches the design's "the game-process check is throttled to once per
/// second, as today" statement.
/// </para>
/// </remarks>
public interface IGameProcessCheck
{
    /// <summary>
    /// Returns whether the Elite Dangerous game process is currently running, re-checking the
    /// process table at most once per second and returning the cached result otherwise.
    /// </summary>
    bool IsRunning();
}

/// <inheritdoc cref="IGameProcessCheck"/>
public sealed class EliteDangerousProcessCheck : IGameProcessCheck
{
    /// <summary>The exact executable basename matched, case-insensitively, identically to the Python original.</summary>
    private const string ProcessName = "EliteDangerous64";

    private readonly IClock _clock;
    private readonly object _gate = new();

    private double _nextCheckAtMonotonicSeconds;
    private bool _cachedResult;

    /// <summary>Creates the check, using <paramref name="clock"/> for the throttle window so it is deterministically testable.</summary>
    public EliteDangerousProcessCheck(IClock clock)
    {
        _clock = clock;
    }

    /// <inheritdoc />
    public bool IsRunning()
    {
        lock (_gate)
        {
            double now = _clock.MonotonicSeconds;
            if (now < _nextCheckAtMonotonicSeconds)
            {
                return _cachedResult;
            }

            _nextCheckAtMonotonicSeconds = now + 1.0;
            _cachedResult = IsProcessRunning();
            return _cachedResult;
        }
    }

    /// <summary>
    /// Queries the live process table. <see cref="System.Diagnostics.Process"/> instances
    /// returned by <see cref="System.Diagnostics.Process.GetProcessesByName(string)"/> hold an
    /// OS handle each and must be disposed promptly; this method disposes every entry before
    /// returning, matching the "query, don't leak" contract
    /// <c>CreateToolhelp32Snapshot</c>/<c>CloseHandle</c> enforces explicitly in the Python
    /// original.
    /// </summary>
    private static bool IsProcessRunning()
    {
        var processes = System.Diagnostics.Process.GetProcessesByName(ProcessName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
