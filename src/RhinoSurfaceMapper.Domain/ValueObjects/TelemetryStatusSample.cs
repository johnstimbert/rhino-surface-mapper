namespace RhinoSurfaceMapper.Domain.ValueObjects;

/// <summary>
/// One parsed Elite Dangerous <c>Status.json</c> document, ported from the raw <c>dict</c>
/// Python's <c>MapperState.process_status</c> receives. Parsing <c>Status.json</c> itself
/// (including its own malformed-value tolerance) is a Phase 2 Infrastructure concern; by the
/// time a sample reaches <c>TelemetryProcessor</c> its fields are already the typed values this
/// record exposes.
/// </summary>
/// <param name="Flags">
/// Raw status bit field. <c>TelemetryProcessor</c> tests bit 26
/// (<see cref="Constants.MapperConstants.SrvFlag"/>) and bit 19
/// (<see cref="Constants.MapperConstants.FuelLowFlag"/>) against this value.
/// </param>
/// <param name="FuelReservoir">
/// SRV fuel reservoir reading in the game's native 0.0–0.80 unit, or <see langword="null"/>
/// when the <c>Fuel</c> object/<c>FuelReservoir</c> key was absent.
/// </param>
/// <param name="Heading">SRV heading in degrees, or <see langword="null"/> when absent.</param>
/// <param name="Latitude">SRV latitude in degrees, or <see langword="null"/> when absent.</param>
/// <param name="Longitude">SRV longitude in degrees, or <see langword="null"/> when absent.</param>
/// <param name="StarSystem">Reported star system name, or <see langword="null"/>/empty when absent.</param>
/// <param name="BodyName">Reported body name, or <see langword="null"/>/empty when absent.</param>
/// <param name="PlanetRadius">
/// Reported planet radius in metres, or <see langword="null"/> to fall back to
/// <see cref="Constants.MapperConstants.DefaultRadiusMetres"/>.
/// </param>
/// <param name="Timestamp">
/// Sample timestamp as Unix epoch seconds. Python falls back to <c>time.time()</c> when absent;
/// the port requires the caller (the telemetry reader, via <c>IClock</c>) to always supply one,
/// since a domain service must not read the wall clock itself.
/// </param>
public sealed record TelemetryStatusSample(
    long Flags,
    double? FuelReservoir,
    double? Heading,
    double? Latitude,
    double? Longitude,
    string? StarSystem,
    string? BodyName,
    double? PlanetRadius,
    double Timestamp);
