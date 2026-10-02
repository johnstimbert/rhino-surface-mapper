using RhinoSurfaceMapper.Application.Interfaces;

namespace RhinoSurfaceMapper.UI.Components.Map;

/// <summary>
/// Pushes projected <see cref="MapScene"/> scenes to a sink (the canvas' JS interop call) at a
/// fixed cadence, and raises <see cref="DiscreteStateChanged"/> only for the coarse,
/// infrequent <see cref="IMapSessionNotifier"/> events — exactly the split the design's
/// "Rendering cadence" section describes: "<c>StateHasChanged</c> is only invoked for discrete
/// state changes... continuous geometry goes straight to the canvas module".
/// </summary>
/// <remarks>
/// Deliberately has no WPF/Blazor/JS-interop dependency itself: the continuous push is an
/// injected delegate (<see cref="MapScenePush"/>), and <see cref="DiscreteStateChanged"/> is a
/// plain <see cref="Action"/> event — both wired up by <c>MapCanvas</c>'s code-behind, which is
/// the only piece of this feature that actually needs <c>ComponentBase.InvokeAsync</c>/JS
/// interop. This keeps the coalescing logic itself unit-testable without bUnit or a browser.
/// </remarks>
public sealed class MapScenePresenter : IAsyncDisposable
{
    private readonly IMapSessionStore _store;
    private readonly IMapSessionNotifier _notifier;
    private readonly MapScenePush _push;
    private readonly TimeSpan _interval;
    private readonly EventHandler _onSessionChanged;
    private readonly EventHandler _onTelemetryUpdated;

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>
    /// Creates a presenter driving <paramref name="push"/> at <paramref name="interval"/> (the
    /// design's ~20 Hz / 50 ms map cadence by default) and forwarding
    /// <see cref="IMapSessionNotifier.SessionChanged"/>/<see cref="IMapSessionNotifier.TelemetryUpdated"/>
    /// as <see cref="DiscreteStateChanged"/>.
    /// </summary>
    /// <param name="store">Source of the latest <see cref="MapSessionSnapshot"/> on every tick.</param>
    /// <param name="notifier">Source of discrete session-change notifications.</param>
    /// <param name="push">Callback invoked with the freshly projected scene on every tick.</param>
    /// <param name="interval">Tick cadence; defaults to 50 ms (~20 Hz) per the design.</param>
    public MapScenePresenter(IMapSessionStore store, IMapSessionNotifier notifier, MapScenePush push, TimeSpan? interval = null)
    {
        _store = store;
        _notifier = notifier;
        _push = push;
        _interval = interval ?? TimeSpan.FromMilliseconds(50);

        // Stored so Dispose can unsubscribe the exact same delegate instances; a lambda written
        // inline at subscribe time cannot be unsubscribed later.
        _onSessionChanged = (_, _) => DiscreteStateChanged?.Invoke();
        _onTelemetryUpdated = (_, _) => DiscreteStateChanged?.Invoke();

        _notifier.SessionChanged += _onSessionChanged;

        // TelemetryUpdated also triggers a discrete refresh: the first sample after a session
        // has no map yet (MapScene.Empty) still needs one StateHasChanged to show "waiting for
        // telemetry" style UI transitioning to "live", even though per-sample geometry itself is
        // handled by the timer below, not by this event.
        _notifier.TelemetryUpdated += _onTelemetryUpdated;
    }

    /// <summary>
    /// Raised for a discrete, infrequent state change a UI component should react to with its
    /// own <c>StateHasChanged</c> (or equivalent) — never raised per telemetry sample at the
    /// 50 ms/16 ms hot-loop rate.
    /// </summary>
    public event Action? DiscreteStateChanged;

    /// <summary>
    /// Starts the periodic push loop. Safe to call only once per instance; a second call
    /// replaces the previous loop's cancellation source without stopping the earlier task; it
    /// has no legitimate call site and exists only so <see cref="DisposeAsync"/> can cancel.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loop = RunAsync(_cts.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var scene = MapScene.FromSnapshot(_store.Snapshot);
                await _push(scene, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown path: DisposeAsync cancelled the token this loop waits on.
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _notifier.SessionChanged -= _onSessionChanged;
        _notifier.TelemetryUpdated -= _onTelemetryUpdated;

        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Already handled inside RunAsync; this guards against a race where the loop
                // observed cancellation via the timer's own exception path instead.
            }
        }

        _cts.Dispose();
    }
}

/// <summary>
/// Callback signature for pushing one projected <see cref="MapScene"/> to its render sink
/// (typically the canvas' JS interop call). Returning a <see cref="ValueTask"/> lets the WebView2
/// JS interop implementation stay allocation-free on the hot path while still supporting
/// cancellation.
/// </summary>
/// <param name="scene">The scene to render.</param>
/// <param name="cancellationToken">Cancelled when the owning presenter is disposed.</param>
public delegate ValueTask MapScenePush(MapScene scene, CancellationToken cancellationToken);
