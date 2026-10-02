using Microsoft.AspNetCore.Components;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.UI.Components.Dialogs;

/// <summary>Code-behind for <c>SaveMapDialog.razor</c>: dispatches <see cref="SaveMap"/>.</summary>
public partial class SaveMapDialog
{
    private bool _busy;

    /// <summary>Whether the dialog is currently shown.</summary>
    [Parameter]
    public bool IsOpen { get; set; }

    /// <summary>Raised once the dialog should be dismissed, whether by Cancel or a successful save.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    private SaveMode Mode { get; set; } = SaveMode.Replace;

    private string ExplicitPath { get; set; } = string.Empty;

    private string? ErrorMessage { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!IsOpen)
        {
            return;
        }

        ErrorMessage = null;
    }

    private async Task SubmitAsync()
    {
        _busy = true;
        try
        {
            var response = await Mediator.SendCommandAsync<SaveMap.Command, SaveMap.Response>(
                new SaveMap.Command
                {
                    Mode = Mode,
                    ExplicitPath = Mode == SaveMode.ExplicitPath ? ExplicitPath : null,
                });

            if (response.Result != SaveMap.Result.Success)
            {
                ErrorMessage = $"Could not save map: {response.Result}.";
                return;
            }

            await OnClose.InvokeAsync();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CancelAsync() => await OnClose.InvokeAsync();
}
