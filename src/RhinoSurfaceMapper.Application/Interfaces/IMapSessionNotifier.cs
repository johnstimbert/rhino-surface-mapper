namespace RhinoSurfaceMapper.Application.Interfaces;

/// <summary>
/// Publishes coarse, discrete notifications about the live <see cref="Domain.Entities.MapSession"/>
/// so UI and overlay consumers can refresh presentation state without polling, per the design's
/// "Rendering cadence" section: continuous geometry (trail points, telemetry positions) is read
/// directly from <see cref="IMapSessionStore.Snapshot"/> on a timer, while these events exist
/// only for discrete, infrequent state changes (a new map, a body change, a radar/navigation
/// mode toggle) that justify an immediate <c>StateHasChanged</c> rather than waiting for the
/// next polling tick.
/// </summary>
/// <remarks>
/// All four events are declared now, matching the design's full "Application services and
/// interfaces" table entry, even though Phase 3 (read-only map display) only raises
/// <see cref="SessionChanged"/> and <see cref="TelemetryUpdated"/>. <see cref="RadarChanged"/>
/// and <see cref="NavigationChanged"/> are reserved for the radar (Phase 8) and navigation
/// (Phase 7) hosted services, so earlier UI consumers can subscribe to the full contract once
/// and never need a breaking interface change later.
/// </remarks>
public interface IMapSessionNotifier
{
    /// <summary>Raised after a mutation that changed <see cref="Domain.Entities.MapSession.MapGeneration"/> (a new map or a body change).</summary>
    event EventHandler? SessionChanged;

    /// <summary>Raised after every accepted telemetry sample is applied to the session.</summary>
    event EventHandler? TelemetryUpdated;

    /// <summary>Raised after radar coverage or pulse state changes (Phase 8; reserved).</summary>
    event EventHandler? RadarChanged;

    /// <summary>Raised after navigation target or search-route state changes (Phase 7; reserved).</summary>
    event EventHandler? NavigationChanged;

    /// <summary>Raises <see cref="SessionChanged"/>.</summary>
    void NotifySessionChanged();

    /// <summary>Raises <see cref="TelemetryUpdated"/>.</summary>
    void NotifyTelemetryUpdated();

    /// <summary>Raises <see cref="RadarChanged"/>.</summary>
    void NotifyRadarChanged();

    /// <summary>Raises <see cref="NavigationChanged"/>.</summary>
    void NotifyNavigationChanged();
}
