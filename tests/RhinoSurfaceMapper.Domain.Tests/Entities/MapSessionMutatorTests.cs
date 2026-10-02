using FluentAssertions;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Domain.Tests.Entities;

/// <summary>
/// Covers the direct collection-mutation contracts of <see cref="MapSession"/>'s deposit, rig
/// and mark mutators without involving document round-tripping or application-layer policies.
/// </summary>
public sealed class MapSessionMutatorTests
{
    [Fact]
    public void UpdateDeposit_replaces_only_editable_fields_and_preserves_position()
    {
        // Arrange
        var session = new MapSession();
        Guid depositId = Guid.NewGuid();
        session.AddDeposit(new Deposit(depositId, "Old", DepositSize.Pequeno, 1, 12.5, 34.5, 38.1, -9.2));

        // Act
        bool result = session.UpdateDeposit(depositId, "New", DepositSize.Enorme, 6);

        // Assert
        result.Should().BeTrue();
        session.Deposits.Should().ContainSingle();
        session.Deposits[0].Should().Be(new Deposit(depositId, "New", DepositSize.Enorme, 6, 12.5, 34.5, 38.1, -9.2));
    }

    [Fact]
    public void UpdateDeposit_returns_false_for_unknown_id_and_leaves_existing_deposits_unchanged()
    {
        // Arrange
        var session = new MapSession();
        var existing = new Deposit(Guid.NewGuid(), "Keep", DepositSize.Grande, 3, 10, 20, 38, -9);
        session.AddDeposit(existing);

        // Act
        bool result = session.UpdateDeposit(Guid.NewGuid(), "Ignored", DepositSize.Medio, 2);

        // Assert
        result.Should().BeFalse();
        session.Deposits.Should().ContainSingle().Which.Should().Be(existing);
    }

    [Fact]
    public void DeleteDeposit_removes_only_the_matching_deposit()
    {
        // Arrange
        var session = new MapSession();
        var kept = new Deposit(Guid.NewGuid(), "Keep", DepositSize.Pequeno, 1, 1, 2, 38.1, -9.1);
        var removed = new Deposit(Guid.NewGuid(), "Remove", DepositSize.Enorme, 5, 3, 4, 38.2, -9.2);
        session.AddDeposit(kept);
        session.AddDeposit(removed);

        // Act
        bool result = session.DeleteDeposit(removed.Id);

        // Assert
        result.Should().BeTrue();
        session.Deposits.Should().ContainSingle().Which.Should().Be(kept);
    }

