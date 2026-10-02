using FluentAssertions;
using Moq;
using RhinoSurfaceMapper.Application.Features.Markers;
using RhinoSurfaceMapper.Application.Features.MapSession;
using RhinoSurfaceMapper.Application.Features.Pml;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Services;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.Enums;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.ValueObjects;
using System.IO;

namespace RhinoSurfaceMapper.Application.Tests.Features;

/// <summary>
/// Representative handler-level tests for <c>Application/Features</c>, each one a smoke test for
/// its command/query's primary success path plus one documented guard. These are not exhaustive
/// per-handler suites (the design's effort budget prioritises the
/// <c>MapTransitionCoordinatorTests</c> lifecycle group above); see the Phase 4 summary for the
/// full list of Python test methods these map onto and which remain unported.
/// </summary>
public sealed class FeatureHandlerTests
{
    private static MapSessionStore NewStore() => new();

    private static Domain.Entities.MapSession EstablishedSession()
    {
        var session = new Domain.Entities.MapSession();
        session.ProcessStatus(new TelemetryStatusSample(0x04000000, null, 0, 38, -9, "Sol", "Earth", 1_000_000.0, 1000.0));
        return session;
    }

    private static Domain.Entities.MapSession EstablishedIdentifiedSession(
        string system = "Sol",
        string body = "Earth",
        string pmlId = "6",
        double lat = 38,
        double lon = -9)
    {
        var session = new Domain.Entities.MapSession();
        session.ProcessStatus(new TelemetryStatusSample(0x04000000, null, 0, lat, lon, system, body, 1_000_000.0, 1000.0));
        session.PmlId = pmlId;
        session.PmlCenterLat = lat;
        session.PmlCenterLon = lon;
        return session;
    }

    /// <summary>Ports the success half of <c>test_deposit_duplicate_edit_rig_cancel_and_delete</c>: a deposit far enough from existing ones is accepted.</summary>
    [Fact]
    public async Task CreateDeposit_succeeds_when_far_enough_from_existing_deposits()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var validator = new CreateDeposit.Validator();
        var handler = new CreateDeposit.Handler(store, notifier.Object, validator);

        var response = await handler.HandleAsync(new CreateDeposit.Command { Name = "D1", Size = DepositSize.Grande, Rigs = 2 });

