using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RhinoSurfaceMapper.Application.Features.Markers;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.UI.Components.Dialogs;

namespace RhinoSurfaceMapper.UI.Components.Tests.Dialogs;

public sealed class MarkDialogTests : BunitContext
{
    [Fact]
    public void Dialog_Should_RenderNothing_When_IsOpenIsFalse()
    {
        var mediator = RegisterMediator();

        var component = Render<MarkDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, false));

        component.Markup.Trim().Should().BeEmpty();
        mediator.VerifyNoOtherCalls();
    }

    [Fact]
    public void Dialog_Should_RenderCreateContent_When_IsOpenIsTrue()
    {
        RegisterMediator();

        var component = Render<MarkDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true));

        component.Markup.Should().Contain("New mark");
        component.Find("#mark-name").Should().NotBeNull();
        component.Find("#mark-azimuth").Should().NotBeNull();
        component.Find("#mark-distance").Should().NotBeNull();
    }

    [Fact]
    public void SubmitAsync_Should_DispatchCreateMarkAndClose_When_CreatingMarkSucceeds()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<CreateMark.Command, CreateMark.Response>(
                It.IsAny<CreateMark.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateMark.Response { Result = CreateMark.Result.Success });

        bool closed = false;
        var component = Render<MarkDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.Find("#mark-name").Input("Base");
        component.Find("#mark-azimuth").Change("123.4");
        component.Find("#mark-distance").Change("567.8");
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeTrue();
            mediator.Verify(service => service.SendCommandAsync<CreateMark.Command, CreateMark.Response>(
                    It.Is<CreateMark.Command>(command =>
                        command.Name == "Base"
                        && command.AzimuthDegrees == 123.4
                        && command.DistanceMetres == 567.8),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Fact]
    public void SubmitAsync_Should_DispatchUpdateMarkAndClose_When_EditingMarkSucceeds()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<UpdateMark.Command, UpdateMark.Response>(
                It.IsAny<UpdateMark.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateMark.Response { Result = UpdateMark.Result.Success });

        bool closed = false;
        Guid markId = Guid.NewGuid();
        var component = Render<MarkDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.MarkId, markId)
            .Add(dialog => dialog.InitialName, "Legacy mark")
            .Add(dialog => dialog.InitialAzimuthDegrees, 45.0)
            .Add(dialog => dialog.InitialDistanceMetres, 100.0)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.Markup.Should().Contain("Edit mark");
        component.Find("#mark-name").GetAttribute("value").Should().Be("Legacy mark");

        component.Find("#mark-name").Input("Updated mark");
        component.Find("#mark-azimuth").Change("47.5");
        component.Find("#mark-distance").Change("125");
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeTrue();
            mediator.Verify(service => service.SendCommandAsync<UpdateMark.Command, UpdateMark.Response>(
                    It.Is<UpdateMark.Command>(command =>
                        command.MarkId == markId
                        && command.Name == "Updated mark"
                        && command.ExistingAzimuthDegrees == 45.0
                        && command.ExistingDistanceMetres == 100.0
                        && command.AzimuthDegrees == 47.5
                        && command.DistanceMetres == 125.0),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Fact]
    public void SubmitAsync_Should_ShowErrorAndStayOpen_When_CreateMarkFails()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<CreateMark.Command, CreateMark.Response>(
                It.IsAny<CreateMark.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateMark.Response { Result = CreateMark.Result.NoRhinoPosition });

        bool closed = false;
        var component = Render<MarkDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.Find("#mark-name").Input("Unplaced mark");
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeFalse();
            component.Markup.Should().Contain("Could not create mark: NoRhinoPosition.");
        });
    }

    [Fact]
    public void CancelAsync_Should_CloseWithoutDispatchingCommand_When_CancelIsClicked()
    {
        var mediator = RegisterMediator();
        bool closed = false;

        var component = Render<MarkDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        component.WaitForAssertion(() => closed.Should().BeTrue());
        mediator.Verify(service => service.SendCommandAsync<CreateMark.Command, CreateMark.Response>(
            It.IsAny<CreateMark.Command>(),
            It.IsAny<CancellationToken>()), Times.Never);
        mediator.Verify(service => service.SendCommandAsync<UpdateMark.Command, UpdateMark.Response>(
            It.IsAny<UpdateMark.Command>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private Mock<IMediator> RegisterMediator()
    {
        var mediator = new Mock<IMediator>();
        Services.AddSingleton(mediator.Object);
        return mediator;
    }
}
