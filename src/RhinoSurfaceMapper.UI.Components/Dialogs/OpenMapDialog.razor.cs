using Microsoft.AspNetCore.Components;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Features.Pml;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.UI.Components.Dialogs;

/// <summary>
/// Code-behind for <c>OpenMapDialog.razor</c>. Dispatches <see cref="LoadMap"/> for the path the
/// user supplies or picks from <see cref="ListPmlVersions"/>'s results.
/// </summary>
/// <remarks>
/// <strong>Deliberate scope reduction:</strong> Python's picker is a native Qt file-open dialog;
/// this phase's instructions scope dialogs to "the same data contract, not the same technology"
/// and explicitly flag that native file-browser chrome is a <c>Platform.Windows</c> concern. No
/// <c>Platform.Windows</c> file-picker interop exists yet, so this dialog instead offers a plain
/// text path field plus a convenience list of the active PML's other saved versions. Wiring an
/// actual native "Browse…" button is left to a future UI-polish pass (Phase 5/6), consistent
/// with this phase's "visually simple, not the goal" instruction.
/// </remarks>
public partial class OpenMapDialog
{
    private bool _busy;

    /// <summary>Whether the dialog is currently shown.</summary>
    [Parameter]
    public bool IsOpen { get; set; }

    /// <summary>Raised once the dialog should be dismissed, whether by Cancel or a successful open.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    private string Path { get; set; } = string.Empty;

    private string? ErrorMessage { get; set; }

    private IReadOnlyList<ListPmlVersions.Version> Versions { get; set; } = [];

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        if (!IsOpen)
        {
            return;
        }

        ErrorMessage = null;
        var response = await Mediator.SendQueryAsync<ListPmlVersions.Query, ListPmlVersions.Response>(new ListPmlVersions.Query());
        Versions = response.Versions;
    }

    private void SelectVersion(string path) => Path = path;

    private async Task SubmitAsync()
    {
        _busy = true;
        try
        {
            var response = await Mediator.SendCommandAsync<LoadMap.Command, LoadMap.Response>(new LoadMap.Command { Path = Path });

            if (response.Result != LoadMap.Result.Success)
            {
                ErrorMessage = $"Could not open map: {response.Result}.";
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
