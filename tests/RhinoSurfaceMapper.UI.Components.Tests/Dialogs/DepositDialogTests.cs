using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using RhinoSurfaceMapper.Application.Features.Markers;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.UI.Components.Dialogs;

namespace RhinoSurfaceMapper.UI.Components.Tests.Dialogs;

public sealed class DepositDialogTests : BunitContext
{
    [Fact]
    public void Dialog_Should_RenderNothing_When_IsOpenIsFalse()
    {
        var mediator = RegisterMediator();

        var component = Render<DepositDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, false));

        component.Markup.Trim().Should().BeEmpty();
        mediator.VerifyNoOtherCalls();
    }

    [Fact]
    public void Dialog_Should_RenderCreateContent_When_IsOpenIsTrue()
    {
        RegisterMediator();

        var component = Render<DepositDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true));

        component.Markup.Should().Contain("New deposit");
        component.Find("#deposit-name").Should().NotBeNull();
        component.Find("#deposit-size").Should().NotBeNull();
        component.Find("#deposit-rigs").Should().NotBeNull();
    }

    [Fact]
    public void SubmitAsync_Should_DispatchCreateDepositAndClose_When_CreatingDepositSucceeds()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<CreateDeposit.Command, CreateDeposit.Response>(
                It.IsAny<CreateDeposit.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateDeposit.Response { Result = CreateDeposit.Result.Success });

        bool closed = false;
        var component = Render<DepositDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.Find("#deposit-name").Input("Bauxite");
        component.Find("#deposit-size").Change(nameof(DepositSize.Grande));
        component.Find("#deposit-rigs").Change("4");
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeTrue();
            mediator.Verify(service => service.SendCommandAsync<CreateDeposit.Command, CreateDeposit.Response>(
                    It.Is<CreateDeposit.Command>(command =>
                        command.Name == "Bauxite"
                        && command.Size == DepositSize.Grande
                        && command.Rigs == 4),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Fact]
    public void SubmitAsync_Should_DispatchUpdateDepositAndClose_When_EditingDepositSucceeds()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<UpdateDeposit.Command, UpdateDeposit.Response>(
                It.IsAny<UpdateDeposit.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UpdateDeposit.Response { Result = UpdateDeposit.Result.Success });

        bool closed = false;
        Guid depositId = Guid.NewGuid();
        var component = Render<DepositDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.DepositId, depositId)
            .Add(dialog => dialog.InitialName, "Old deposit")
            .Add(dialog => dialog.InitialSize, DepositSize.Medio)
            .Add(dialog => dialog.InitialRigs, 2)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.Markup.Should().Contain("Edit deposit");
        component.Find("#deposit-name").GetAttribute("value").Should().Be("Old deposit");

        component.Find("#deposit-name").Input("Updated deposit");
        component.Find("#deposit-size").Change(nameof(DepositSize.Enorme));
        component.Find("#deposit-rigs").Change("6");
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeTrue();
            mediator.Verify(service => service.SendCommandAsync<UpdateDeposit.Command, UpdateDeposit.Response>(
                    It.Is<UpdateDeposit.Command>(command =>
                        command.DepositId == depositId
                        && command.Name == "Updated deposit"
                        && command.Size == DepositSize.Enorme
                        && command.Rigs == 6),
                    It.IsAny<CancellationToken>()),
                Times.Once);
        });
    }

    [Fact]
    public void SubmitAsync_Should_ShowErrorAndStayOpen_When_CreateDepositFails()
    {
        var mediator = RegisterMediator();
        mediator
            .Setup(service => service.SendCommandAsync<CreateDeposit.Command, CreateDeposit.Response>(
                It.IsAny<CreateDeposit.Command>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateDeposit.Response { Result = CreateDeposit.Result.TooClose });

        bool closed = false;
        var component = Render<DepositDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.Find("#deposit-name").Input("Crowded deposit");
        component.FindAll("button").Single(button => button.TextContent.Trim() == "Save").Click();

        component.WaitForAssertion(() =>
        {
            closed.Should().BeFalse();
            component.Markup.Should().Contain("Could not create deposit: TooClose.");
        });
    }

    [Fact]
    public void CancelAsync_Should_CloseWithoutDispatchingCommand_When_CancelIsClicked()
    {
        var mediator = RegisterMediator();
        bool closed = false;

        var component = Render<DepositDialog>(parameters => parameters
            .Add(dialog => dialog.IsOpen, true)
            .Add(dialog => dialog.OnClose, EventCallback.Factory.Create(this, () => closed = true)));

        component.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();

        component.WaitForAssertion(() => closed.Should().BeTrue());
        mediator.Verify(service => service.SendCommandAsync<CreateDeposit.Command, CreateDeposit.Response>(
            It.IsAny<CreateDeposit.Command>(),
            It.IsAny<CancellationToken>()), Times.Never);
        mediator.Verify(service => service.SendCommandAsync<UpdateDeposit.Command, UpdateDeposit.Response>(
            It.IsAny<UpdateDeposit.Command>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    private Mock<IMediator> RegisterMediator()
    {
        var mediator = new Mock<IMediator>();
        Services.AddSingleton(mediator.Object);
        return mediator;
    }
}
