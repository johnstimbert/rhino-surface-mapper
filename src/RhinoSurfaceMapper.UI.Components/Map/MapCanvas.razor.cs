using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Domain.Constants;

namespace RhinoSurfaceMapper.UI.Components.Map;

/// <summary>
/// Code-behind for <c>MapCanvas.razor</c>: owns the <see cref="MapCanvasInterop"/> JS module
/// instance and the <see cref="MapScenePresenter"/> that feeds it, per the design's "Map canvas"
/// and "Rendering cadence" sections. Renders the single live <see cref="IMapSessionStore"/>
/// session; this phase (read-only map display) takes no parameters and exposes no editing
/// surface.
/// </summary>
public partial class MapCanvas : IAsyncDisposable
{
    private ElementReference _canvasElement;
    private MapCanvasInterop? _interop;
    private MapScenePresenter? _presenter;

    /// <summary>The map session store this canvas renders from.</summary>
    [Inject]
    private IMapSessionStore SessionStore { get; set; } = null!;

    /// <summary>Source of discrete session-change notifications driving non-geometry re-renders.</summary>
    [Inject]
    private IMapSessionNotifier Notifier { get; set; } = null!;

    /// <summary>The JS runtime used to create this component's <see cref="MapCanvasInterop"/>.</summary>
    [Inject]
    private IJSRuntime JSRuntime { get; set; } = null!;

    /// <summary>Logger for interop lifecycle/failure events (<see cref="LogEvents.MapCanvasInitialized"/>/<see cref="LogEvents.MapCanvasInteropFailed"/>).</summary>
    [Inject]
    private ILogger<MapCanvas> Logger { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        _interop = new MapCanvasInterop(JSRuntime, Logger);

        try
        {
            await _interop.InitializeAsync(_canvasElement).ConfigureAwait(false);
            Logger.MapCanvasInitialized();
        }
        catch (JSException ex)
        {
            // Matches risk R1's "fail gracefully... log Critical" intent at the component level:
            // the canvas simply never receives scene pushes (map-canvas.js failed to load, most
            // likely because the WebView2 Runtime itself failed to initialise), rather than
            // crashing the whole render tree over a missing drawing surface.
            Logger.MapCanvasInteropFailed(ex);
            return;
        }

        _presenter = new MapScenePresenter(SessionStore, Notifier, PushSceneAsync);
        _presenter.DiscreteStateChanged += OnDiscreteStateChanged;
        _presenter.Start();
    }

    private ValueTask PushSceneAsync(MapScene scene, CancellationToken cancellationToken) =>
        _interop is null ? ValueTask.CompletedTask : _interop.PushSceneAsync(scene, cancellationToken);

    private void OnDiscreteStateChanged()
    {
        // Notifier events are raised from a background hosted service thread; InvokeAsync
        // marshals the resulting StateHasChanged onto this component's own renderer
        // synchronization context, exactly as the design's "UiDispatcher" responsibility
        // requires (ComponentBase.InvokeAsync standing in for a dedicated dispatcher type in
        // this phase's minimal scope).
        _ = InvokeAsync(StateHasChanged);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_presenter is not null)
        {
            _presenter.DiscreteStateChanged -= OnDiscreteStateChanged;
            await _presenter.DisposeAsync().ConfigureAwait(false);
        }

        if (_interop is not null)
        {
            await _interop.DisposeAsync().ConfigureAwait(false);
        }
    }
}
