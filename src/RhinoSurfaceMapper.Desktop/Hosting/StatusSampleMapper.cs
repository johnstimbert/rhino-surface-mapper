using System.Text.Json;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Desktop.Hosting;

/// <summary>
/// Converts a parsed <c>Status.json</c> <see cref="JsonDocument"/> into a
/// <see cref="TelemetryStatusSample"/>, ported from the field reads at the top of Python's
/// <c>MapperState.process_status</c> (<c>status.get("Flags", 0)</c>,
/// <c>status.get("Fuel") or {}</c> / <c>fuel.get("FuelReservoir")</c>, <c>Heading</c>,
/// <c>Latitude</c>/<c>Longitude</c>, <c>StarSystem</c>/<c>BodyName</c>, <c>PlanetRadius</c>,
/// <c>status.get("timestamp", time.time())</c>).
/// </summary>
/// <remarks>
/// Deliberately placed in <c>Desktop.Hosting</c> rather than <c>Infrastructure</c> or
/// <c>Application</c>: <see cref="TelemetryStatusSample"/>'s own documentation states that
/// "parsing <c>Status.json</c> itself... is a Phase 2 Infrastructure concern", but Phase 2 only
/// implemented <see cref="RhinoSurfaceMapper.Application.Interfaces.IStatusTelemetryReader"/>'s
/// raw-<see cref="JsonDocument"/> contract, not the field-level mapping into
/// <see cref="TelemetryStatusSample"/> — that mapping has exactly one consumer today
/// (<see cref="TelemetryHostedService"/>), so it lives beside its only caller for now. A second
/// consumer (for example, a future diagnostics panel) should prompt moving this type into
/// <c>Infrastructure.Telemetry</c> instead of duplicating it.
/// </remarks>
internal static class StatusSampleMapper
{
    /// <summary>
    /// Maps <paramref name="document"/>'s root object into a <see cref="TelemetryStatusSample"/>.
    /// </summary>
    /// <param name="document">The parsed <c>Status.json</c> document.</param>
    /// <param name="clock">Source of the fallback timestamp when <c>timestamp</c> is absent, matching Python's <c>time.time()</c> fallback.</param>
    public static TelemetryStatusSample Map(JsonDocument document, IClock clock)
    {
        JsonElement root = document.RootElement;

        long flags = TryGetInt64(root, "Flags") ?? 0;
        double? fuelReservoir = root.TryGetProperty("Fuel", out var fuel) && fuel.ValueKind == JsonValueKind.Object
            ? TryGetDouble(fuel, "FuelReservoir")
            : null;
        double? heading = TryGetDouble(root, "Heading");
        double? latitude = TryGetDouble(root, "Latitude");
        double? longitude = TryGetDouble(root, "Longitude");
        string? starSystem = TryGetString(root, "StarSystem");
        string? bodyName = TryGetString(root, "BodyName");
        double? planetRadius = TryGetDouble(root, "PlanetRadius");
        double timestamp = TryGetDouble(root, "timestamp") ?? UnixSeconds(clock);

        return new TelemetryStatusSample(flags, fuelReservoir, heading, latitude, longitude, starSystem, bodyName, planetRadius, timestamp);
    }

    private static double UnixSeconds(IClock clock) => clock.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private static long? TryGetInt64(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out long i) => i,
            JsonValueKind.Number => (long)value.GetDouble(),
            _ => null,
        };
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.GetDouble();
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return value.GetString();
    }
}
