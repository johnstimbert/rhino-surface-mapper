using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.UI.Components.Dialogs;

namespace RhinoSurfaceMapper.UI.Components.Tests.Dialogs;

public sealed class NewMapDialogTests : BunitContext
{
    [Fact]
    public void Dialog_Should_RenderNothing_When_IsOpenIsFalse()
    {
        var mediator = RegisterMediator();

        var component = Render<NewMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, false));

        component.Markup.Trim().Should().BeEmpty();
        mediator.VerifyNoOtherCalls();
    }

    [Fact]
    public void Dialog_Should_RenderContent_When_IsOpenIsTrue()
    {
        RegisterMediator();

        var component = Render<NewMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true));

        component.Markup.Should().Contain("Start a new map?");
        component.Markup.Should().Contain("Keep the current PML identity");
    }

    [Fact]
    public void SubmitAsync_Should_DispatchNewMapAndClose_When_Submitted()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<NewMap.Command, NewMap.Response>(
                It.IsAny<NewMap.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NewMap.Response { Result = NewMap.Result.Success });

        bool closed = false;
        var component = Render<NewMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.Find("input[type='checkbox']").Change(true);
        component.FindAll("button").Single(button => button.TextContent.Trim() == "New map").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeTrue();
            mediator.Verify(service => service.SendCommandAsync<NewMap.Command, NewMap.Response>(
                    It.Is<NewMap.Command>(command => command.KeepPml),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Fact]
    public void CancelAsync_Should_CloseWithoutDispatchingCommand_When_CancelIsClicked()
    {
        var mediator = RegisterMediator();
        bool closed = false;

        var component = Render<NewMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        component.WaitForAssertion(() => closed.Should().BeTrue());
        mediator.Verify(service => service.SendCommandAsync<NewMap.Command, NewMap.Response>(
            It.IsAny<NewMap.Command>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private Mock<IMediator> RegisterMediator()
    {
        var mediator = new Mock<IMediator>();
        Services.AddSingleton(mediator.Object);
        return mediator;
    }
}
