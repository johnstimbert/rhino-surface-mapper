using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Features.Markers;
using RhinoSurfaceMapper.Application.Features.Pml;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Application.Services;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.UI.Components.Dialogs;

namespace RhinoSurfaceMapper.UI.Components.Tests.Map;

public sealed class MapToolbarTests : BunitContext
{
    [Fact]
    public void Toolbar_Should_RenderAllExpectedButtons_When_Initialized()
    {
        RegisterServices(MapSessionSnapshot.Empty, out _, out _);

        var component = Render<RhinoSurfaceMapper.UI.Components.Map.MapToolbar>();

        var buttonTexts = component.FindAll("button").Select(button => button.TextContent.Trim()).ToArray();

        buttonTexts.Should().Equal(
            "New",
            "Open",
            "Save",
            "Add deposit",
            "Add mark",
            "Place rig here",
            "Mining mode");
    }

    [Fact]
    public void Toolbar_Should_RenderActualDialogChildComponents_When_Initialized()
    {
        RegisterServices(MapSessionSnapshot.Empty, out _, out _);

        var component = Render<RhinoSurfaceMapper.UI.Components.Map.MapToolbar>();

        component.FindComponents<NewMapDialog>().Should().ContainSingle();
        component.FindComponents<OpenMapDialog>().Should().ContainSingle();
        component.FindComponents<SaveMapDialog>().Should().ContainSingle();
        component.FindComponents<DepositDialog>().Should().ContainSingle();
        component.FindComponents<MarkDialog>().Should().ContainSingle();
        component.FindComponents<ProtectedMapDialog>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("New", "Start a new map?")]
    [InlineData("Open", "Open map")]
    [InlineData("Save", "Save map")]
    [InlineData("Add deposit", "New deposit")]
    [InlineData("Add mark", "New mark")]
    public void Toolbar_Should_OpenExpectedDialog_When_ButtonIsClicked(string buttonText, string expectedContent)
    {
        RegisterServices(MapSessionSnapshot.Empty, out _, out _);

        var component = Render<RhinoSurfaceMapper.UI.Components.Map.MapToolbar>();

        component.FindAll("button").Single(button => button.TextContent.Trim() == buttonText).Click();

        component.WaitForAssertion(() => component.Markup.Should().Contain(expectedContent));
    }

    [Fact]
    public void PlaceRigAtRhinoPositionAsync_Should_DispatchPlaceRigWithLastTrailPoint_When_TrailHasPoints()
    {
        var snapshot = MapSessionSnapshot.Empty with
        {
            Points =
            [
                new TrailPoint(1.0, 2.0, 0.0, 0.0, 100.0),
                new TrailPoint(12.5, -7.25, 0.0, 0.0, 200.0),
            ],
        };

        var mediator = RegisterServices(snapshot, out _, out _);
        mediator
            .Setup(service => service.SendCommandAsync<PlaceRig.Command, PlaceRig.Response>(
                It.IsAny<PlaceRig.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PlaceRig.Response { Result = PlaceRig.Result.Success });

        var component = Render<RhinoSurfaceMapper.UI.Components.Map.MapToolbar>();

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Place rig here").Click();

        component.WaitForAssertion(() =>
            mediator.Verify(service => service.SendCommandAsync<PlaceRig.Command, PlaceRig.Response>(
                    It.Is<PlaceRig.Command>(command => command.X == 12.5 && command.Y == -7.25),
                    It.IsAny<CancellationToken>()),
                Times.Once));
    }

    [Fact]
    public void PlaceRigAtRhinoPositionAsync_Should_NotDispatch_When_TrailHasNoPoints()
    {
        var mediator = RegisterServices(MapSessionSnapshot.Empty, out _, out _);

        var component = Render<RhinoSurfaceMapper.UI.Components.Map.MapToolbar>();

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Place rig here").Click();

        mediator.Verify(service => service.SendCommandAsync<PlaceRig.Command, PlaceRig.Response>(
            It.IsAny<PlaceRig.Command>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void EnterMiningModeAsync_Should_DispatchCommand_When_ButtonIsClicked()
    {
        var mediator = RegisterServices(MapSessionSnapshot.Empty, out _, out _);
        mediator
            .Setup(service => service.SendCommandAsync<EnterMiningMode.Command, EnterMiningMode.Response>(
                It.IsAny<EnterMiningMode.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnterMiningMode.Response { Result = EnterMiningMode.Result.Success });

        var component = Render<RhinoSurfaceMapper.UI.Components.Map.MapToolbar>();

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Mining mode").Click();

        component.WaitForAssertion(() =>
            mediator.Verify(service => service.SendCommandAsync<EnterMiningMode.Command, EnterMiningMode.Response>(
                    It.Is<EnterMiningMode.Command>(_ => true),
                    It.IsAny<CancellationToken>()),
                Times.Once));
    }

    private Mock<IMediator> RegisterServices(MapSessionSnapshot snapshot, out Mock<IMapSessionStore> store, out MapSessionNotifier notifier)
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(service => service.SendQueryAsync<ListPmlVersions.Query, ListPmlVersions.Response>(
                It.IsAny<ListPmlVersions.Query>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListPmlVersions.Response { Versions = [] });

        store = new Mock<IMapSessionStore>();
        store.SetupGet(service => service.Snapshot).Returns(snapshot);

        var coordinator = new Mock<IMapTransitionCoordinator>();
        coordinator.SetupGet(service => service.TransitionRequired).Returns(false);
        coordinator.SetupGet(service => service.PendingOldMapResolved).Returns(false);

        notifier = new MapSessionNotifier();

        Services.AddSingleton(mediator.Object);
        Services.AddSingleton(store.Object);
        Services.AddSingleton(coordinator.Object);
        Services.AddSingleton<IMapSessionNotifier>(notifier);
        return mediator;
    }
}
