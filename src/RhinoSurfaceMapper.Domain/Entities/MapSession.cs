using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Domain.Entities;

/// <summary>
/// Mutable map and navigation state independent of any graphical toolkit, ported from Python's
/// <c>MapperState</c>. Owns one loaded or in-progress map plus live telemetry that is never
/// serialised.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="MapSession"/> is intentionally mutable: the generation counter, pause state and
/// consumed-sample flags are inherently stateful session concepts the Python original never
/// treated as immutable, and modelling it as an immutable record would force a rewrite of
/// behaviour this port must preserve exactly. Every *value* it holds (<see cref="TrailPoint"/>,
/// <see cref="Deposit"/>, <see cref="Rig"/>, <see cref="MapMark"/>,
/// <see cref="RadarCoverageDisc"/>, <see cref="RouteHistoryEntry"/>) is an immutable
/// <see langword="record"/>, so a reader can snapshot a collection cheaply without racing a
/// writer — see the "Threading and concurrency model" section of the design for how Phase 3+
/// turns these snapshots into copy-on-write <see cref="System.Collections.Immutable.ImmutableArray{T}"/>
/// instances without changing this public surface.
/// </para>
/// <para>
/// The actual domain *rules* that mutate a session (telemetry ingestion, search route
/// advancement, overlay guidance, document validation) live in the stateless services under
/// <c>Domain.Services</c> (<c>TelemetryProcessor</c>, <c>SearchRouteCalculator</c>,
/// <c>OverlayNavigationCalculator</c>, <c>MapValidator</c>), exactly as the design's "Domain
/// services" table assigns them. The convenience methods on this type
/// (<see cref="ProcessStatus"/>, <see cref="StartSearch"/>, <see cref="SkipNext"/>,
/// <see cref="UpdateNext"/>, <see cref="EvaluateOverlayNavigation"/>) simply delegate to those
/// services so call sites read close to the original <c>state.start_search(...)</c> style.
/// </para>
/// </remarks>
public sealed class MapSession
{
    private readonly List<TrailPoint> _points = [];
    private readonly List<RadarCoverageDisc> _radarCoverage = [];
    private readonly List<Deposit> _deposits = [];
    private readonly List<Rig> _rigs = [];
    private readonly List<MapMark> _marks = [];
    private readonly List<RouteHistoryEntry> _routeHistory = [];

    /// <summary>
    /// Path to the live <c>Status.json</c> being polled for this session, or <see langword="null"/>
    /// until a telemetry reader (Phase 2+) assigns one. Reserved field: no Phase 1 rule reads or
    /// writes it, matching Python's <c>status_path</c> attribute, which <c>mapper_core.py</c>
    /// also only declares without using.
    /// </summary>
    public string? StatusPath { get; set; }

    /// <summary>Current star system name, or <see cref="string.Empty"/> until known.</summary>
    public string System { get; set; } = string.Empty;

    /// <summary>Current body name, or <see cref="string.Empty"/> until known.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary><c>"{System}|{Body}"</c>, or <see langword="null"/> before any body has ever been established.</summary>
    public string? BodyKey { get; set; }

    /// <summary>
    /// Convenience read-only view of <see cref="System"/>/<see cref="Body"/>/<see cref="BodyKey"/>
    /// as the design's <see cref="MapIdentity"/> entity, built fresh from live state on every
    /// access so it can never desynchronise from the flat properties it mirrors.
    /// </summary>
    public MapIdentity MapIdentity => new(System, Body, BodyKey);

    /// <summary>Monotonically increasing counter bumped by <see cref="NewMap"/>, letting UI code detect a map switch without comparing collections.</summary>
    public int MapGeneration { get; private set; }

    /// <summary>ISO-8601 creation timestamp, or <see langword="null"/> for a map never stamped (old maps remain valid without one).</summary>
    public string? CreatedAt { get; set; }

    /// <summary>ISO-8601 last-saved timestamp, or <see langword="null"/> for a map never saved with a stamp.</summary>
    public string? LastSavedAt { get; set; }

    /// <summary>Library favourite flag.</summary>
    public bool Favorite { get; set; }

    /// <summary>Library protected (read-only) flag, persisted to disk.</summary>
    public bool Protected { get; set; }

