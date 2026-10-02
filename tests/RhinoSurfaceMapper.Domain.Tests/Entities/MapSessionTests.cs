using FluentAssertions;
using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.Tests.TestSupport;

namespace RhinoSurfaceMapper.Domain.Tests.Entities;

/// <summary>
/// Ports the behavioural invariants of <c>python/tests/test_mapper_core.py</c> that exercise
/// <see cref="MapSession"/> end-to-end (telemetry acceptance, body/system-change handling,
/// search-route interaction, coordinate round-tripping, PML metadata lifecycle, and the
/// load/save document round-trip via <see cref="MapSession.ToDocument"/>/<see cref="MapSession.LoadFromDocument"/>
/// rather than real file I/O, since persistence is a Phase 2 concern).
/// </summary>
public sealed class MapSessionTests
{
    [Fact]
    public void Status_creates_a_position_and_normalizes_heading()
    {
        var session = new MapSession();

        var result = session.ProcessStatus(TelemetrySamples.Status(heading: 361.0));

        result.Accepted.Should().BeTrue();
        session.RhinoHeading.Should().Be(1.0);
        session.Points.Should().HaveCount(1);
        session.FuelPercent.Should().Be(100.0);
    }

    [Fact]
    public void Status_ignores_non_srv_telemetry()
    {
        var session = new MapSession();

        var result = session.ProcessStatus(TelemetrySamples.Status(flags: 0));

        result.Accepted.Should().BeFalse();
        session.Points.Should().BeEmpty();
    }

    [Fact]
    public void Status_reports_body_change_without_resetting_active_map()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.PmlId = "6";
        session.AddMark(new MapMark(Guid.NewGuid(), "Centro [6]", 12, 34, 38, -9));

        var result = session.ProcessStatus(TelemetrySamples.Status(bodyName: "A 2", latitude: 39.0, longitude: -8.0));

