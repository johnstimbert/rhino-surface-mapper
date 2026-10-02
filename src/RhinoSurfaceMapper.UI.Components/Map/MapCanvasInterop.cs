using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using RhinoSurfaceMapper.Domain.Constants;

namespace RhinoSurfaceMapper.UI.Components.Map;

/// <summary>
/// Thin JS interop wrapper around <c>wwwroot/map-canvas.js</c>, isolating every
/// <see cref="IJSRuntime"/> call behind a small, test-friendly surface so <c>MapCanvas</c>'s
/// code-behind never calls <see cref="IJSRuntime"/> directly. Packs the high-cardinality trail
/// array as a flat <c>double[]</c>/<c>bool[]</c> pair (serialised by the framework's JSON-based
/// interop as compact JS typed-array-friendly arrays) instead of one JS call per point, per the
/// design's "packed... payloads... to avoid per-point JSON overhead" rule.
/// </summary>
public sealed class MapCanvasInterop : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private readonly ILogger _logger;
    private IJSObjectReference? _module;
    private IJSObjectReference? _instance;

    /// <summary>Creates the interop wrapper over the given <see cref="IJSRuntime"/>.</summary>
    /// <param name="jsRuntime">The JS runtime to import <c>map-canvas.js</c> through.</param>
    /// <param name="logger">
    /// Logs a per-tick push failure (<see cref="LogEvents.MapCanvasInteropFailed"/>) instead of
    /// letting it propagate out of the caller's render loop.
    /// </param>
    public MapCanvasInterop(IJSRuntime jsRuntime, ILogger logger)
    {
        _jsRuntime = jsRuntime;
        _logger = logger;
    }

    /// <summary>
    /// Imports <c>map-canvas.js</c> as an ES module and creates one canvas instance bound to
    /// <paramref name="canvasElement"/>. Must complete before <see cref="PushSceneAsync"/> is
    /// called; the design assigns JS ownership of the <c>requestAnimationFrame</c> draw loop and
    /// pan/zoom gestures to this instance from the moment it is created.
    /// </summary>
    /// <param name="canvasElement">The <c>&lt;canvas&gt;</c> element reference to render into.</param>
    public async Task InitializeAsync(ElementReference canvasElement)
    {
        _module = await _jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/RhinoSurfaceMapper.UI.Components/map-canvas.js");
        _instance = await _module.InvokeAsync<IJSObjectReference>("createMapCanvas", canvasElement);
    }

    /// <summary>
    /// Pushes one projected <see cref="MapScene"/> to the JS-owned draw loop. A no-op before
    /// <see cref="InitializeAsync"/> has completed, so a presenter tick racing component
    /// start-up simply drops that one frame rather than throwing.
    /// </summary>
    /// <param name="scene">The scene to render.</param>
    /// <param name="cancellationToken">Cancels the interop call.</param>
    public async ValueTask PushSceneAsync(MapScene scene, CancellationToken cancellationToken = default)
    {
        if (_instance is null)
        {
            return;
        }

        var trailX = new double[scene.Trail.Length];
        var trailY = new double[scene.Trail.Length];
        var trailBreak = new bool[scene.Trail.Length];
        for (int i = 0; i < scene.Trail.Length; i++)
        {
            trailX[i] = scene.Trail[i].X;
            trailY[i] = scene.Trail[i].Y;
            trailBreak[i] = scene.Trail[i].BreakBefore;
        }

        var meta = new MapSceneMeta(
            scene.Generation,
            scene.Rhino?.X,
            scene.Rhino?.Y,
            scene.Rhino?.HeadingDegrees);

        try
        {
            await _instance.InvokeVoidAsync("pushScene", cancellationToken, meta, trailX, trailY, trailBreak).ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            // The WebView's circuit tore down (window closing) while a push was in flight;
            // there is no surface left to draw to, so this is not an error worth propagating.
        }
        catch (JSException ex)
        {
            // A genuine JS-side fault (malformed payload, transient WebView2 error) must not
            // propagate into MapScenePresenter's hot loop and permanently kill its background
            // task — log it via the existing MapCanvasInteropFailed event and drop this one
            // frame; the loop continues and the next tick simply pushes a fresh scene.
            _logger.MapCanvasInteropFailed(ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_instance is not null)
        {
            try
            {
                await _instance.InvokeVoidAsync("dispose").ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // See PushSceneAsync's remark: nothing left to dispose on the JS side either.
            }

            await _instance.DisposeAsync().ConfigureAwait(false);
            _instance = null;
        }

        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // See above.
            }

            _module = null;
        }
    }

    /// <summary>
    /// Compact scene metadata passed alongside the packed trail arrays. A separate type (rather
    /// than an anonymous object) so its JSON shape is documented and stable for
    /// <c>map-canvas.js</c> to destructure.
    /// </summary>
    /// <param name="Generation">Mirrors <see cref="MapScene.Generation"/>.</param>
    /// <param name="RhinoX">World X of the Rhino marker, or <see langword="null"/> when there is none.</param>
    /// <param name="RhinoY">World Y of the Rhino marker, or <see langword="null"/> when there is none.</param>
    /// <param name="RhinoHeadingDegrees">Rhino heading in degrees, or <see langword="null"/>.</param>
    private sealed record MapSceneMeta(int Generation, double? RhinoX, double? RhinoY, double? RhinoHeadingDegrees);
}