    /// <summary>
    /// Session-only "viewing an existing map without recording" mode. Never serialised — see
    /// <see cref="ToDocument"/>, which omits it exactly as Python's <c>to_dict</c> does.
    /// </summary>
    public bool MiningOnly { get; private set; }

    /// <summary>
    /// PML identifier. The map centre (<see cref="CenterLat"/>/<see cref="CenterLon"/>) remains a
    /// technical projection anchor; this is the geographic identity used to rediscover the same
    /// map on return.
    /// </summary>
    public string PmlId { get; set; } = string.Empty;

    /// <summary>PML centre latitude in degrees, paired with <see cref="PmlCenterLon"/>.</summary>
    public double? PmlCenterLat { get; set; }

    /// <summary>PML centre longitude in degrees, paired with <see cref="PmlCenterLat"/>.</summary>
    public double? PmlCenterLon { get; set; }

    /// <summary>
    /// Convenience read-only view of <see cref="PmlId"/>/<see cref="PmlCenterLat"/>/
    /// <see cref="PmlCenterLon"/> as the design's <see cref="PmlIdentity"/> entity, built fresh
    /// from live state on every access so it can never desynchronise from the flat properties it
    /// mirrors.
    /// </summary>
    public PmlIdentity PmlIdentity => new(PmlId, PmlCenterLat, PmlCenterLon);

    /// <summary>Local-projection map centre latitude in degrees, or <see langword="null"/> until the first accepted telemetry sample establishes it.</summary>
    public double? CenterLat { get; set; }

    /// <summary>Local-projection map centre longitude in degrees, or <see langword="null"/> until established.</summary>
    public double? CenterLon { get; set; }

    /// <summary>Planet radius in metres, used by every local-projection/haversine calculation for this map.</summary>
    public double Radius { get; set; } = Constants.MapperConstants.DefaultRadiusMetres;

    /// <summary>Local <c>(x, y)</c> of the most recently recorded trail point, used to enforce the minimum trail spacing/break-distance rules.</summary>
    public (double X, double Y)? LastXy { get; set; }

    /// <summary>Last known SRV latitude in degrees.</summary>
    public double? RhinoLat { get; set; }

    /// <summary>Last known SRV longitude in degrees.</summary>
    public double? RhinoLon { get; set; }

    /// <summary>Last known SRV heading in degrees, normalised to the half-open range [0, 360).</summary>
    public double? RhinoHeading { get; set; }

    /// <summary>Last known SRV fuel reservoir reading, in the game's native 0.0–0.80 unit.</summary>
    public double? FuelReservoir { get; set; }

    /// <summary>Last known SRV fuel percentage, clamped to [0, 100].</summary>
    public double? FuelPercent { get; set; }

    /// <summary>Whether the SRV fuel-low bit was set on the most recent accepted sample.</summary>
    public bool FuelLow { get; set; }

    /// <summary>Radar coverage disc draw width in metres (a rendering parameter, not a rule input).</summary>
    public double CoverageWidthMetres { get; set; } = 2_000.0;

    /// <summary>Scanner range in metres (a rendering parameter, not a rule input).</summary>
    public double ScannerRangeMetres { get; set; } = 2_000.0;

    /// <summary>Whether map-centre-follow rendering is enabled. Session/UI concern; no Phase 1 rule reads it.</summary>
    public bool CenterEnabled { get; set; }

    /// <summary>
    /// Search route starting bearing in degrees, where 0 is north and values increase clockwise.
    /// Set only through <see cref="StartSearch"/>, which enforces the inclusive 0–359 range.
    /// </summary>
    public int SearchAzimuth { get; set; }

    /// <summary>Whether a circular search route is currently active.</summary>
    public bool SearchStarted { get; set; }

    /// <summary>Search Datum latitude in degrees, paired with <see cref="DatumLon"/>.</summary>
    public double? DatumLat { get; set; }

    /// <summary>Search Datum longitude in degrees, paired with <see cref="DatumLat"/>.</summary>
    public double? DatumLon { get; set; }

    /// <summary>Zero-based index of the next unreached search route point.</summary>
    public int RouteIndex { get; set; }

    /// <summary>Local <c>(x, y)</c> of the next search route target, or <see langword="null"/> when no route is active or it just completed.</summary>
    public (double X, double Y)? NextTargetXy { get; set; }

