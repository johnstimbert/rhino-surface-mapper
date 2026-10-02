using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Domain.Tests.TestSupport;

/// <summary>
/// Builds <see cref="TelemetryStatusSample"/> values with the same defaults as the Python test
/// suite's <c>status(**changes)</c>/<c>sample_status(**changes)</c> helper, so ported tests read
/// close to their Python originals.
/// </summary>
internal static class TelemetrySamples
{
    /// <summary>
    /// Builds a sample accepted by <c>TelemetryProcessor</c> (SRV flag set) at a fixed default
    /// position/body/system, overridable per test.
    /// </summary>
    public static TelemetryStatusSample Status(
        long? flags = null,
        double? fuelReservoir = 0.8,
        double? heading = 0.0,
        double? latitude = 38.0,
        double? longitude = -9.0,
        string? starSystem = "Teste",
        string? bodyName = "A 1",
        double? planetRadius = 6_371_000.0,
        double timestamp = 0.0) =>
        new(
            flags ?? MapperConstants.SrvFlag,
            fuelReservoir,
            heading,
            latitude,
            longitude,
            starSystem,
            bodyName,
            planetRadius,
            timestamp);
}
