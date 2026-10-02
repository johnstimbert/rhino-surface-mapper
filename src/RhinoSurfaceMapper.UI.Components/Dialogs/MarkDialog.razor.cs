using Microsoft.AspNetCore.Components;
using RhinoSurfaceMapper.Application.Features.Markers;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.UI.Components.Dialogs;

/// <summary>
/// Code-behind for <c>MarkDialog.razor</c>. Create mode (<see cref="MarkId"/> is
/// <see langword="null"/>) dispatches <see cref="CreateMark"/>; edit mode dispatches
/// <see cref="UpdateMark"/>, passing both the dialog's pre-filled (<see cref="InitialAzimuthDegrees"/>/
/// <see cref="InitialDistanceMetres"/>) and confirmed values so the handler can detect a
/// name-only edit and leave the mark's position untouched, exactly as
/// <c>test_alter_mark_cancel_and_name_only_preserve_position</c> requires.
/// </summary>
public partial class MarkDialog
{
    private bool _busy;

    /// <summary>Whether the dialog is currently shown.</summary>
    [Parameter]
    public bool IsOpen { get; set; }

    /// <summary>Raised once the dialog should be dismissed, whether by Cancel or a successful save.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>The mark being edited, or <see langword="null"/> to create a new one.</summary>
    [Parameter]
    public Guid? MarkId { get; set; }

    /// <summary>Pre-filled name when editing; ignored in create mode.</summary>
    [Parameter]
    public string InitialName { get; set; } = string.Empty;

    /// <summary>The bearing the mark currently sits at, pre-filling the dialog when editing.</summary>
    [Parameter]
    public double InitialAzimuthDegrees { get; set; }

    /// <summary>The distance the mark currently sits at, pre-filling the dialog when editing.</summary>
    [Parameter]
    public double InitialDistanceMetres { get; set; }

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    private string Name { get; set; } = string.Empty;

    private double AzimuthDegrees { get; set; }

    private double DistanceMetres { get; set; }

    private string? ErrorMessage { get; set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!IsOpen)
        {
            return;
        }

        Name = MarkId is null ? string.Empty : InitialName;
        AzimuthDegrees = InitialAzimuthDegrees;
        DistanceMetres = InitialDistanceMetres;
        ErrorMessage = null;
    }

    private async Task SubmitAsync()
    {
        _busy = true;
        try
        {
            if (MarkId is Guid id)
            {
                var response = await Mediator.SendCommandAsync<UpdateMark.Command, UpdateMark.Response>(
                    new UpdateMark.Command
                    {
                        MarkId = id,
                        Name = Name,
                        ExistingAzimuthDegrees = InitialAzimuthDegrees,
                        ExistingDistanceMetres = InitialDistanceMetres,
                        AzimuthDegrees = AzimuthDegrees,
                        DistanceMetres = DistanceMetres,
                    });

                if (response.Result != UpdateMark.Result.Success)
                {
                    ErrorMessage = $"Could not update mark: {response.Result}.";
                    return;
                }
            }
            else
            {
                var response = await Mediator.SendCommandAsync<CreateMark.Command, CreateMark.Response>(
                    new CreateMark.Command { Name = Name, AzimuthDegrees = AzimuthDegrees, DistanceMetres = DistanceMetres });

                if (response.Result != CreateMark.Result.Success)
                {
                    ErrorMessage = $"Could not create mark: {response.Result}.";
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