    /// <summary>Whether the overlay distance text is currently in its "on" blink phase.</summary>
    public bool OverlayBlinkOn { get; set; } = true;

    /// <summary>Monotonic time, in seconds, at which the overlay blink phase next toggles.</summary>
    public double OverlayNextBlink { get; set; }

    /// <summary>Explicit navigation target (deposit/rig/mark), taking priority over the circular search route.</summary>
    public NavigationTarget? ActiveNavTarget { get; set; }

    /// <summary>Whether the circular search route is paused in favour of marker navigation.</summary>
    public bool SearchPaused { get; set; }

    /// <summary>Local <c>(x, y)</c> the route was paused at, to be returned to once marker navigation completes.</summary>
    public (double X, double Y)? SearchPausePoint { get; set; }

    /// <summary>Whether the overlay is currently guiding back to <see cref="SearchPausePoint"/>.</summary>
    public bool ReturnToPause { get; set; }

    /// <summary>Whether the most recently processed telemetry sample had the SRV flag set.</summary>
    public bool InSrv { get; set; }

    /// <summary>Recorded SRV trail samples, in recording order. Never mutate the returned list in place; use <see cref="AddPoint"/>.</summary>
    public IReadOnlyList<TrailPoint> Points => _points;

    /// <summary>Accumulated radar coverage discs. Never mutate the returned list in place; use <see cref="AddRadarCoverage"/>.</summary>
    public IReadOnlyList<RadarCoverageDisc> RadarCoverage => _radarCoverage;

    /// <summary>Recorded mining deposits. Never mutate the returned list in place; use <see cref="AddDeposit"/>.</summary>
    public IReadOnlyList<Deposit> Deposits => _deposits;

    /// <summary>Placed rigs. Never mutate the returned list in place; use <see cref="AddRig"/>.</summary>
    public IReadOnlyList<Rig> Rigs => _rigs;

    /// <summary>User-placed named marks. Never mutate the returned list in place; use <see cref="AddMark"/>.</summary>
    public IReadOnlyList<MapMark> Marks => _marks;

    /// <summary>Completed search-route point history. Never mutate the returned list in place; use <see cref="AddRouteHistory"/>.</summary>
    public IReadOnlyList<RouteHistoryEntry> RouteHistory => _routeHistory;

    /// <summary>Appends a trail point and records it as the most recent recorded position.</summary>
    public void AddPoint(TrailPoint point) => _points.Add(point);

    /// <summary>Appends an accumulated radar coverage disc.</summary>
    public void AddRadarCoverage(RadarCoverageDisc disc) => _radarCoverage.Add(disc);

    /// <summary>Appends a recorded mining deposit.</summary>
    public void AddDeposit(Deposit deposit) => _deposits.Add(deposit);

    /// <summary>Appends a placed rig.</summary>
    public void AddRig(Rig rig) => _rigs.Add(rig);

    /// <summary>Appends a user-placed named mark.</summary>
    public void AddMark(MapMark mark) => _marks.Add(mark);

    /// <summary>Appends a completed search-route point history entry.</summary>
    public void AddRouteHistory(RouteHistoryEntry entry) => _routeHistory.Add(entry);

    /// <summary>Clears the search-route point history, used when (re)starting a search route.</summary>
    public void ClearRouteHistory() => _routeHistory.Clear();

    /// <summary>
    /// Returns whether exploration changes (trail recording, search route, new marks/deposits/rigs)
    /// are currently forbidden, ported from <c>MapperState.read_only</c>.
    /// </summary>
    public bool ReadOnly => Protected || MiningOnly;

    /// <summary>
    /// Returns whether the navigation overlay is meaningful for this session right now, ported
    /// from <c>MapperState.overlay_allowed</c>.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> whenever <see cref="InSrv"/> is <see langword="false"/>;
    /// otherwise <see langword="true"/> while a search route is active, an explicit navigation
    /// target is set, or the overlay is guiding back to a pause point.
    /// </returns>
    public bool OverlayAllowed()
    {
        if (!InSrv)
        {
            return false;
        }

        return SearchStarted || ActiveNavTarget is not null || ReturnToPause;
    }

