using RhinoSurfaceMapper.Domain.Services;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Infrastructure.Persistence;

/// <summary>
/// Converts between the on-disk <see cref="MapDocumentDto"/> wire shape and the Domain's
/// pre-validation <see cref="RawMapDocument"/> shape, so <see cref="JsonMapRepository"/> never
/// hands a wire-format type to <c>Domain.Services.MapValidator</c> or vice versa.
/// </summary>
internal static class MapDocumentMapper
{
    /// <summary>
    /// Converts a deserialized <see cref="MapDocumentDto"/> into a <see cref="RawMapDocument"/>
    /// ready for <c>MapValidator.Validate</c>, additionally reporting whether any deposit's
    /// persisted size literal was one of the legacy Portuguese ones (decision D7).
    /// </summary>
    /// <param name="dto">The deserialized wire-format document.</param>
    /// <param name="usedLegacyLiteral">
    /// Set to <see langword="true"/> when at least one deposit's <c>size</c> matched a legacy
    /// Portuguese literal, so <see cref="Migration.LegacyMapMigrationService"/> can decide
    /// whether the owning file needs re-saving. Routed through
    /// <see cref="DepositSizeCodec.Decode(string?, out bool)"/> — the single shared place this
    /// distinction is computed, per AGENTS.md's DRY rule — rather than re-deriving the legacy
    /// literal set here.
    /// </param>
    /// <exception cref="Domain.Exceptions.MapValidationException">
    /// A deposit's <c>size</c> is not one of the eight recognised literals. Raised here (via
    /// <see cref="DepositSizeCodec.Decode(string?, out bool)"/>) rather than deferred to
    /// <c>MapValidator</c> only because computing <paramref name="usedLegacyLiteral"/> requires
    /// decoding every deposit's literal anyway; the exception type and message are identical to
    /// what <c>MapValidator</c> would raise for the same input.
    /// </exception>
    public static RawMapDocument ToRawDocument(MapDocumentDto dto, out bool usedLegacyLiteral)
    {
        usedLegacyLiteral = false;
        var deposits = new List<RawDepositDocument>(dto.Deposits.Count);
        foreach (var deposit in dto.Deposits)
        {
            DepositSizeCodec.Decode(deposit.Size ?? "Pequeno", out bool wasLegacy);
            usedLegacyLiteral |= wasLegacy;
            deposits.Add(new RawDepositDocument
            {
                X = deposit.X,
                Y = deposit.Y,
                Lat = deposit.Lat,
                Lon = deposit.Lon,
                Name = deposit.Name,
                Size = deposit.Size,
                Rigs = deposit.Rigs,
            });
        }

        return new RawMapDocument
        {
            System = dto.System,
            Body = dto.Body,
            CreatedAt = dto.CreatedAt,
            LastSavedAt = dto.LastSavedAt,
            Favorite = dto.Favorite,
            Protected = dto.Protected,
            PmlId = dto.PmlId,
            PmlCenterLat = dto.PmlCenterLat,
            PmlCenterLon = dto.PmlCenterLon,
            CenterLat = dto.CenterLat,
            CenterLon = dto.CenterLon,
            PlanetRadius = dto.PlanetRadius,
            CoverageWidthMetres = dto.CoverageWidthMetres,
            ScannerRangeMetres = dto.ScannerRangeMetres,
            SearchStarted = dto.SearchStarted,
            DatumLat = dto.DatumLat,
            DatumLon = dto.DatumLon,
            SearchAzimuth = dto.SearchAzimuth,
            RouteIndex = dto.RouteIndex,
            Points = [.. dto.Points.Select(p => new RawTrailPointDocument { X = p.X, Y = p.Y, Lat = p.Lat, Lon = p.Lon, T = p.T, BreakBefore = p.BreakBefore })],
            Deposits = deposits,
            Rigs = [.. dto.Rigs.Select(r => new RawPositionDocument { X = r.X, Y = r.Y, Lat = r.Lat, Lon = r.Lon })],
            Marks = [.. dto.Marks.Select(m => new RawMarkDocument { X = m.X, Y = m.Y, Lat = m.Lat, Lon = m.Lon, Name = m.Name })],
            RouteHistory = [.. dto.RouteHistory.Select(h => new RawRouteHistoryDocument { Number = h.Number, X = h.X, Y = h.Y, Status = h.Status })],
            RadarCoverage = [.. dto.RadarCoverage.Select(c => new RawRadarPulseDocument { X = c.X, Y = c.Y, Radius = c.Radius })],
        };
    }

    /// <summary>
    /// Converts a <see cref="RawMapDocument"/> (typically <c>MapSession.ToDocument()</c>'s
    /// result) into the wire-format <see cref="MapDocumentDto"/> ready for serialisation.
    /// </summary>
    public static MapDocumentDto ToDto(RawMapDocument document) => new()
    {
        System = document.System,
        Body = document.Body,
        CreatedAt = document.CreatedAt,
        LastSavedAt = document.LastSavedAt,
        Favorite = document.Favorite,
        Protected = document.Protected,
        PmlId = document.PmlId,
        PmlCenterLat = document.PmlCenterLat,
        PmlCenterLon = document.PmlCenterLon,
        CenterLat = document.CenterLat,
        CenterLon = document.CenterLon,
        PlanetRadius = document.PlanetRadius,
        CoverageWidthMetres = document.CoverageWidthMetres,
        ScannerRangeMetres = document.ScannerRangeMetres,
        SearchStarted = document.SearchStarted,
        DatumLat = document.DatumLat,
        DatumLon = document.DatumLon,
        SearchAzimuth = document.SearchAzimuth,
        RouteIndex = document.RouteIndex,
        Points = [.. document.Points.Select(p => new TrailPointDto { X = p.X, Y = p.Y, Lat = p.Lat, Lon = p.Lon, T = p.T, BreakBefore = p.BreakBefore })],
        Deposits = [.. document.Deposits.Select(d => new DepositDto { X = d.X, Y = d.Y, Lat = d.Lat, Lon = d.Lon, Name = d.Name, Size = d.Size, Rigs = d.Rigs })],
        Rigs = [.. document.Rigs.Select(r => new PositionDto { X = r.X, Y = r.Y, Lat = r.Lat, Lon = r.Lon })],
        Marks = [.. document.Marks.Select(m => new MarkDto { X = m.X, Y = m.Y, Lat = m.Lat, Lon = m.Lon, Name = m.Name })],
        RouteHistory = [.. document.RouteHistory.Select(h => new RouteHistoryDto { Number = h.Number, X = h.X, Y = h.Y, Status = h.Status })],
        RadarCoverage = [.. document.RadarCoverage.Select(c => new RadarPulseDto { X = c.X, Y = c.Y, Radius = c.Radius })],
    };
}
