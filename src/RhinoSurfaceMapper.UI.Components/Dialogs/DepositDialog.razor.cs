using Microsoft.AspNetCore.Components;
using RhinoSurfaceMapper.Application.Features.Markers;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Enums;

namespace RhinoSurfaceMapper.UI.Components.Dialogs;

/// <summary>
/// Code-behind for <c>DepositDialog.razor</c>. Create mode (<see cref="DepositId"/> is
/// <see langword="null"/>) dispatches <see cref="CreateDeposit"/> at the live Rhino position;
/// edit mode dispatches <see cref="UpdateDeposit"/> and never moves the deposit, matching
/// Python's <c>edit_deposit</c> contract (position is immutable once recorded).
/// </summary>
public partial class DepositDialog
{
    private bool _busy;

    /// <summary>Whether the dialog is currently shown.</summary>
    [Parameter]
    public bool IsOpen { get; set; }

    /// <summary>Raised once the dialog should be dismissed, whether by Cancel or a successful save.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>The deposit being edited, or <see langword="null"/> to create a new one.</summary>
    [Parameter]
    public Guid? DepositId { get; set; }

    /// <summary>Pre-filled name when editing; ignored in create mode.</summary>
    [Parameter]
    public string InitialName { get; set; } = string.Empty;

    /// <summary>Pre-filled size when editing; ignored in create mode.</summary>
    [Parameter]
    public DepositSize InitialSize { get; set; } = DepositSize.Pequeno;

    /// <summary>Pre-filled rig count when editing; ignored in create mode.</summary>
    [Parameter]
    public int InitialRigs { get; set; } = 1;

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    private string Name { get; set; } = string.Empty;

    private DepositSize Size { get; set; } = DepositSize.Pequeno;

    private int Rigs { get; set; } = 1;

    private string? ErrorMessage { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!IsOpen)
        {
            return;
        }

        Name = DepositId is null ? string.Empty : InitialName;
        Size = DepositId is null ? DepositSize.Pequeno : InitialSize;
        Rigs = DepositId is null ? 1 : InitialRigs;
        ErrorMessage = null;
    }

    private async Task SubmitAsync()
    {
        _busy = true;
        try
        {
            if (DepositId is Guid id)
            {
                var response = await Mediator.SendCommandAsync<UpdateDeposit.Command, UpdateDeposit.Response>(
                    new UpdateDeposit.Command { DepositId = id, Name = Name, Size = Size, Rigs = Rigs });

                if (response.Result != UpdateDeposit.Result.Success)
                {
                    ErrorMessage = $"Could not update deposit: {response.Result}.";
                    return;
                }
            }
            else
            {
                var response = await Mediator.SendCommandAsync<CreateDeposit.Command, CreateDeposit.Response>(
                    new CreateDeposit.Command { Name = Name, Size = Size, Rigs = Rigs });

                if (response.Result != CreateDeposit.Result.Success)
                {
                    ErrorMessage = $"Could not create deposit: {response.Result}.";
                    return;
                }
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