    /// <summary>
    /// Converts latitude/longitude to local metres relative to this session's map centre, ported
    /// from <c>MapperState.llxy</c>. A thin convenience wrapper over
    /// <see cref="PlanetGeometry.LocalFromGeographic"/> that supplies this session's own centre
    /// and radius.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The map centre has not been established yet (<see cref="CenterLat"/>/<see cref="CenterLon"/>
    /// is <see langword="null"/>), matching Python's <c>RuntimeError</c>.
    /// </exception>
    public (double X, double Y) LocalFromGeographic(double lat, double lon)
    {
        if (CenterLat is null || CenterLon is null)
        {
            throw new InvalidOperationException("The map center has not been set yet.");
        }

        return PlanetGeometry.LocalFromGeographic(CenterLat.Value, CenterLon.Value, Radius, lat, lon);
    }

    /// <summary>
    /// Converts local metres relative to this session's map centre back to latitude/longitude,
    /// ported from <c>MapperState.xyll</c>. The exact inverse of <see cref="LocalFromGeographic"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The map centre has not been established yet.</exception>
    public (double Lat, double Lon) GeographicFromLocal(double x, double y)
    {
        if (CenterLat is null || CenterLon is null)
        {
            throw new InvalidOperationException("The map center has not been set yet.");
        }

        return PlanetGeometry.GeographicFromLocal(CenterLat.Value, CenterLon.Value, Radius, x, y);
    }

    /// <summary>
    /// Clears exploration data and search state while keeping live telemetry, ported from
    /// <c>MapperState.new_map</c>.
    /// </summary>
    /// <param name="keepPml">
    /// When <see langword="true"/>, preserves <see cref="PmlId"/>/<see cref="PmlCenterLat"/>/
    /// <see cref="PmlCenterLon"/> — used by the "New" action to start another recording within
    /// the same PML. A real body change (the default) clears PML data because it no longer
    /// applies to the new body.
    /// </param>
    public void NewMap(bool keepPml = false)
    {
        Favorite = false;
        Protected = false;
        MiningOnly = false;
        _points.Clear();
        _radarCoverage.Clear();
        MapGeneration++;
        _deposits.Clear();
        _rigs.Clear();
        _marks.Clear();
        CreatedAt = null;
        LastSavedAt = null;
        LastXy = null;
        SearchStarted = false;
        SearchPaused = false;
        SearchPausePoint = null;
        ReturnToPause = false;
        ActiveNavTarget = null;
        DatumLat = null;
        DatumLon = null;
        if (!keepPml)
        {
            PmlId = string.Empty;
            PmlCenterLat = null;
            PmlCenterLon = null;
        }

        RouteIndex = 0;
        NextTargetXy = null;
        _routeHistory.Clear();
    }

    /// <summary>
    /// Suspends exploration/search while keeping existing records viewable, ported from
    /// <c>MapperState.enter_mining_mode</c>.
    /// </summary>
    public void EnterMiningMode()
    {
        MiningOnly = true;
        SearchStarted = false;
        SearchPaused = false;
        SearchPausePoint = null;
        ReturnToPause = false;
        ActiveNavTarget = null;
        NextTargetXy = null;
    }

    /// <summary>
    /// Updates this session from one telemetry sample. Convenience delegate for
    /// <see cref="Services.TelemetryProcessor.Process"/> — see that method for the full rule set.
    /// </summary>
    public StatusUpdate ProcessStatus(TelemetryStatusSample sample, bool recordPosition = true) =>
        TelemetryProcessor.Process(this, sample, recordPosition);

    /// <summary>
    /// Starts a circular search route from the current position. Convenience delegate for
    /// <see cref="Services.SearchRouteCalculator.StartSearch"/>.
    /// </summary>
    public bool StartSearch(int azimuth = 0) => SearchRouteCalculator.StartSearch(this, azimuth);

    /// <summary>
    /// Marks the current search target as skipped and advances to the next one. Convenience
    /// delegate for <see cref="Services.SearchRouteCalculator.SkipNext"/>.
    /// </summary>
    public bool SkipNext() => SearchRouteCalculator.SkipNext(this);

    /// <summary>
    /// Refreshes the circular search target and auto-advances on arrival. Convenience delegate
    /// for <see cref="Services.SearchRouteCalculator.UpdateNext"/>.
    /// </summary>
    public void UpdateNext() => SearchRouteCalculator.UpdateNext(this);

