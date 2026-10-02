using Microsoft.AspNetCore.Components;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.UI.Components.Dialogs;

/// <summary>
/// Code-behind for <c>ProtectedMapDialog.razor</c>. Dispatches
/// <see cref="ResolveUnsavedChanges"/> for the three actionable buttons; "Not now" dismisses the
/// dialog for the current transition without resolving anything — exactly like Python's own
/// Cancel button, the transition simply stays pending and <c>EvaluateTelemetryPoll</c> keeps
/// re-surfacing it every subsequent poll.
/// </summary>
/// <remarks>
/// Subscribes to <see cref="IMapSessionNotifier.TelemetryUpdated"/>/<see cref="IMapSessionNotifier.SessionChanged"/>
/// rather than taking an <c>IsOpen</c> parameter: unlike every other dialog in this folder, this
/// one is driven entirely by <see cref="IMapTransitionCoordinator"/>'s own state (set by the
/// background telemetry poll loop, not by a toolbar click), so it must react to events raised
/// from that loop instead of a parent's render. This mirrors <c>MapCanvas</c>'s own
/// notifier-subscription pattern from Phase 3.
/// </remarks>
public partial class ProtectedMapDialog : IDisposable
{
    private bool _visible;
    private bool _busy;
    private bool _dismissedForCurrentTransition;

    [Inject]
    private IMapTransitionCoordinator Coordinator { get; set; } = null!;

    [Inject]
    private IMapSessionNotifier Notifier { get; set; } = null!;

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    private string? ErrorMessage { get; set; }

    /// <inheritdoc />
    protected override void OnInitialized()
    {
        Notifier.TelemetryUpdated += OnNotified;
        Notifier.SessionChanged += OnNotified;
        Refresh();
    }

    private void OnNotified(object? sender, EventArgs e) => _ = InvokeAsync(() =>
    {
        Refresh();
        StateHasChanged();
    });

    private void Refresh()
    {
        if (!Coordinator.TransitionRequired || Coordinator.PendingOldMapResolved)
        {
            _dismissedForCurrentTransition = false;
            _visible = false;
            return;
        }

        _visible = !_dismissedForCurrentTransition;
    }

    private void Dismiss()
    {
        _dismissedForCurrentTransition = true;
        _visible = false;
    }

    private async Task ResolveAsync(OldMapDisposition disposition)
    {
        _busy = true;
        try
        {
            await Mediator.SendCommandAsync<ResolveUnsavedChanges.Command, ResolveUnsavedChanges.Response>(
                new ResolveUnsavedChanges.Command { Disposition = disposition });
            ErrorMessage = null;
            _dismissedForCurrentTransition = false;
            _visible = false;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Notifier.TelemetryUpdated -= OnNotified;
        Notifier.SessionChanged -= OnNotified;
    }
}
