using Microsoft.AspNetCore.Components;
using RhinoSurfaceMapper.Application.Features.Markers;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;

namespace RhinoSurfaceMapper.UI.Components.Map;

/// <summary>
/// Code-behind for <c>MapToolbar.razor</c>.
/// </summary>
/// <remarks>
/// <strong>Deliberate scope reduction:</strong> Python's rig placement is driven by a canvas
/// click converted to local metres by <c>self.view.world(point)</c> before calling
/// <c>place_rig</c>. No click-to-place canvas gesture exists yet in <c>MapCanvas</c> (it remains
/// Phase 3's read-only render surface); wiring that is explicitly out of scope per this phase's
/// "a minimal MapToolbar... is sufficient" allowance. "Place rig here" instead places at the
/// live Rhino's most recently recorded local position (the last <see cref="Domain.Entities.TrailPoint"/>
/// in the snapshot, which already carries the local X/Y the trail renderer uses) — a reasonable
/// placeholder for "where the commander currently is" until a future phase adds the click
/// gesture and a screen-to-local conversion in the canvas itself.
/// </remarks>
public partial class MapToolbar
{
    private enum DialogKind
    {
        None,
        NewMap,
        Open,
        Save,
        Deposit,
        Mark,
    }

    private DialogKind _openDialog = DialogKind.None;

    [Inject]
    private IMediator Mediator { get; set; } = null!;

    [Inject]
    private IMapSessionStore Store { get; set; } = null!;

    private void CloseDialog() => _openDialog = DialogKind.None;

    private async Task PlaceRigAtRhinoPositionAsync()
    {
        var points = Store.Snapshot.Points;
        if (points.IsDefaultOrEmpty)
        {
            return;
        }

        var last = points[^1];
        await Mediator.SendCommandAsync<PlaceRig.Command, PlaceRig.Response>(new PlaceRig.Command { X = last.X, Y = last.Y });
    }

    private async Task EnterMiningModeAsync() =>
        await Mediator.SendCommandAsync<EnterMiningMode.Command, EnterMiningMode.Response>(new EnterMiningMode.Command());
}
