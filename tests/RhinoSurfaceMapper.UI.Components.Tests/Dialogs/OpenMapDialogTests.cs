using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Features.Pml;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.UI.Components.Dialogs;

namespace RhinoSurfaceMapper.UI.Components.Tests.Dialogs;

public sealed class OpenMapDialogTests : BunitContext
{
    [Fact]
    public void Dialog_Should_RenderNothingAndSkipVersionQuery_When_IsOpenIsFalse()
    {
        var mediator = RegisterMediator();

        var component = Render<OpenMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, false));

        component.Markup.Trim().Should().BeEmpty();
        mediator.Verify(service => service.SendQueryAsync<ListPmlVersions.Query, ListPmlVersions.Response>(
            It.IsAny<ListPmlVersions.Query>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void Dialog_Should_RenderContentAndListedVersions_When_IsOpenIsTrue()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendQueryAsync<ListPmlVersions.Query, ListPmlVersions.Response>(
                It.IsAny<ListPmlVersions.Query>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListPmlVersions.Response
            {
                Versions =
                [
                    new ListPmlVersions.Version(@"E:\maps\alpha.json", new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc))
                ],
            });

        var component = Render<OpenMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true));

        component.WaitForAssertion(() =>
        {
            component.Markup.Should().Contain("Open map");
            component.Markup.Should().Contain(@"E:\maps\alpha.json");
        });
    }

    [Fact]
    public void SubmitAsync_Should_DispatchLoadMapAndClose_When_SelectedVersionLoadsSuccessfully()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendQueryAsync<ListPmlVersions.Query, ListPmlVersions.Response>(
                It.IsAny<ListPmlVersions.Query>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListPmlVersions.Response
            {
                Versions =
                [
                    new ListPmlVersions.Version(@"E:\maps\selected.json", new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc))
                ],
            });
        mediator
            .Setup(service => service.SendCommandAsync<LoadMap.Command, LoadMap.Response>(
                It.IsAny<LoadMap.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoadMap.Response { Result = LoadMap.Result.Success, Path = @"E:\maps\selected.json" });

        bool closed = false;
        var component = Render<OpenMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.WaitForAssertion(() => component.Markup.Should().Contain(@"E:\maps\selected.json"));
        component.FindAll("button").Single(button => button.TextContent.Contains(@"E:\maps\selected.json", StringComparison.Ordinal)).Click();
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Open").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeTrue();
            mediator.Verify(service => service.SendCommandAsync<LoadMap.Command, LoadMap.Response>(
                    It.Is<LoadMap.Command>(command => command.Path == @"E:\maps\selected.json"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Fact]
    public void SubmitAsync_Should_ShowErrorAndStayOpen_When_LoadFails()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendQueryAsync<ListPmlVersions.Query, ListPmlVersions.Response>(
                It.IsAny<ListPmlVersions.Query>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListPmlVersions.Response { Versions = [] });
        mediator
            .Setup(service => service.SendCommandAsync<LoadMap.Command, LoadMap.Response>(
                It.IsAny<LoadMap.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LoadMap.Response { Result = LoadMap.Result.NotFound, Path = null });

        bool closed = false;
        var component = Render<OpenMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.Find("#open-path").Input(@"E:\maps\missing.json");
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Open").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeFalse();
            component.Markup.Should().Contain("Could not open map: NotFound.");
        });
    }

    [Fact]
    public void CancelAsync_Should_CloseWithoutDispatchingLoadCommand_When_CancelIsClicked()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendQueryAsync<ListPmlVersions.Query, ListPmlVersions.Response>(
                It.IsAny<ListPmlVersions.Query>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListPmlVersions.Response { Versions = [] });

        bool closed = false;
        var component = Render<OpenMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.WaitForAssertion(() => component.Markup.Should().Contain("Open map"));
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        component.WaitForAssertion(() => closed.Should().BeTrue());
        mediator.Verify(service => service.SendCommandAsync<LoadMap.Command, LoadMap.Response>(
            It.IsAny<LoadMap.Command>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private Mock<IMediator> RegisterMediator()
    {
        var mediator = new Mock<IMediator>();
        Services.AddSingleton(mediator.Object);
        return mediator;
    }
}
