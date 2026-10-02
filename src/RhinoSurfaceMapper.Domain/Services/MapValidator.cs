using RhinoSurfaceMapper.Domain.Entities;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Domain.Services;

/// <summary>
/// Validates and normalises a persisted map document into a fully validated
/// <see cref="MapSession"/>, ported from <c>MapperState.validate_map</c> (plus the parts of
/// <c>_load_file</c> that are pure data-shape decisions rather than file I/O).
/// </summary>
/// <remarks>
/// Enforces expected types, coordinate bounds, search invariants, marker/deposit schemas, and
/// radar pulse limits; backfills latitude/longitude for legacy marker-like records from stored
/// local metre coordinates. Returns a brand-new <see cref="MapSession"/> rather than mutating one
/// in place, so a caller can validate into a candidate and only install it on success — see
/// <see cref="MapSession.LoadFromDocument"/>, which is the orchestration point that performs that
/// swap.
/// </remarks>
public static class MapValidator
{
    /// <summary>Validates <paramref name="document"/> and returns the resulting session.</summary>
    /// <exception cref="MapValidationException">
    /// The document is structurally invalid or out of range — see the exception message for the
    /// specific rule violated.
    /// </exception>
    public static MapSession Validate(RawMapDocument document)
    {
        var session = new MapSession
        {
            System = document.System,
            Body = document.Body,
            BodyKey = $"{document.System}|{document.Body}",
            CreatedAt = document.CreatedAt,
            LastSavedAt = document.LastSavedAt,
            Favorite = document.Favorite,
            Protected = document.Protected,
            PmlId = document.PmlId,
            SearchStarted = document.SearchStarted,
        };

        ValidatePmlCenter(document, session);
        ValidateMapCenter(document, session);
        ValidateRadius(document, session);
        ValidateCoverageAndScanner(document, session);
        ValidateSearchAzimuth(document, session);
        ValidateRouteIndex(document, session);
        ValidateDatum(document, session);

        foreach (var point in document.Points)
        {
            var (x, y, lat, lon) = ResolvePosition(point, session, "points");
            session.AddPoint(new TrailPoint(x, y, lat, lon, point.T ?? 0.0, point.BreakBefore));
        }

        foreach (var deposit in document.Deposits)
        {
            var (x, y, lat, lon) = ResolvePosition(deposit, session, "deposits");
            string name = deposit.Name ?? string.Empty;
            var size = DepositSizeCodec.Decode(deposit.Size ?? "Pequeno");
            int rigCount = NumberCoercion.ToIntegralValueInRange(
                deposit.Rigs ?? 1, "deposits.rigs", min: 1, max: 6, rangeMessage: "Invalid rig count.");

            session.AddDeposit(new Deposit(Guid.NewGuid(), name, size, rigCount, x, y, lat, lon));
        }

        foreach (var rig in document.Rigs)
        {
            var (x, y, lat, lon) = ResolvePosition(rig, session, "rigs");
            session.AddRig(new Rig(Guid.NewGuid(), x, y, lat, lon));
        }

        foreach (var mark in document.Marks)
        {
            var (x, y, lat, lon) = ResolvePosition(mark, session, "marks");
            if (string.IsNullOrWhiteSpace(mark.Name))
            {
                throw new MapValidationException("Invalid mark name.");
            }

            session.AddMark(new MapMark(Guid.NewGuid(), mark.Name, x, y, lat, lon));
        }

        foreach (var entry in document.RouteHistory)
        {
            double x = NumberCoercion.ToFiniteDouble(entry.X, "route_history.x");
            double y = NumberCoercion.ToFiniteDouble(entry.Y, "route_history.y");
            var status = RouteStatusCodec.Parse(entry.Status);
            int number = NumberCoercion.ToIntegralValueInRange(
                entry.Number, "route_history.number", min: 1, max: SearchRouteCalculator.SearchTotalPoints, rangeMessage: "Invalid route target number.");

            session.AddRouteHistory(new RouteHistoryEntry(number, x, y, status));
        }

        session.LastXy = session.Points.Count > 0
            ? (session.Points[^1].X, session.Points[^1].Y)
            : null;

        foreach (var pulse in document.RadarCoverage)
        {
            double x = NumberCoercion.ToFiniteDouble(pulse.X, "radar_coverage.x");
            double y = NumberCoercion.ToFiniteDouble(pulse.Y, "radar_coverage.y");
            double radius = NumberCoercion.ToFiniteDouble(pulse.Radius, "radar_coverage.radius");
            if (radius is < 0 or > 5000)
            {
                throw new MapValidationException("Invalid coverage radius.");
            }

            session.AddRadarCoverage(new RadarCoverageDisc(x, y, radius));
        }

        return session;
    }

    private static void ValidatePmlCenter(RawMapDocument document, MapSession session)
    {
        bool latMissing = document.PmlCenterLat is null;
        bool lonMissing = document.PmlCenterLon is null;
        if (latMissing != lonMissing)
        {
            throw new MapValidationException("Incomplete PML center.");
        }

        if (document.PmlCenterLat is double lat && document.PmlCenterLon is double lon)
        {
            RequireLatitude(lat, strict: false, "PML center out of bounds.");
            RequireLongitude(lon, "PML center out of bounds.");
            session.PmlCenterLat = lat;
            session.PmlCenterLon = lon;
        }
    }

