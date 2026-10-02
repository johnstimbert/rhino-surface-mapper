using RhinoSurfaceMapper.Application.Interfaces;

namespace RhinoSurfaceMapper.Application.Services;

/// <summary>
/// <see cref="IMapSessionNotifier"/> implementation: a plain, synchronous event aggregator.
/// Each <c>Notify*</c> method raises its matching event directly on the calling thread — the
/// hosted service (or, in tests, the caller) that detects the discrete change. Subscribers that
/// need to marshal onto a UI thread (Blazor's renderer synchronization context, in
/// <c>UI.Components</c>) are responsible for doing so themselves; this type has no WPF or
/// Blazor dependency, keeping it usable from <c>Application</c>-layer tests without either.
/// </summary>
public sealed class MapSessionNotifier : IMapSessionNotifier
{
    /// <inheritdoc />
    public event EventHandler? SessionChanged;

    /// <inheritdoc />
    public event EventHandler? TelemetryUpdated;

    /// <inheritdoc />
    public event EventHandler? RadarChanged;

    /// <inheritdoc />
    public event EventHandler? NavigationChanged;

    /// <inheritdoc />
    public void NotifySessionChanged() => SessionChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void NotifyTelemetryUpdated() => TelemetryUpdated?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void NotifyRadarChanged() => RadarChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public void NotifyNavigationChanged() => NavigationChanged?.Invoke(this, EventArgs.Empty);
}
