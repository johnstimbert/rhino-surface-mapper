using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Application.Services;
using RhinoSurfaceMapper.UI.Components.Dialogs;

namespace RhinoSurfaceMapper.UI.Components.Tests.Dialogs;

public sealed class ProtectedMapDialogTests : BunitContext
{
    [Fact]
    public void Dialog_Should_RenderNothing_When_NoTransitionIsPending()
    {
        RegisterServices(transitionRequired: false, pendingOldMapResolved: false, out _);

        var component = Render<ProtectedMapDialog>();

        component.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Dialog_Should_RenderContent_When_TransitionRequiresResolution()
    {
        RegisterServices(transitionRequired: true, pendingOldMapResolved: false, out _);

        var component = Render<ProtectedMapDialog>();

        component.Markup.Should().Contain("Leaving the current map");
        component.Markup.Should().Contain("Discard");
        component.Markup.Should().Contain("Save (replace)");
        component.Markup.Should().Contain("Save as new version");
        component.Markup.Should().Contain("Not now");
    }

    [Theory]
    [InlineData("Discard", OldMapDisposition.Discard)]
    [InlineData("Save (replace)", OldMapDisposition.SaveReplace)]
    [InlineData("Save as new version", OldMapDisposition.SaveNewVersion)]
    public void ResolveAsync_Should_DispatchSelectedDispositionAndHide_When_ActionIsClicked(string buttonText, OldMapDisposition disposition)
    {
        var mediator = RegisterServices(transitionRequired: true, pendingOldMapResolved: false, out _);
        mediator
            .Setup(service => service.SendCommandAsync<ResolveUnsavedChanges.Command, ResolveUnsavedChanges.Response>(
                It.IsAny<ResolveUnsavedChanges.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolveUnsavedChanges.Response { Result = ResolveUnsavedChanges.Result.Recorded });

        var component = Render<ProtectedMapDialog>();

        component.FindAll("button").Single(button => button.TextContent.Trim() == buttonText).Click();

        component.WaitForAssertion(() =>
        {
            component.Markup.Should().NotContain("Leaving the current map");
            mediator.Verify(service => service.SendCommandAsync<ResolveUnsavedChanges.Command, ResolveUnsavedChanges.Response>(
                    It.Is<ResolveUnsavedChanges.Command>(command => command.Disposition == disposition),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Fact]
    public void Dismiss_Should_HideWithoutDispatchingCommand_When_NotNowIsClicked()
    {
        var mediator = RegisterServices(transitionRequired: true, pendingOldMapResolved: false, out _);

        var component = Render<ProtectedMapDialog>();

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Not now").Click();

        component.WaitForAssertion(() => component.Markup.Should().NotContain("Leaving the current map"));
        mediator.Verify(service => service.SendCommandAsync<ResolveUnsavedChanges.Command, ResolveUnsavedChanges.Response>(
            It.IsAny<ResolveUnsavedChanges.Command>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private Mock<IMediator> RegisterServices(bool transitionRequired, bool pendingOldMapResolved, out MapSessionNotifier notifier)
    {
        var mediator = new Mock<IMediator>();
        var coordinator = new Mock<IMapTransitionCoordinator>();
        notifier = new MapSessionNotifier();

        coordinator.SetupGet(service => service.TransitionRequired).Returns(transitionRequired);
        coordinator.SetupGet(service => service.PendingOldMapResolved).Returns(pendingOldMapResolved);

        Services.AddSingleton(mediator.Object);
        Services.AddSingleton(coordinator.Object);
        Services.AddSingleton<IMapSessionNotifier>(notifier);
        return mediator;
    }
}
