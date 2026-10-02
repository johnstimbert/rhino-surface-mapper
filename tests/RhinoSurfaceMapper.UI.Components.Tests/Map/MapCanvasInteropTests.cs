using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using RhinoSurfaceMapper.UI.Components.Map;

namespace RhinoSurfaceMapper.UI.Components.Tests.Map;

/// <summary>
/// Regression tests for review BLOCKING 2: a <see cref="JSException"/> raised while pushing one
/// scene must be caught, logged via the existing <c>MapCanvasInteropFailed</c> event, and must
/// not prevent later pushes from succeeding — the hot render loop must survive a single bad
/// tick exactly like the telemetry poll loop does for a bad read.
/// </summary>
public sealed class MapCanvasInteropTests
{
    private static MapScene SampleScene() => MapScene.Empty;

    [Fact]
    public async Task PushSceneAsync_logs_and_swallows_a_JSException_without_throwing()
    {
        var instance = new ThrowOnceOnPushJsObjectReference();
        var module = new ForwardingJsObjectReference(instance);
        var jsRuntime = new ImportingJsRuntime(module);
        var logger = new CapturingLogger();
        var interop = new MapCanvasInterop(jsRuntime, logger);

        await interop.InitializeAsync(default(ElementReference));

        Func<Task> firstPush = async () => await interop.PushSceneAsync(SampleScene(), CancellationToken.None).AsTask();

        await firstPush.Should().NotThrowAsync("a JSException from one push must be caught, not propagated to the caller");

        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error,
            "the swallowed JSException must still be logged via the existing MapCanvasInteropFailed event");
    }

    [Fact]
    public async Task PushSceneAsync_keeps_pushing_on_a_later_tick_after_a_prior_JSException()
    {
        var instance = new ThrowOnceOnPushJsObjectReference();
        var module = new ForwardingJsObjectReference(instance);
        var jsRuntime = new ImportingJsRuntime(module);
        var logger = new CapturingLogger();
        var interop = new MapCanvasInterop(jsRuntime, logger);

        await interop.InitializeAsync(default(ElementReference));

        await interop.PushSceneAsync(SampleScene(), CancellationToken.None); // throws internally; must be swallowed.
        await interop.PushSceneAsync(SampleScene(), CancellationToken.None); // must succeed, proving the loop is not wedged.

        instance.PushCallCount.Should().Be(2, "the second push must actually reach the JS side, not be skipped");
        logger.Entries.Count(e => e.Level == LogLevel.Error).Should().Be(1,
            "only the first push failed; the second succeeded and must not log another failure");
    }

    /// <summary>Fake <see cref="IJSRuntime"/> whose only responsibility is to hand back a fixed module for the <c>import</c> call.</summary>
    private sealed class ImportingJsRuntime(IJSObjectReference module) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult((TValue)module);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((TValue)module);
    }

    /// <summary>Fake JS module reference that hands back a fixed instance for the <c>createMapCanvas</c> call.</summary>
    private sealed class ForwardingJsObjectReference(IJSObjectReference instance) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult((TValue)instance);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult((TValue)instance);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Fake JS canvas instance whose first <c>pushScene</c> call throws <see cref="JSException"/>; later calls succeed.</summary>
    private sealed class ThrowOnceOnPushJsObjectReference : IJSObjectReference
    {
        public int PushCallCount { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeCore<TValue>(identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeCore<TValue>(identifier);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private ValueTask<TValue> InvokeCore<TValue>(string identifier)
        {
            if (identifier == "pushScene")
            {
                PushCallCount++;
                if (PushCallCount == 1)
                {
                    throw new JSException("Simulated transient WebView2 fault");
                }
            }

            return ValueTask.FromResult(default(TValue)!);
        }
    }

    /// <summary>Minimal <see cref="ILogger"/> double recording every call, independent of category type.</summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