    [Fact]
    public void DeleteDeposit_returns_false_for_unknown_id_and_leaves_existing_deposits_unchanged()
    {
        // Arrange
        var session = new MapSession();
        var first = new Deposit(Guid.NewGuid(), "A", DepositSize.Medio, 2, 1, 2, 38.1, -9.1);
        var second = new Deposit(Guid.NewGuid(), "B", DepositSize.Grande, 4, 3, 4, 38.2, -9.2);
        session.AddDeposit(first);
        session.AddDeposit(second);

        // Act
        bool result = session.DeleteDeposit(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
        session.Deposits.Should().BeEquivalentTo(new[] { first, second }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void DeleteRig_removes_only_the_matching_rig()
    {
        // Arrange
        var session = new MapSession();
        var kept = new Rig(Guid.NewGuid(), 10, 20, 38.1, -9.1);
        var removed = new Rig(Guid.NewGuid(), 30, 40, 38.2, -9.2);
        session.AddRig(kept);
        session.AddRig(removed);

        // Act
        bool result = session.DeleteRig(removed.Id);

        // Assert
        result.Should().BeTrue();
        session.Rigs.Should().ContainSingle().Which.Should().Be(kept);
    }

    [Fact]
    public void DeleteRig_returns_false_for_unknown_id_and_leaves_existing_rigs_unchanged()
    {
        // Arrange
        var session = new MapSession();
        var first = new Rig(Guid.NewGuid(), 10, 20, 38.1, -9.1);
        var second = new Rig(Guid.NewGuid(), 30, 40, 38.2, -9.2);
        session.AddRig(first);
        session.AddRig(second);

        // Act
        bool result = session.DeleteRig(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
        session.Rigs.Should().BeEquivalentTo(new[] { first, second }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void UpdateMark_replaces_name_and_position_fields()
    {
        // Arrange
        var session = new MapSession();
        Guid markId = Guid.NewGuid();
        session.AddMark(new MapMark(markId, "Old", 10, 20, 38.1, -9.1));

        // Act
        bool result = session.UpdateMark(markId, "New", 100, 200, 39.1, -8.1);

        // Assert
        result.Should().BeTrue();
        session.Marks.Should().ContainSingle();
        session.Marks[0].Should().Be(new MapMark(markId, "New", 100, 200, 39.1, -8.1));
    }

    [Fact]
    public void UpdateMark_returns_false_for_unknown_id_and_leaves_existing_marks_unchanged()
    {
        // Arrange
        var session = new MapSession();
        var existing = new MapMark(Guid.NewGuid(), "Keep", 10, 20, 38.1, -9.1);
        session.AddMark(existing);

        // Act
        bool result = session.UpdateMark(Guid.NewGuid(), "Ignored", 100, 200, 39.1, -8.1);

        // Assert
        result.Should().BeFalse();
        session.Marks.Should().ContainSingle().Which.Should().Be(existing);
    }

    [Fact]
    public void DeleteMark_removes_only_the_matching_mark()
    {
        // Arrange
        var session = new MapSession();
        var kept = new MapMark(Guid.NewGuid(), "Keep", 10, 20, 38.1, -9.1);
        var removed = new MapMark(Guid.NewGuid(), "Remove", 30, 40, 38.2, -9.2);
        session.AddMark(kept);
        session.AddMark(removed);

        // Act
        bool result = session.DeleteMark(removed.Id);

        // Assert
        result.Should().BeTrue();
        session.Marks.Should().ContainSingle().Which.Should().Be(kept);
    }

    [Fact]
    public void DeleteMark_returns_false_for_unknown_id_and_leaves_existing_marks_unchanged()
    {
        // Arrange
        var session = new MapSession();
        var first = new MapMark(Guid.NewGuid(), "A", 10, 20, 38.1, -9.1);
        var second = new MapMark(Guid.NewGuid(), "B", 30, 40, 38.2, -9.2);
        session.AddMark(first);
        session.AddMark(second);

        // Act
        bool result = session.DeleteMark(Guid.NewGuid());

        // Assert
        result.Should().BeFalse();
        session.Marks.Should().BeEquivalentTo(new[] { first, second }, options => options.WithStrictOrdering());
    }

    /// <summary>
    /// Ports <c>test_game_closing_clears_only_live_session_state</c>: <see cref="MapSession.SetOffline"/>
    /// resets only live-telemetry fields (in-SRV flag, fuel, Rhino position/heading) and never
    /// touches persistent map identity or recorded deposits/marks/rigs/trail.
    /// </summary>
    [Fact]
    public void SetOffline_clears_only_live_telemetry_fields_and_preserves_persistent_map_state()
    {
        // Arrange
        var session = new MapSession();
        session.ProcessStatus(new TelemetryStatusSample(
            Flags: MapperConstants.SrvFlag,
            FuelReservoir: 0.4,
            Heading: 90,
            Latitude: 38,
            Longitude: -9,
            StarSystem: "Teste",
            BodyName: "Test",
            PlanetRadius: 1_000_000,
            Timestamp: 1000));
        session.PmlId = "PML-1";
        session.AddDeposit(new Deposit(Guid.NewGuid(), "Deposit", DepositSize.Pequeno, 1, 0, 0, 38, -9));
        var persistentBefore = (session.System, session.Body, session.BodyKey, session.PmlId, session.Deposits.Count);

        // Act
        session.SetOffline();

        // Assert
        session.InSrv.Should().BeFalse();
        session.FuelReservoir.Should().BeNull();
        session.FuelPercent.Should().BeNull();
        session.FuelLow.Should().BeFalse();
        session.RhinoLat.Should().BeNull();
        session.RhinoLon.Should().BeNull();
        session.RhinoHeading.Should().BeNull();

        (session.System, session.Body, session.BodyKey, session.PmlId, session.Deposits.Count).Should().Be(persistentBefore);
    }
}