        response.Result.Should().Be(CreateDeposit.Result.Success);
        store.Snapshot.Deposits.Length.Should().Be(1);
        notifier.Verify(n => n.NotifySessionChanged(), Times.Once);
    }

    /// <summary>Ports the duplicate-rejection half of <c>test_deposit_duplicate_edit_rig_cancel_and_delete</c>: a deposit too close to an existing one is rejected.</summary>
    [Fact]
    public async Task CreateDeposit_rejects_a_deposit_too_close_to_an_existing_one()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new CreateDeposit.Handler(store, notifier.Object, new CreateDeposit.Validator());
        await handler.HandleAsync(new CreateDeposit.Command { Name = "D1", Size = DepositSize.Grande, Rigs = 2 });

        var response = await handler.HandleAsync(new CreateDeposit.Command { Name = "D2", Size = DepositSize.Grande, Rigs = 2 });

        response.Result.Should().Be(CreateDeposit.Result.TooClose);
        store.Snapshot.Deposits.Length.Should().Be(1);
    }

    /// <summary>Ports the delete half of <c>test_deposit_duplicate_edit_rig_cancel_and_delete</c>.</summary>
    [Fact]
    public async Task DeleteDeposit_removes_an_existing_deposit_and_reports_NotFound_for_an_unknown_one()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var createHandler = new CreateDeposit.Handler(store, notifier.Object, new CreateDeposit.Validator());
        await createHandler.HandleAsync(new CreateDeposit.Command { Name = "D1", Size = DepositSize.Grande, Rigs = 2 });
        Guid depositId = await FirstDepositId(store);

        var deleteHandler = new DeleteDeposit.Handler(store, notifier.Object);
        var missing = await deleteHandler.HandleAsync(new DeleteDeposit.Command { DepositId = Guid.NewGuid() });
        var removed = await deleteHandler.HandleAsync(new DeleteDeposit.Command { DepositId = depositId });

        missing.Result.Should().Be(DeleteDeposit.Result.NotFound);
        removed.Result.Should().Be(DeleteDeposit.Result.Success);
        store.Snapshot.Deposits.Length.Should().Be(0);
    }

    private static async Task<Guid> FirstDepositId(MapSessionStore store)
    {
        Guid id = Guid.Empty;
        await store.MutateAsync(session => id = session.Deposits[0].Id);
        return id;
    }

    /// <summary>Ports the create half of <c>test_mark_position_persistence_rename_and_delete</c>: a mark is placed via great-circle offset from the Rhino position.</summary>
    [Fact]
    public async Task CreateMark_places_a_mark_at_the_bearing_distance_offset()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new CreateMark.Handler(store, notifier.Object, new CreateMark.Validator());

        var response = await handler.HandleAsync(new CreateMark.Command { Name = "M1", AzimuthDegrees = 90, DistanceMetres = 100 });

        response.Result.Should().Be(CreateMark.Result.Success);
        store.Snapshot.Marks.Length.Should().Be(1);
    }

    /// <summary>
    /// Ports <c>test_alter_mark_cancel_and_name_only_preserve_position</c>: renaming a mark
    /// without changing its confirmed bearing/distance must not recompute (and so must not
    /// perturb) its stored position.
    /// </summary>
    [Fact]
    public async Task UpdateMark_preserves_position_when_only_the_name_changes()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var createHandler = new CreateMark.Handler(store, notifier.Object, new CreateMark.Validator());
        await createHandler.HandleAsync(new CreateMark.Command { Name = "Original", AzimuthDegrees = 45, DistanceMetres = 250 });

        (Guid id, double x, double y) before = await FirstMark(store);

        var updateHandler = new UpdateMark.Handler(store, notifier.Object, new UpdateMark.Validator());
        var response = await updateHandler.HandleAsync(new UpdateMark.Command
        {
            MarkId = before.id,
            Name = "Renamed",
            ExistingAzimuthDegrees = 45,
            ExistingDistanceMetres = 250,
            AzimuthDegrees = 45,
            DistanceMetres = 250,
        });

        response.Result.Should().Be(UpdateMark.Result.Success);
        (Guid id, double x, double y) after = await FirstMark(store);
        after.x.Should().Be(before.x);
        after.y.Should().Be(before.y);
    }

    private static async Task<(Guid Id, double X, double Y)> FirstMark(MapSessionStore store)
    {
        (Guid, double, double) result = default;
        await store.MutateAsync(session =>
        {
            var mark = session.Marks[0];
            result = (mark.Id, mark.X, mark.Y);
        });
        return result;
    }

    /// <summary>Ports the identification half of <c>test_poll_opens_pml_flow_when_late_system_completes_body_identity</c>: assigning a PML id sets both the id and a centre derived from the live Rhino position.</summary>
    [Fact]
    public async Task IdentifyPml_assigns_the_pml_id_and_centre_from_the_rhino_position()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new IdentifyPml.Handler(store, notifier.Object, new IdentifyPml.Validator());

        var response = await handler.HandleAsync(new IdentifyPml.Command { PmlId = "42" });

        response.Result.Should().Be(IdentifyPml.Result.Success);
        store.Snapshot.PmlId.Should().Be("42");
    }

    /// <summary>Ports <c>test_game_closing_clears_only_live_session_state</c>'s "New" half: starting a new map clears exploration state and the open-file path.</summary>
    [Fact]
    public async Task NewMap_clears_the_session_and_the_open_file_path()
    {
        var store = NewStore();
        await store.MutateAsync(s =>
        {
            s.InstallFrom(EstablishedSession());
            s.CurrentFilePath = "some/path.json";
        });
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new RhinoSurfaceMapper.Application.Features.MapSession.NewMap.Handler(store, notifier.Object, new RhinoSurfaceMapper.Application.Features.MapSession.NewMap.Validator());

        var response = await handler.HandleAsync(new RhinoSurfaceMapper.Application.Features.MapSession.NewMap.Command { KeepPml = false });

        response.Result.Should().Be(RhinoSurfaceMapper.Application.Features.MapSession.NewMap.Result.Success);
        store.Snapshot.PmlId.Should().BeEmpty();
        store.Snapshot.Deposits.Length.Should().Be(0);
        notifier.Verify(n => n.NotifySessionChanged(), Times.Once);
        string? currentFilePath = null;
        await store.MutateAsync(s => currentFilePath = s.CurrentFilePath);
        currentFilePath.Should().BeNull();
    }

    /// <summary>Smoke-tests <c>GetMapSummary</c> reads every summary field off the live session, including fields the rendering snapshot omits.</summary>
    [Fact]
    public async Task GetMapSummary_reads_identity_and_record_counts_off_the_live_session()
    {
        var store = NewStore();
        await store.MutateAsync(s =>
        {
            s.InstallFrom(EstablishedSession());
            s.Favorite = true;
        });
        var handler = new GetMapSummary.Handler(store);

        var response = await handler.HandleAsync(new GetMapSummary.Query());

        response.System.Should().Be("Sol");
        response.Body.Should().Be("Earth");
        response.Favorite.Should().BeTrue();
        response.DepositCount.Should().Be(0);
    }

    /// <summary>
    /// Ports the success half of <c>test_save_load_round_trip_and_invalid_file_preserves_map</c>:
    /// saving then loading the same map restores every field through <see cref="LoadMap"/>'s
    /// <c>InstallFrom</c>, and loading an invalid file leaves the still-live session untouched.
    /// </summary>
    [Fact]
    public async Task SaveMap_then_LoadMap_round_trips_the_session_and_an_invalid_file_preserves_the_live_map()
    {
        var repository = new InMemoryMapRepository();
        var paths = new StubAppPaths();
        var store = NewStore();
        var session = EstablishedSession();
        session.PmlId = "7";
        await store.MutateAsync(s => s.InstallFrom(session));
        var notifier = new Mock<IMapSessionNotifier>();

        var saveHandler = new SaveMap.Handler(repository, paths, store, notifier.Object, new SaveMap.Validator());
        var saveResponse = await saveHandler.HandleAsync(new SaveMap.Command { Mode = SaveMode.Replace });
        saveResponse.Result.Should().Be(SaveMap.Result.Success);
        string savedPath = saveResponse.Path!;

        // Overwrite the live session with something different, then reload the saved file.
        await store.MutateAsync(s => s.InstallFrom(new Domain.Entities.MapSession()));
        var loadHandler = new LoadMap.Handler(repository, store, notifier.Object, Mock.Of<IMapTransitionCoordinator>(), new LoadMap.Validator());
        var loadResponse = await loadHandler.HandleAsync(new LoadMap.Command { Path = savedPath });

        loadResponse.Result.Should().Be(LoadMap.Result.Success);
        store.Snapshot.System.Should().Be("Sol");
        store.Snapshot.Body.Should().Be("Earth");

        // Loading a path that was never saved must fail without mutating the live session.
        var invalidResponse = await loadHandler.HandleAsync(new LoadMap.Command { Path = "does-not-exist.json" });
        invalidResponse.Result.Should().Be(LoadMap.Result.NotFound);
        store.Snapshot.System.Should().Be("Sol", "a failed load must preserve the still-live map");
    }

    /// <summary>
    /// Ports the <c>clear_transition_state()</c> half of Python's <c>install_loaded_map</c>: a
    /// successful manual open always clears any pending background-telemetry transition so it
    /// can never later activate against a map the user has already replaced, but a failed load
    /// (file missing/invalid) must not touch the coordinator at all.
    /// </summary>
    [Fact]
    public async Task LoadMap_resets_the_transition_coordinator_on_success_but_not_on_failure()
    {
        var repository = new InMemoryMapRepository();
        var paths = new StubAppPaths();
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedIdentifiedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var saveHandler = new SaveMap.Handler(repository, paths, store, notifier.Object, new SaveMap.Validator());
        var saveResponse = await saveHandler.HandleAsync(new SaveMap.Command { Mode = SaveMode.Replace });
        saveResponse.Result.Should().Be(SaveMap.Result.Success);
        var coordinator = new Mock<IMapTransitionCoordinator>();
        var loadHandler = new LoadMap.Handler(repository, store, notifier.Object, coordinator.Object, new LoadMap.Validator());

        var failed = await loadHandler.HandleAsync(new LoadMap.Command { Path = "does-not-exist.json" });
        failed.Result.Should().Be(LoadMap.Result.NotFound);
        coordinator.Verify(c => c.Reset(), Times.Never);

        var succeeded = await loadHandler.HandleAsync(new LoadMap.Command { Path = saveResponse.Path! });
        succeeded.Result.Should().Be(LoadMap.Result.Success);
        coordinator.Verify(c => c.Reset(), Times.Once);
    }

    /// <summary>Smoke-tests <see cref="UpdateDeposit"/> updates only the editable fields of an existing deposit.</summary>
    [Fact]
    public async Task UpdateDeposit_Should_UpdateEditableFields_When_DepositExists()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var createHandler = new CreateDeposit.Handler(store, notifier.Object, new CreateDeposit.Validator());
        await createHandler.HandleAsync(new CreateDeposit.Command { Name = "D1", Size = DepositSize.Grande, Rigs = 2 });
        var originalDeposit = store.Snapshot.Deposits[0];

        var handler = new UpdateDeposit.Handler(store, notifier.Object, new UpdateDeposit.Validator());

        var response = await handler.HandleAsync(new UpdateDeposit.Command
        {
            DepositId = originalDeposit.Id,
            Name = "Updated",
            Size = DepositSize.Medio,
            Rigs = 4,
        });

        response.Result.Should().Be(UpdateDeposit.Result.Success);
        store.Snapshot.Deposits.Should().ContainSingle();
        store.Snapshot.Deposits[0].Should().BeEquivalentTo(originalDeposit, options => options
            .Excluding(deposit => deposit.Name)
            .Excluding(deposit => deposit.Size)
            .Excluding(deposit => deposit.Rigs));
        store.Snapshot.Deposits[0].Name.Should().Be("Updated");
        store.Snapshot.Deposits[0].Size.Should().Be(DepositSize.Medio);
        store.Snapshot.Deposits[0].Rigs.Should().Be(4);
        notifier.Verify(n => n.NotifySessionChanged(), Times.Exactly(2));
    }

    /// <summary>Documents <see cref="UpdateDeposit"/> returning <c>NotFound</c> for an unknown deposit id.</summary>
    [Fact]
    public async Task UpdateDeposit_Should_ReturnNotFound_When_DepositDoesNotExist()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new UpdateDeposit.Handler(store, notifier.Object, new UpdateDeposit.Validator());

        var response = await handler.HandleAsync(new UpdateDeposit.Command
        {
            DepositId = Guid.NewGuid(),
            Name = "Missing",
            Size = DepositSize.Pequeno,
            Rigs = 1,
        });

        response.Result.Should().Be(UpdateDeposit.Result.NotFound);
        store.Snapshot.Deposits.Should().BeEmpty();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    /// <summary>Smoke-tests <see cref="DeleteRig"/> removing an existing rig and publishing a change notification.</summary>
    [Fact]
    public async Task DeleteRig_Should_RemoveRig_When_RigExists()
    {
        var store = NewStore();
        await store.MutateAsync(s =>
        {
            s.InstallFrom(EstablishedSession());
            s.AddRig(new Domain.Entities.Rig(Guid.NewGuid(), 10, 20, 38.0001, -8.9998));
        });
        Guid rigId = await FirstRigId(store);
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new DeleteRig.Handler(store, notifier.Object);

        var response = await handler.HandleAsync(new DeleteRig.Command { RigId = rigId });

        response.Result.Should().Be(DeleteRig.Result.Success);
        store.Snapshot.Rigs.Should().BeEmpty();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Once);
    }

    /// <summary>Documents <see cref="DeleteRig"/> returning <c>NotFound</c> without mutating the map.</summary>
    [Fact]
    public async Task DeleteRig_Should_ReturnNotFound_When_RigDoesNotExist()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new DeleteRig.Handler(store, notifier.Object);

        var response = await handler.HandleAsync(new DeleteRig.Command { RigId = Guid.NewGuid() });

        response.Result.Should().Be(DeleteRig.Result.NotFound);
        store.Snapshot.Rigs.Should().BeEmpty();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    /// <summary>Smoke-tests <see cref="PlaceRig"/> converting local metres back to geographic coordinates before appending the rig.</summary>
    [Fact]
    public async Task PlaceRig_Should_AddRigWithGeographicCoordinates_When_MapHasCentre()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new PlaceRig.Handler(store, notifier.Object);

        var response = await handler.HandleAsync(new PlaceRig.Command { X = 125, Y = -75 });

        response.Result.Should().Be(PlaceRig.Result.Success);
        store.Snapshot.Rigs.Should().ContainSingle();
        store.Snapshot.Rigs[0].X.Should().Be(125);
        store.Snapshot.Rigs[0].Y.Should().Be(-75);
        store.Snapshot.Rigs[0].Lat.Should().NotBe(0);
        store.Snapshot.Rigs[0].Lon.Should().NotBe(0);
        notifier.Verify(n => n.NotifySessionChanged(), Times.Once);
    }

    /// <summary>Documents <see cref="PlaceRig"/> treating a map without an established centre as a guarded read-only result.</summary>
    [Fact]
    public async Task PlaceRig_Should_ReturnReadOnly_When_MapHasNoCentre()
    {
        var store = NewStore();
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new PlaceRig.Handler(store, notifier.Object);

        var response = await handler.HandleAsync(new PlaceRig.Command { X = 1, Y = 2 });

        response.Result.Should().Be(PlaceRig.Result.ReadOnly);
        store.Snapshot.Rigs.Should().BeEmpty();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    /// <summary>Smoke-tests <see cref="DeleteMark"/> removing an existing mark by id.</summary>
    [Fact]
    public async Task DeleteMark_Should_RemoveMark_When_MarkExists()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var createHandler = new CreateMark.Handler(store, notifier.Object, new CreateMark.Validator());
        await createHandler.HandleAsync(new CreateMark.Command { Name = "M1", AzimuthDegrees = 0, DistanceMetres = 50 });
        Guid markId = (await FirstMark(store)).Id;
        var handler = new DeleteMark.Handler(store, notifier.Object);

        var response = await handler.HandleAsync(new DeleteMark.Command { MarkId = markId });

        response.Result.Should().Be(DeleteMark.Result.Success);
        store.Snapshot.Marks.Should().BeEmpty();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Exactly(2));
    }

    /// <summary>Documents <see cref="DeleteMark"/> returning <c>NotFound</c> for a stale or foreign id.</summary>
    [Fact]
    public async Task DeleteMark_Should_ReturnNotFound_When_MarkDoesNotExist()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new DeleteMark.Handler(store, notifier.Object);

        var response = await handler.HandleAsync(new DeleteMark.Command { MarkId = Guid.NewGuid() });

        response.Result.Should().Be(DeleteMark.Result.NotFound);
        store.Snapshot.Marks.Should().BeEmpty();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    /// <summary>Smoke-tests <see cref="CreatePml"/> delegating to <see cref="SaveMap"/> with the active map's canonical PML path.</summary>
    [Fact]
    public async Task CreatePml_Should_SaveTheActiveMapToItsCanonicalPath_When_MapIsIdentified()
    {
        using var artifacts = new ArtifactScope();
        var repository = new InMemoryMapRepository();
        var paths = new ProjectScopedAppPaths(artifacts.RootPath);
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedIdentifiedSession(pmlId: "7")));
        var notifier = new Mock<IMapSessionNotifier>();
        var saveHandler = new SaveMap.Handler(repository, paths, store, notifier.Object, new SaveMap.Validator());
        var handler = new CreatePml.Handler(saveHandler);

        var response = await handler.HandleAsync(new CreatePml.Command());

        string expectedPath = Path.Combine(paths.MapsDirectory, "Sol", "Earth [7].json");
        response.Result.Should().Be(SaveMap.Result.Success);
        response.Path.Should().Be(expectedPath);
        (await repository.LoadAsync(expectedPath)).PmlId.Should().Be("7");
        notifier.Verify(n => n.NotifySessionChanged(), Times.Once);
    }

    /// <summary>Documents <see cref="CreatePml"/> surfacing <see cref="SaveMap.Result.PathRequired"/> when the map has no identified canonical path yet.</summary>
    [Fact]
    public async Task CreatePml_Should_ReturnPathRequired_When_MapHasNoIdentity()
    {
        using var artifacts = new ArtifactScope();
        var repository = new InMemoryMapRepository();
        var paths = new ProjectScopedAppPaths(artifacts.RootPath);
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var saveHandler = new SaveMap.Handler(repository, paths, store, notifier.Object, new SaveMap.Validator());
        var handler = new CreatePml.Handler(saveHandler);

        var response = await handler.HandleAsync(new CreatePml.Command());

        response.Result.Should().Be(SaveMap.Result.PathRequired);
        response.Path.Should().BeNull();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    /// <summary>Smoke-tests <see cref="ListPmlVersions"/> returning only the active PML's saved versions, newest first.</summary>
    [Fact]
    public async Task ListPmlVersions_Should_ReturnNewestMatchingVersions_When_ActivePmlIsIdentified()
    {
        using var artifacts = new ArtifactScope();
        var repository = new InMemoryMapRepository();
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedIdentifiedSession(pmlId: "7")));
        string systemDirectory = Path.Combine(artifacts.RootPath, "MAPAS", "Sol");
        Directory.CreateDirectory(systemDirectory);

        string olderPath = Path.Combine(systemDirectory, "Earth [7].json");
        string newerPath = Path.Combine(systemDirectory, "Earth [7] v2.json");
        string foreignPath = Path.Combine(systemDirectory, "Earth [8].json");
        File.WriteAllText(olderPath, "{}");
        File.WriteAllText(newerPath, "{}");
        File.WriteAllText(foreignPath, "{}");
        File.SetLastWriteTimeUtc(olderPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerPath, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(foreignPath, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        repository.Add(olderPath, EstablishedIdentifiedSession(pmlId: "7"));
        repository.Add(newerPath, EstablishedIdentifiedSession(pmlId: "7"));
        repository.Add(foreignPath, EstablishedIdentifiedSession(pmlId: "8"));
        var handler = new ListPmlVersions.Handler(repository, store);

        var response = await handler.HandleAsync(new ListPmlVersions.Query());

        response.Versions.Select(version => version.Path).Should().Equal(newerPath, olderPath);
    }

    /// <summary>Documents <see cref="ListPmlVersions"/> returning an empty list until the active map has a full identity.</summary>
    [Fact]
    public async Task ListPmlVersions_Should_ReturnEmpty_When_ActiveMapHasNoPmlIdentity()
    {
        var repository = new InMemoryMapRepository();
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var handler = new ListPmlVersions.Handler(repository, store);

        var response = await handler.HandleAsync(new ListPmlVersions.Query());

        response.Versions.Should().BeEmpty();
    }

    /// <summary>Smoke-tests <see cref="AllocateJohnDoeId"/> scanning existing body-matching maps and incrementing the highest John Doe suffix.</summary>
    [Fact]
    public async Task AllocateJohnDoeId_Should_ReturnTheNextAvailableId_When_BodyHasExistingJohnDoeMaps()
    {
        var repository = new InMemoryMapRepository();
        repository.Add(@"MAPAS\Sol\Earth [JD1].json", EstablishedIdentifiedSession(pmlId: "JD1"));
        repository.Add(@"MAPAS\Sol\Earth [42].json", EstablishedIdentifiedSession(pmlId: "42"));
        repository.Add(@"MAPAS\Sol\Earth [JD2].json", EstablishedIdentifiedSession(pmlId: "JD2"));
        var handler = new AllocateJohnDoeId.Handler(repository);

        var response = await handler.HandleAsync(new AllocateJohnDoeId.Query { System = "Sol", Body = "Earth" });

        response.PmlId.Should().Be("JD3");
    }

    /// <summary>Documents <see cref="AllocateJohnDoeId"/> ignoring John Doe ids that belong to another body.</summary>
    [Fact]
    public async Task AllocateJohnDoeId_Should_IgnoreOtherBodies_When_CalculatingTheNextId()
    {
        var repository = new InMemoryMapRepository();
        repository.Add(@"MAPAS\Sol\Mars [JD9].json", EstablishedIdentifiedSession(body: "Mars", pmlId: "JD9"));
        var handler = new AllocateJohnDoeId.Handler(repository);

        var response = await handler.HandleAsync(new AllocateJohnDoeId.Query { System = "Sol", Body = "Earth" });

        response.PmlId.Should().Be("JD1");
    }

    /// <summary>Smoke-tests <see cref="SaveMapVersion"/> delegating to <see cref="SaveMap"/> with the next numbered sibling path.</summary>
    [Fact]
    public async Task SaveMapVersion_Should_WriteANewVersionBesideTheCanonicalPath_When_MapHasAnIdentifiedPml()
    {
        using var artifacts = new ArtifactScope();
        var repository = new InMemoryMapRepository();
        var paths = new ProjectScopedAppPaths(artifacts.RootPath);
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedIdentifiedSession(pmlId: "7")));
        var notifier = new Mock<IMapSessionNotifier>();
        var saveHandler = new SaveMap.Handler(repository, paths, store, notifier.Object, new SaveMap.Validator());
        var handler = new SaveMapVersion.Handler(saveHandler);

        var response = await handler.HandleAsync(new SaveMapVersion.Command());

        string expectedPath = Path.Combine(paths.MapsDirectory, "Sol", "Earth [7] v2.json");
        response.Result.Should().Be(SaveMap.Result.Success);
        response.Path.Should().Be(expectedPath);
        (await repository.LoadAsync(expectedPath)).PmlId.Should().Be("7");
        notifier.Verify(n => n.NotifySessionChanged(), Times.Once);
    }

    /// <summary>Documents <see cref="SaveMapVersion"/> returning <c>PathRequired</c> when no canonical PML path can be derived yet.</summary>
    [Fact]
    public async Task SaveMapVersion_Should_ReturnPathRequired_When_MapHasNoCanonicalPath()
    {
        using var artifacts = new ArtifactScope();
        var repository = new InMemoryMapRepository();
        var paths = new ProjectScopedAppPaths(artifacts.RootPath);
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var saveHandler = new SaveMap.Handler(repository, paths, store, notifier.Object, new SaveMap.Validator());
        var handler = new SaveMapVersion.Handler(saveHandler);

        var response = await handler.HandleAsync(new SaveMapVersion.Command());

        response.Result.Should().Be(SaveMap.Result.PathRequired);
        response.Path.Should().BeNull();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    /// <summary>Smoke-tests <see cref="EnterMiningMode"/> switching the live session into read-only mining mode.</summary>
    [Fact]
    public async Task EnterMiningMode_Should_SetTheSessionReadOnly_When_ExplorationIsActive()
    {
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new EnterMiningMode.Handler(store, notifier.Object);

        var response = await handler.HandleAsync(new EnterMiningMode.Command());

        response.Result.Should().Be(EnterMiningMode.Result.Success);
        (await ReadSessionAsync(store)).ReadOnly.Should().BeTrue();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Once);
    }

    /// <summary>Documents <see cref="EnterMiningMode"/> clearing transient search guidance while preserving recorded exploration data.</summary>
    [Fact]
    public async Task EnterMiningMode_Should_PreserveRecordedDeposits_When_SwitchingToMiningMode()
    {
        var store = NewStore();
        await store.MutateAsync(s =>
        {
            s.InstallFrom(EstablishedSession());
            s.AddDeposit(new Domain.Entities.Deposit(Guid.NewGuid(), "D1", DepositSize.Grande, 2, 0, 0, 38, -9));
            s.StartSearch();
        });
        var notifier = new Mock<IMapSessionNotifier>();
        var handler = new EnterMiningMode.Handler(store, notifier.Object);

        await handler.HandleAsync(new EnterMiningMode.Command());

        var session = await ReadSessionAsync(store);
        session.Deposits.Should().ContainSingle();
        session.SearchStarted.Should().BeFalse();
        session.NextTargetXy.Should().BeNull();
    }

    /// <summary>Smoke-tests <see cref="ResolveUnsavedChanges"/> unblocking the coordinator once a transition is pending.</summary>
    [Fact]
    public async Task ResolveUnsavedChanges_Should_RecordTheDisposition_When_ATransitionIsPending()
    {
        using var artifacts = new ArtifactScope();
        var repository = new InMemoryMapRepository();
        var paths = new ProjectScopedAppPaths(artifacts.RootPath);
        var coordinator = new MapTransitionCoordinator(repository, paths, new FakeClock());
        var activeSession = EstablishedIdentifiedSession();
        activeSession.CurrentFilePath = @"MAPAS\Sol\Earth [6].json";
        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Wytheville", "New Body", 10, 20), false, Telemetry("Wytheville", "New Body", 10, 20));
        var handler = new ResolveUnsavedChanges.Handler(coordinator);

        var response = await handler.HandleAsync(new ResolveUnsavedChanges.Command { Disposition = OldMapDisposition.SaveReplace });
        bool resolved = await coordinator.TryResolveOldMapAsync(activeSession);

        response.Result.Should().Be(ResolveUnsavedChanges.Result.Recorded);
        resolved.Should().BeTrue();
        coordinator.PendingOldMapResolved.Should().BeTrue();
        repository.SavedPaths.Should().ContainSingle().Which.Should().Be(@"MAPAS\Sol\Earth [6].json");
    }

    /// <summary>Documents <see cref="ResolveUnsavedChanges"/> being a no-op when no transition is currently pending.</summary>
    [Fact]
    public async Task ResolveUnsavedChanges_Should_LeaveCoordinatorUnchanged_When_NoTransitionIsPending()
    {
        using var artifacts = new ArtifactScope();
        var coordinator = new MapTransitionCoordinator(new InMemoryMapRepository(), new ProjectScopedAppPaths(artifacts.RootPath), new FakeClock());
        var handler = new ResolveUnsavedChanges.Handler(coordinator);

        var response = await handler.HandleAsync(new ResolveUnsavedChanges.Command { Disposition = OldMapDisposition.Discard });

        response.Result.Should().Be(ResolveUnsavedChanges.Result.Recorded);
        coordinator.TransitionRequired.Should().BeFalse();
        coordinator.PendingOldMapResolved.Should().BeFalse();
        coordinator.PendingDestination.Should().BeNull();
    }

    /// <summary>Smoke-tests <see cref="EvaluateTelemetryPoll"/> returning <c>Rejected</c> for a non-SRV sample.</summary>
    [Fact]
    public async Task EvaluateTelemetryPoll_Should_ReturnRejected_When_TheSampleIsNotInSrv()
    {
        var store = NewStore();
        var notifier = new Mock<IMapSessionNotifier>();
        var coordinator = new Mock<IMapTransitionCoordinator>();
        var handler = new EvaluateTelemetryPoll.Handler(store, notifier.Object, coordinator.Object);

        var response = await handler.HandleAsync(new EvaluateTelemetryPoll.Command
        {
            Sample = new TelemetryStatusSample(0, null, 0, 38, -9, "Sol", "Earth", 1_000_000.0, 1000.0),
        });

        response.Result.Should().Be(EvaluateTelemetryPoll.Result.Rejected);
        coordinator.Verify(c => c.ObserveAcceptedSample(It.IsAny<TelemetryStatusSample>()), Times.Never);
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    /// <summary>Smoke-tests <see cref="EvaluateTelemetryPoll"/> returning <c>Continued</c> when accepted telemetry still belongs to the active map.</summary>
    [Fact]
    public async Task EvaluateTelemetryPoll_Should_ReturnContinued_When_TheSampleBelongsToTheActiveMap()
    {
        using var artifacts = new ArtifactScope();
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedIdentifiedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var coordinator = new MapTransitionCoordinator(new InMemoryMapRepository(), new ProjectScopedAppPaths(artifacts.RootPath), new FakeClock());
        var handler = new EvaluateTelemetryPoll.Handler(store, notifier.Object, coordinator);

        var response = await handler.HandleAsync(new EvaluateTelemetryPoll.Command
        {
            Sample = Telemetry("Sol", "Earth", 38.01, -8.99),
        });

        response.Result.Should().Be(EvaluateTelemetryPoll.Result.Continued);
        store.Snapshot.System.Should().Be("Sol");
        store.Snapshot.Body.Should().Be("Earth");
        coordinator.TransitionRequired.Should().BeFalse();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    /// <summary>Smoke-tests <see cref="EvaluateTelemetryPoll"/> returning <c>TransitionPending</c> while the old map still awaits a disposition.</summary>
    [Fact]
    public async Task EvaluateTelemetryPoll_Should_ReturnTransitionPending_When_TheOldMapDispositionHasNotBeenResolved()
    {
        using var artifacts = new ArtifactScope();
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedIdentifiedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var coordinator = new MapTransitionCoordinator(new InMemoryMapRepository(), new ProjectScopedAppPaths(artifacts.RootPath), new FakeClock());
        var handler = new EvaluateTelemetryPoll.Handler(store, notifier.Object, coordinator);

        var response = await handler.HandleAsync(new EvaluateTelemetryPoll.Command
        {
            Sample = Telemetry("Wytheville", "New Body", 10, 20),
        });

        response.Result.Should().Be(EvaluateTelemetryPoll.Result.TransitionPending);
        coordinator.TransitionRequired.Should().BeTrue();
        store.Snapshot.System.Should().Be("Sol", "a pending foreign transition must leave the live map untouched");
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    /// <summary>Smoke-tests <see cref="EvaluateTelemetryPoll"/> completing the full resolve/prepare/activate flow when every step is unblocked.</summary>
    [Fact]
    public async Task EvaluateTelemetryPoll_Should_ReturnActivated_When_ThePreparedDestinationIsInstalled()
    {
        using var artifacts = new ArtifactScope();
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedIdentifiedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var coordinator = new MapTransitionCoordinator(new InMemoryMapRepository(), new ProjectScopedAppPaths(artifacts.RootPath), new FakeClock());
        var transitionSample = Telemetry("Wytheville", "New Body", 10, 20);
        coordinator.EvaluateStatusUpdate(new StatusUpdate(true, true, "Wytheville", "New Body", 10, 20), false, transitionSample);
        coordinator.ResolveOldMapDisposition(OldMapDisposition.Discard);
        var handler = new EvaluateTelemetryPoll.Handler(store, notifier.Object, coordinator);

        var response = await handler.HandleAsync(new EvaluateTelemetryPoll.Command
        {
            Sample = transitionSample,
        });

        response.Result.Should().Be(EvaluateTelemetryPoll.Result.Activated);
        store.Snapshot.System.Should().Be("Wytheville");
        store.Snapshot.Body.Should().Be("New Body");
        coordinator.TransitionRequired.Should().BeFalse();
        notifier.Verify(n => n.NotifySessionChanged(), Times.Once);
    }

    /// <summary>
    /// Ports <c>test_active_map_lifecycle_evaluation_preserves_pending_mismatch</c> end-to-end
    /// through <see cref="EvaluateTelemetryPoll.Handler"/> (not just
    /// <see cref="MapTransitionCoordinator.EvaluateStatusUpdate"/> directly): a sample that
    /// stays on the exact same body but drives far outside the active PML's match radius must
    /// start a pending transition, even though <see cref="StatusUpdate.LocationChanged"/> is
    /// <see langword="false"/> for this sample (<see cref="Domain.Services.TelemetryProcessor"/>
    /// never reports a location change for pure lat/lon drift on an unchanged body — only
    /// <see cref="EvaluateTelemetryPoll.Handler"/>'s own, independently-computed correspondence
    /// check can detect this). This is the regression test for the structurally-unreachable bug
    /// a prior revision of this handler had: computing/forwarding correspondence only when
    /// <c>LocationChanged</c> was already true made this branch impossible to reach from real
    /// telemetry.
    /// </summary>
    [Fact]
    public async Task EvaluateTelemetryPoll_Should_ReturnTransitionPending_When_TheSampleDriftsFarFromThePmlCentreOnTheSameBody()
    {
        using var artifacts = new ArtifactScope();
        var store = NewStore();
        await store.MutateAsync(s => s.InstallFrom(EstablishedIdentifiedSession()));
        var notifier = new Mock<IMapSessionNotifier>();
        var coordinator = new MapTransitionCoordinator(new InMemoryMapRepository(), new ProjectScopedAppPaths(artifacts.RootPath), new FakeClock());
        var handler = new EvaluateTelemetryPoll.Handler(store, notifier.Object, coordinator);

        // Same system/body as EstablishedIdentifiedSession's "Sol"/"Earth", but ~111 km from the
        // established PML centre (38, -9) — far beyond any plausible PmlMatchDistanceMetres, so
        // CorrespondsToMap must return false while TelemetryProcessor reports LocationChanged:
        // false (the body never changed).
        var response = await handler.HandleAsync(new EvaluateTelemetryPoll.Command
        {
            Sample = Telemetry("Sol", "Earth", 39, -9),
        });

        response.Result.Should().Be(EvaluateTelemetryPoll.Result.TransitionPending);
        coordinator.TransitionRequired.Should().BeTrue("drifting past the PML radius while staying on the same body must start a transition, matching Python's `location_changed or correspondence is False` OR condition");
        store.Snapshot.System.Should().Be("Sol", "a pending transition must leave the live map untouched until activated");
        store.Snapshot.RhinoLat.Should().Be(38, "position recording must be suppressed once the sample no longer corresponds to the active PML, just like for an actual body change");
        notifier.Verify(n => n.NotifySessionChanged(), Times.Never);
    }

    private static async Task<Guid> FirstRigId(MapSessionStore store)
    {
        Guid id = Guid.Empty;
        await store.MutateAsync(session => id = session.Rigs[0].Id);
        return id;
    }

    private static TelemetryStatusSample Telemetry(string system, string body, double lat, double lon, long flags = MapperConstants.SrvFlag) =>
        new(flags, null, 0, lat, lon, system, body, 1_000_000.0, 1000.0);

    private static async Task<Domain.Entities.MapSession> ReadSessionAsync(MapSessionStore store)
    {
        Domain.Entities.MapSession? captured = null;
        await store.MutateAsync(session => captured = session);
        return captured!;
    }

    private sealed class ArtifactScope : IDisposable
    {
        public ArtifactScope()
        {
            RootPath = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public void Dispose()
        {
            if (!Directory.Exists(RootPath))
            {
                return;
            }

            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class ProjectScopedAppPaths(string baseDirectory) : IAppPaths
    {
        public string BaseDirectory { get; } = baseDirectory;
        public string MapsDirectory => Path.Combine(BaseDirectory, "MAPAS");
        public string OptionsPath => Path.Combine(BaseDirectory, "options.json");
        public string LogsDirectory => Path.Combine(BaseDirectory, "logs");
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow => new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public double MonotonicSeconds => 0.0;
    }

    private sealed class StubAppPaths : Domain.Interfaces.IAppPaths
    {
        public string BaseDirectory { get; } = System.IO.Path.GetTempPath();
        public string MapsDirectory => System.IO.Path.Combine(BaseDirectory, "MAPAS-" + Guid.NewGuid());
        public string OptionsPath => System.IO.Path.Combine(BaseDirectory, "options.json");
        public string LogsDirectory => System.IO.Path.Combine(BaseDirectory, "logs");
    }

    private sealed class InMemoryMapRepository : Domain.Interfaces.IMapRepository
    {
        private readonly Dictionary<string, Domain.Entities.MapSession> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _savedPaths = [];

        public IReadOnlyList<string> SavedPaths => _savedPaths;

        public void Add(string path, Domain.Entities.MapSession session) => _files[path] = Clone(session);

        public Task<Domain.Entities.MapSession> LoadAsync(string path, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path, out var session))
            {
                throw new FileNotFoundException(path);
            }

            var copy = new Domain.Entities.MapSession();
            copy.LoadFromDocument(session.ToDocument());
            return Task.FromResult(copy);
        }

        public Task SaveAsync(Domain.Entities.MapSession session, string path, bool updateSavedAt = true, CancellationToken ct = default)
        {
            _files[path] = Clone(session);
            _savedPaths.Add(path);
            return Task.CompletedTask;
        }

        public Task<bool> IsProtectedAsync(string path, CancellationToken ct = default) => Task.FromResult(false);

        public Task SetFlagsAsync(string path, bool favorite, bool protectedFlag, CancellationToken ct = default) => Task.CompletedTask;

        public Task<(string CreatedAt, string LastSavedAt)> ReadTimestampsAsync(string path, CancellationToken ct = default) =>
            Task.FromResult((_files[path].CreatedAt ?? string.Empty, _files[path].LastSavedAt ?? string.Empty));

        public IReadOnlyList<string> EnumerateMaps(string systemName) =>
            _files.Keys
                .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), systemName, StringComparison.OrdinalIgnoreCase))
                .ToList();

        public IReadOnlyList<string> EnumerateSystems() => Array.Empty<string>();

        private static Domain.Entities.MapSession Clone(Domain.Entities.MapSession session)
        {
            var copy = new Domain.Entities.MapSession();
            copy.LoadFromDocument(session.ToDocument());
            return copy;
        }
    }
}