    /// <summary>
    /// Evaluates overlay heading/distance guidance for the current navigation target.
    /// Convenience delegate for <see cref="Services.OverlayNavigationCalculator.Evaluate"/>.
    /// </summary>
    public OverlayNavigationResult EvaluateOverlayNavigation(IClock clock) => OverlayNavigationCalculator.Evaluate(this, clock);

    /// <summary>
    /// Fills absent map dates from legacy file metadata, ported from
    /// <c>MapperState.populate_missing_timestamps</c>. Reading the file's own creation/
    /// modification time is a Phase 2 Infrastructure concern (<see cref="IMapRepository.ReadTimestampsAsync"/>);
    /// this method only applies values already read, so the migration rule — preserving
    /// historical dates instead of inventing a migration date — lives in one place.
    /// </summary>
    /// <param name="createdAt">The file's creation time, already formatted as ISO-8601.</param>
    /// <param name="lastSavedAt">The file's last-modified time, already formatted as ISO-8601.</param>
    /// <returns><see langword="true"/> when at least one field was filled, meaning the caller should re-save.</returns>
    public bool PopulateMissingTimestamps(string createdAt, string lastSavedAt)
    {
        bool changed = false;
        if (CreatedAt is null)
        {
            CreatedAt = createdAt;
            changed = true;
        }

        if (LastSavedAt is null)
        {
            LastSavedAt = lastSavedAt;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Applies the save-time timestamp rule, ported from the timestamp half of
    /// <c>MapperState.save</c> (the file-write half is a Phase 2 <see cref="IMapRepository"/>
    /// concern). Only maps that already carry a <see cref="CreatedAt"/> receive a new
    /// <see cref="LastSavedAt"/>; a map saved for the very first time does not invent a creation
    /// date here.
    /// </summary>
    /// <param name="clock">Source of the current UTC time; a domain service must never read the wall clock directly.</param>
    /// <param name="updateSavedAt">When <see langword="false"/>, suppresses the timestamp update even for an already-stamped map (used when only re-writing content, such as after <see cref="PopulateMissingTimestamps"/>).</param>
    /// <exception cref="MapSessionReadOnlyException"><see cref="MiningOnly"/> is set.</exception>
    public void PrepareForSave(IClock clock, bool updateSavedAt = true)
    {
        if (MiningOnly)
        {
            throw new MapSessionReadOnlyException(
                "Mining-only mode does not allow saving changes. Open a new version to explore.");
        }

        if (CreatedAt is not null && updateSavedAt)
        {
            // "yyyy-MM-ddTHH:mm:ssZ" reproduces Python's time.strftime('%Y-%m-%dT%H:%M:%SZ',
            // time.gmtime()): second precision, a literal "Z" (clock.UtcNow is already UTC), and
            // invariant formatting regardless of host locale.
            LastSavedAt = clock.UtcNow.ToString("yyyy-MM-dd\\THH:mm:ss\\Z", global::System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Serialises this session's durable map state into the persisted document shape, ported
    /// from <c>MapperState.to_dict</c>. Live telemetry, <see cref="MiningOnly"/> and
    /// <see cref="MapGeneration"/> are omitted on purpose: they are session/UI state, not map
    /// data to reload later.
    /// </summary>
    public RawMapDocument ToDocument() => new()
    {
        System = System,
        Body = Body,
        CreatedAt = CreatedAt,
        LastSavedAt = LastSavedAt,
        Favorite = Favorite,
        Protected = Protected,
        PmlId = PmlId,
        PmlCenterLat = PmlCenterLat,
        PmlCenterLon = PmlCenterLon,
        CenterLat = CenterLat ?? 0.0,
        CenterLon = CenterLon ?? 0.0,
        PlanetRadius = Radius,
        CoverageWidthMetres = CoverageWidthMetres,
        ScannerRangeMetres = ScannerRangeMetres,
        SearchStarted = SearchStarted,
        DatumLat = DatumLat,
        DatumLon = DatumLon,
        SearchAzimuth = SearchAzimuth,
        RouteIndex = RouteIndex,
        Points = [.. Points.Select(p => new RawTrailPointDocument { X = p.X, Y = p.Y, Lat = p.Lat, Lon = p.Lon, T = p.T, BreakBefore = p.BreakBefore })],
        Deposits = [.. Deposits.Select(d => new RawDepositDocument { X = d.X, Y = d.Y, Lat = d.Lat, Lon = d.Lon, Name = d.Name, Size = DepositSizeCodec.Encode(d.Size), Rigs = d.Rigs })],
        Rigs = [.. Rigs.Select(r => new RawPositionDocument { X = r.X, Y = r.Y, Lat = r.Lat, Lon = r.Lon })],
        Marks = [.. Marks.Select(m => new RawMarkDocument { X = m.X, Y = m.Y, Lat = m.Lat, Lon = m.Lon, Name = m.Name })],
        RouteHistory = [.. RouteHistory.Select(h => new RawRouteHistoryDocument { Number = h.Number, X = h.X, Y = h.Y, Status = RouteStatusCodec.ToLiteral(h.Status) })],
        RadarCoverage = [.. RadarCoverage.Select(c => new RawRadarPulseDocument { X = c.X, Y = c.Y, Radius = c.Radius })],
    };

    /// <summary>
    /// Loads, validates and installs a map document, ported from <c>MapperState.load</c>. A
    /// candidate session is built and validated before replacing this instance's state; if
    /// validation fails, this session's current state remains completely untouched.
    /// </summary>
    /// <exception cref="MapValidationException">The document is structurally invalid or out of range. This session is left unchanged.</exception>
    public void LoadFromDocument(RawMapDocument document)
    {
        var candidate = MapValidator.Validate(document);
        SearchRouteCalculator.UpdateNext(candidate);
        ReplaceFrom(candidate);
    }

    /// <summary>
    /// Replaces every field of this instance with the candidate's, ported from Python's
    /// <c>self.__dict__.update(candidate.__dict__)</c> swap at the end of <c>load</c>.
    /// </summary>
    private void ReplaceFrom(MapSession candidate)
    {
        StatusPath = candidate.StatusPath;
        System = candidate.System;
        Body = candidate.Body;
        BodyKey = candidate.BodyKey;
        MapGeneration = candidate.MapGeneration;
        CreatedAt = candidate.CreatedAt;
        LastSavedAt = candidate.LastSavedAt;
        Favorite = candidate.Favorite;
        Protected = candidate.Protected;
        MiningOnly = candidate.MiningOnly;
        PmlId = candidate.PmlId;
        PmlCenterLat = candidate.PmlCenterLat;
        PmlCenterLon = candidate.PmlCenterLon;
        CenterLat = candidate.CenterLat;
        CenterLon = candidate.CenterLon;
        Radius = candidate.Radius;
        LastXy = candidate.LastXy;
        RhinoLat = candidate.RhinoLat;
        RhinoLon = candidate.RhinoLon;
        RhinoHeading = candidate.RhinoHeading;
        FuelReservoir = candidate.FuelReservoir;
        FuelPercent = candidate.FuelPercent;
        FuelLow = candidate.FuelLow;
        CoverageWidthMetres = candidate.CoverageWidthMetres;
        ScannerRangeMetres = candidate.ScannerRangeMetres;
        CenterEnabled = candidate.CenterEnabled;
        SearchAzimuth = candidate.SearchAzimuth;
        SearchStarted = candidate.SearchStarted;
        DatumLat = candidate.DatumLat;
        DatumLon = candidate.DatumLon;
        RouteIndex = candidate.RouteIndex;
        NextTargetXy = candidate.NextTargetXy;
        OverlayBlinkOn = candidate.OverlayBlinkOn;
        OverlayNextBlink = candidate.OverlayNextBlink;
        ActiveNavTarget = candidate.ActiveNavTarget;
        SearchPaused = candidate.SearchPaused;
        SearchPausePoint = candidate.SearchPausePoint;
        ReturnToPause = candidate.ReturnToPause;
        InSrv = candidate.InSrv;

        _points.Clear();
        _points.AddRange(candidate._points);
        _radarCoverage.Clear();
        _radarCoverage.AddRange(candidate._radarCoverage);
        _deposits.Clear();
        _deposits.AddRange(candidate._deposits);
        _rigs.Clear();
        _rigs.AddRange(candidate._rigs);
        _marks.Clear();
        _marks.AddRange(candidate._marks);
        _routeHistory.Clear();
        _routeHistory.AddRange(candidate._routeHistory);
    }
}