    private static void ValidateMapCenter(RawMapDocument document, MapSession session)
    {
        double lat = RequireFinite(document.CenterLat, "center_lat");
        double lon = RequireFinite(document.CenterLon, "center_lon");

        // Unlike every other latitude bound in this validator, the map centre uses a *strict*
        // [-90, 90] exclusion at the poles (Python: `-90 < self.center_lat < 90`) because the
        // local equirectangular projection's cos(latitude) scale factor degenerates at the poles.
        RequireLatitude(lat, strict: true, "Map center out of supported bounds.");
        RequireLongitude(lon, "Map center out of supported bounds.");
        session.CenterLat = lat;
        session.CenterLon = lon;
    }

    private static void ValidateRadius(RawMapDocument document, MapSession session)
    {
        double radius = RequireFinite(document.PlanetRadius, "planet_radius");
        if (radius <= 0)
        {
            throw new MapValidationException("Invalid planet radius.");
        }

        session.Radius = radius;
    }

    private static void ValidateCoverageAndScanner(RawMapDocument document, MapSession session)
    {
        double coverage = RequireFinite(document.CoverageWidthMetres, "coverage_width_m");
        double scanner = RequireFinite(document.ScannerRangeMetres, "scanner_range_m");
        if (coverage is < 100 or > 5000 || scanner is < 500 or > 5000)
        {
            throw new MapValidationException("Coverage or scanner range out of bounds.");
        }

        session.CoverageWidthMetres = coverage;
        session.ScannerRangeMetres = scanner;
    }

    private static void ValidateSearchAzimuth(RawMapDocument document, MapSession session)
    {
        // Python: `type(self.search_azimuth) is not int` — deliberately rejects bool (a Python int
        // subtype) as well as float/str. C#'s `is int` pattern on a boxed value already has this
        // exact behaviour for free: a boxed `bool`/`double`/`string` never matches `is int`.
        if (document.SearchAzimuth is not int azimuth || azimuth is < 0 or > 359)
        {
            throw new MapValidationException("Search azimuth must be an integer between 000 and 359.");
        }

        session.SearchAzimuth = azimuth;
    }

    private static void ValidateRouteIndex(RawMapDocument document, MapSession session)
    {
        if (document.RouteIndex < 0 || document.RouteIndex > SearchRouteCalculator.SearchTotalPoints)
        {
            throw new MapValidationException("Invalid search route index.");
        }

        session.RouteIndex = document.RouteIndex;
    }

    private static void ValidateDatum(RawMapDocument document, MapSession session)
    {
        if (session.SearchStarted && (document.DatumLat is null || document.DatumLon is null))
        {
            throw new MapValidationException("Active search without a Datum.");
        }

        bool latMissing = document.DatumLat is null;
        bool lonMissing = document.DatumLon is null;
        if (latMissing != lonMissing)
        {
            throw new MapValidationException("Incomplete Datum.");
        }

        if (document.DatumLat is double lat && document.DatumLon is double lon)
        {
            double checkedLat = RequireFinite(lat, "datum_lat");
            double checkedLon = RequireFinite(lon, "datum_lon");
            RequireLatitude(checkedLat, strict: false, "Invalid Datum.");
            RequireLongitude(checkedLon, "Invalid Datum.");
            session.DatumLat = checkedLat;
            session.DatumLon = checkedLon;
        }
    }

    /// <summary>
    /// Resolves a marker-like item's local coordinates, numerically coercing <c>x</c>/<c>y</c>,
    /// and either coercing its stored <c>lat</c>/<c>lon</c> or reconstructing both from
    /// <c>x</c>/<c>y</c> when either is absent — ported from the shared per-item loop in
    /// <c>validate_map</c> that applies to points, deposits, rigs and marks alike.
    /// </summary>
    private static (double X, double Y, double Lat, double Lon) ResolvePosition(RawPositionDocument raw, MapSession session, string label)
    {
        double x = NumberCoercion.ToFiniteDouble(raw.X, $"{label}.x");
        double y = NumberCoercion.ToFiniteDouble(raw.Y, $"{label}.y");

        double lat, lon;
        if (raw.Lat is null || raw.Lon is null)
        {
            (lat, lon) = session.GeographicFromLocal(x, y);
        }
        else
        {
            lat = NumberCoercion.ToFiniteDouble(raw.Lat, $"{label}.lat");
            lon = NumberCoercion.ToFiniteDouble(raw.Lon, $"{label}.lon");
        }

        RequireLatitude(lat, strict: false, "Invalid marker coordinates.");
        RequireLongitude(lon, "Invalid marker coordinates.");
        return (x, y, lat, lon);
    }

    private static double RequireFinite(double value, string fieldName) =>
        double.IsFinite(value) ? value : throw new MapValidationException($"{fieldName} contains a non-finite number.");

    private static void RequireLatitude(double lat, bool strict, string message)
    {
        bool valid = strict ? lat is > -90 and < 90 : lat is >= -90 and <= 90;
        if (!valid)
        {
            throw new MapValidationException(message);
        }
    }

    private static void RequireLongitude(double lon, string message)
    {
        if (lon is < -180 or > 180)
        {
            throw new MapValidationException(message);
        }
    }
}