        result.Accepted.Should().BeTrue();
        result.LocationChanged.Should().BeTrue();
        (result.System, result.Body, result.Latitude, result.Longitude).Should().Be(("Teste", "A 2", 39.0, -8.0));
        // A mere report of a different body/system must not reset the active map.
        session.System.Should().Be("Teste");
        session.Body.Should().Be("A 1");
        session.PmlId.Should().Be("6");
        session.Marks.Should().HaveCount(1);
    }

    [Fact]
    public void Status_reports_system_change_without_resetting_active_map()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.PmlId = "JD1";

        var result = session.ProcessStatus(TelemetrySamples.Status(starSystem: "Outro"));

        result.LocationChanged.Should().BeTrue();
        (result.System, result.Body).Should().Be(("Outro", "A 1"));
        session.System.Should().Be("Teste");
        session.Body.Should().Be("A 1");
        session.PmlId.Should().Be("JD1");
    }

    [Fact]
    public void Non_srv_status_preserves_active_map()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.PmlId = "6";

        var result = session.ProcessStatus(TelemetrySamples.Status(flags: 0));

        result.Accepted.Should().BeFalse();
        result.LocationChanged.Should().BeFalse();
        (session.System, session.Body, session.PmlId).Should().Be(("Teste", "A 1", "6"));
    }

    [Fact]
    public void Changed_location_can_be_processed_without_mutating_position_or_trail()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        var oldPosition = (session.RhinoLat, session.RhinoLon);
        var oldPoints = session.Points.ToList();

        var result = session.ProcessStatus(TelemetrySamples.Status(latitude: 39.0, longitude: -8.0), recordPosition: false);

        result.Accepted.Should().BeTrue();
        (session.RhinoLat, session.RhinoLon).Should().Be(oldPosition);
        session.Points.Should().BeEquivalentTo(oldPoints);
    }

    [Fact]
    public void Missing_system_keeps_the_known_system_on_the_same_planet()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.AddMark(new MapMark(Guid.NewGuid(), "Centro [6]", 0, 0, 38.0, -9.0));

        session.ProcessStatus(TelemetrySamples.Status(starSystem: "", latitude: 38.001));

        session.System.Should().Be("Teste");
        session.Body.Should().Be("A 1");
        session.Marks.Should().HaveCount(1);
    }

    [Fact]
    public void Late_system_completes_same_body_identity_without_resetting_map()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status(starSystem: ""));
        session.PmlId = "6";
        session.AddDeposit(new Deposit(Guid.NewGuid(), "A", Enums.DepositSize.Grande, 3, 0, 0, 38, -9));
        session.AddMark(new MapMark(Guid.NewGuid(), "Keep", 0, 0, 38, -9));
        var pointsBefore = session.Points.ToList();
        var depositsBefore = session.Deposits.ToList();
        var marksBefore = session.Marks.ToList();

        var result = session.ProcessStatus(TelemetrySamples.Status());

        result.Accepted.Should().BeTrue();
        result.LocationChanged.Should().BeFalse();
        (session.System, session.Body, session.BodyKey).Should().Be(("Teste", "A 1", "Teste|A 1"));
        session.Points.Should().BeEquivalentTo(pointsBefore);
        session.Deposits.Should().BeEquivalentTo(depositsBefore);
        session.Marks.Should().BeEquivalentTo(marksBefore);
        session.PmlId.Should().Be("6");
    }

    [Fact]
    public void Search_starts_north_of_datum_and_can_skip()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());

        session.StartSearch().Should().BeTrue();
        var (targetX, targetY) = session.NextTargetXy!.Value;
        targetX.Should().BeApproximately(0.0, 1e-6);
        targetY.Should().BeApproximately(3_500.0, 1e-6);

        session.SkipNext().Should().BeTrue();
        session.RouteHistory[^1].Status.Should().Be(Enums.RouteStatus.Skipped);
        session.RouteHistory[^1].Number.Should().Be(1);
    }

    [Fact]
    public void Coordinate_conversion_round_trips()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());

        var (x, y) = session.LocalFromGeographic(38.01, -8.98);
        var (lat, lon) = session.GeographicFromLocal(x, y);

        lat.Should().BeApproximately(38.01, 1e-10);
        lon.Should().BeApproximately(-8.98, 1e-10);
    }

    [Fact]
    public void Pml_metadata_is_saved_loaded_and_reset_with_a_new_map()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.PmlId = "6";
        session.PmlCenterLat = 38.01;
        session.PmlCenterLon = -9.02;

        var document = session.ToDocument();
        var loaded = new MapSession();
        loaded.LoadFromDocument(document);

        loaded.PmlId.Should().Be("6");
        (loaded.PmlCenterLat, loaded.PmlCenterLon).Should().Be((38.01, -9.02));

        loaded.NewMap();

        loaded.PmlId.Should().Be(string.Empty);
        loaded.PmlCenterLat.Should().BeNull();
    }

    [Fact]
    public void New_map_can_keep_the_current_pml()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.PmlId = "6";
        session.PmlCenterLat = 38.01;
        session.PmlCenterLon = -9.02;

        session.NewMap(keepPml: true);

        session.PmlId.Should().Be("6");
        (session.PmlCenterLat, session.PmlCenterLon).Should().Be((38.01, -9.02));
    }

    [Fact]
    public void Old_map_can_record_dates_from_its_file_properties_via_populate_missing_timestamps()
    {
        // Ported from test_old_map_can_record_dates_from_its_file_properties, adapted: reading
        // file creation/modification time is a Phase 2 IMapRepository concern, so this test
        // supplies already-read timestamps directly instead of touching the filesystem.
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        var document = session.ToDocument();
        var loaded = new MapSession();
        loaded.LoadFromDocument(document);

        loaded.CreatedAt.Should().BeNull();
        loaded.PopulateMissingTimestamps("2020-01-01T00:00:00Z", "2020-01-02T00:00:00Z").Should().BeTrue();

        loaded.CreatedAt.Should().Be("2020-01-01T00:00:00Z");
        loaded.LastSavedAt.Should().Be("2020-01-02T00:00:00Z");
    }

    [Fact]
    public void Mining_mode_moves_rhino_without_recording_and_refuses_search_mutations()
    {
        // Ported from the pure-rule core of test_map_protection.py's
        // test_mining_moves_rhino_without_recording_and_refuses_all_saves. That Python test is
        // otherwise a disk-I/O and RadarPulse/UI scenario entirely out of Phase 1 scope (saving
        // to a protected path, radar tick accumulation); only the mining-only gating invariant
        // — telemetry still updates live position but records no trail, and search mutators
        // refuse to run — is extracted here.
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.EnterMiningMode();
        int pointsBefore = session.Points.Count;

        var result = session.ProcessStatus(TelemetrySamples.Status(longitude: -8.999));

        result.Accepted.Should().BeTrue();
        session.RhinoLon.Should().Be(-8.999);
        session.Points.Should().HaveCount(pointsBefore);
        session.StartSearch().Should().BeFalse();
        session.SkipNext().Should().BeFalse();
    }

    [Fact]
    public void New_map_increments_the_generation_counter_on_every_call()
    {
        var session = new MapSession();
        int initial = session.MapGeneration;

        session.NewMap();
        session.MapGeneration.Should().Be(initial + 1);

        session.NewMap(keepPml: true);
        session.MapGeneration.Should().Be(initial + 2);
    }

    [Fact]
    public void Prepare_for_save_throws_when_mining_only()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.EnterMiningMode();

        var act = () => session.PrepareForSave(new FakeClock());

        act.Should().Throw<MapSessionReadOnlyException>();
    }

    [Fact]
    public void Prepare_for_save_does_not_invent_a_creation_date_on_first_save()
    {
        // A map saved for the first time (CreatedAt still null) must not have LastSavedAt stamped
        // here either — that invariant is PopulateMissingTimestamps' job, not PrepareForSave's.
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());

        session.PrepareForSave(new FakeClock());

        session.CreatedAt.Should().BeNull();
        session.LastSavedAt.Should().BeNull();
    }

    [Fact]
    public void Prepare_for_save_stamps_last_saved_at_for_an_already_created_map()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.CreatedAt = "2020-01-01T00:00:00Z";
        var clock = new FakeClock(new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero));

        session.PrepareForSave(clock);

        session.LastSavedAt.Should().Be("2026-03-04T05:06:07Z");
    }

    [Fact]
    public void Prepare_for_save_can_suppress_the_last_saved_at_update()
    {
        // Used after PopulateMissingTimestamps, so re-writing the file's content does not
        // overwrite the migrated historical LastSavedAt with "now".
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.CreatedAt = "2020-01-01T00:00:00Z";
        session.LastSavedAt = "2020-01-02T00:00:00Z";

        session.PrepareForSave(new FakeClock(), updateSavedAt: false);

        session.LastSavedAt.Should().Be("2020-01-02T00:00:00Z");
    }

    [Fact]
    public void Pml_identity_and_map_identity_reflect_live_session_state()
    {
        var session = new MapSession();
        session.MapIdentity.Should().Be(MapIdentity.Unknown);
        session.PmlIdentity.Should().Be(PmlIdentity.Empty);

        session.ProcessStatus(TelemetrySamples.Status());
        session.PmlId = "6";
        session.PmlCenterLat = 38.01;
        session.PmlCenterLon = -9.02;

        // Both are computed fresh from the flat properties on every access, so they track live
        // mutation rather than a stale snapshot taken at session construction.
        session.MapIdentity.Should().Be(new MapIdentity("Teste", "A 1", "Teste|A 1"));
        session.PmlIdentity.Should().Be(new PmlIdentity("6", 38.01, -9.02));

        session.System = "Outro";
        session.BodyKey = "Outro|A 1";
        session.MapIdentity.Should().Be(new MapIdentity("Outro", "A 1", "Outro|A 1"));
    }

    [Fact]
    public void Completed_route_stays_completed_after_repeated_updates()
    {
        var session = new MapSession();
        session.ProcessStatus(TelemetrySamples.Status());
        session.StartSearch();

        for (int i = 0; i < SearchRouteCalculator.SearchTotalPoints; i++)
        {
            var (lat, lon) = session.GeographicFromLocal(session.NextTargetXy!.Value.X, session.NextTargetXy.Value.Y);
            session.RhinoLat = lat;
            session.RhinoLon = lon;
            session.UpdateNext();
        }

        var history = session.RouteHistory.ToList();
        session.NextTargetXy.Should().BeNull();
        session.RouteIndex.Should().Be(SearchRouteCalculator.SearchTotalPoints);

        session.UpdateNext();
        session.UpdateNext();

        session.NextTargetXy.Should().BeNull();
        session.RouteIndex.Should().Be(SearchRouteCalculator.SearchTotalPoints);
        session.RouteHistory.Should().BeEquivalentTo(history, options => options.WithStrictOrdering());
    }
}
