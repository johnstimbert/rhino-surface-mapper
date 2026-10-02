using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.UI.Components.Dialogs;

namespace RhinoSurfaceMapper.UI.Components.Tests.Dialogs;

public sealed class SaveMapDialogTests : BunitContext
{
    [Fact]
    public void Dialog_Should_RenderNothing_When_IsOpenIsFalse()
    {
        var mediator = RegisterMediator();

        var component = Render<SaveMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, false));

        component.Markup.Trim().Should().BeEmpty();
        mediator.VerifyNoOtherCalls();
    }

    [Fact]
    public void Dialog_Should_RenderContent_When_IsOpenIsTrue()
    {
        RegisterMediator();

        var component = Render<SaveMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true));

        component.Markup.Should().Contain("Save map");
        component.Find("#save-mode").Should().NotBeNull();
    }

    [Fact]
    public void SubmitAsync_Should_DispatchReplaceSaveAndClose_When_DefaultModeSucceeds()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<SaveMap.Command, SaveMap.Response>(
                It.IsAny<SaveMap.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaveMap.Response { Result = SaveMap.Result.Success, Path = @"E:\maps\map.json" });

        bool closed = false;
        var component = Render<SaveMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeTrue();
            mediator.Verify(service => service.SendCommandAsync<SaveMap.Command, SaveMap.Response>(
                    It.Is<SaveMap.Command>(command =>
                        command.Mode == SaveMode.Replace
                        && command.ExplicitPath == null),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Fact]
    public void SubmitAsync_Should_DispatchExplicitPathSaveAndClose_When_ExplicitPathModeSucceeds()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<SaveMap.Command, SaveMap.Response>(
                It.IsAny<SaveMap.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaveMap.Response { Result = SaveMap.Result.Success, Path = @"E:\maps\explicit.json" });

        bool closed = false;
        var component = Render<SaveMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.Find("#save-mode").Change(nameof(SaveMode.ExplicitPath));
        component.Find("#save-path").Input(@"E:\maps\explicit");
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeTrue();
            mediator.Verify(service => service.SendCommandAsync<SaveMap.Command, SaveMap.Response>(
                    It.Is<SaveMap.Command>(command =>
                        command.Mode == SaveMode.ExplicitPath
                        && command.ExplicitPath == @"E:\maps\explicit"),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Fact]
    public void SubmitAsync_Should_ShowErrorAndStayOpen_When_SaveFails()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<SaveMap.Command, SaveMap.Response>(
                It.IsAny<SaveMap.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaveMap.Response { Result = SaveMap.Result.WriteFailed, Path = null });

        bool closed = false;
        var component = Render<SaveMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeFalse();
            component.Markup.Should().Contain("Could not save map: WriteFailed.");
        });
    }

    [Fact]
    public void CancelAsync_Should_CloseWithoutDispatchingCommand_When_CancelIsClicked()
    {
        var mediator = RegisterMediator();
        bool closed = false;

        var component = Render<SaveMapDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        component.WaitForAssertion(() => closed.Should().BeTrue());
        mediator.Verify(service => service.SendCommandAsync<SaveMap.Command, SaveMap.Response>(
            It.IsAny<SaveMap.Command>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private Mock<IMediator> RegisterMediator()
    {
        var mediator = new Mock<IMediator>();
        Services.AddSingleton(mediator.Object);
        return mediator;
    }
}
