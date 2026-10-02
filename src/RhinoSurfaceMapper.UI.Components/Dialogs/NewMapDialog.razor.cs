using Microsoft.AspNetCore.Components;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.UI.Components.Dialogs;

/// <summary>Code-behind for <c>NewMapDialog.razor</c>: dispatches <see cref="NewMap"/>.</summary>
public partial class NewMapDialog
{
    private bool _busy;

    /// <summary>Whether the dialog is currently shown.</summary>
    [Parameter]
    public bool IsOpen { get; set; }

    /// <summary>Raised once the dialog should be dismissed, whether by Cancel or a successful reset.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    private bool KeepPml { get; set; }

    private string? ErrorMessage { get; set; }

    private async Task SubmitAsync()
    {
        _busy = true;
        try
        {
            await Mediator.SendCommandAsync<NewMap.Command, NewMap.Response>(new NewMap.Command { KeepPml = KeepPml });
            await OnClose.InvokeAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CancelAsync() => await OnClose.InvokeAsync();
}
