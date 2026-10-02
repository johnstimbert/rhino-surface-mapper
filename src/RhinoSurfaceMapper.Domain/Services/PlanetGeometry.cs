namespace RhinoSurfaceMapper.Domain.Services;

/// <summary>
/// Stateless planetary geometry: the local equirectangular metre projection used by every map
/// (<c>llxy</c>/<c>xyll</c> in Python), great-circle distance, destination-point projection, and
/// signed heading error. Ported from the free functions in <c>mapper_core.py</c> and
/// <c>map_pml.py</c>.
/// </summary>
/// <remarks>
/// Every member is a pure function: no field, no I/O, no exception depends on process state.
/// Angles are degrees at the API boundary (matching the telemetry/JSON convention) and radians
/// only inside each method body, exactly as the Python functions mix the two.
/// </remarks>
public static class PlanetGeometry
{
    /// <summary>
    /// Converts latitude/longitude to local metres relative to a map centre, ported from
    /// <c>MapperState.llxy</c>.
    /// </summary>
    /// <param name="centerLat">Map centre latitude in degrees.</param>
    /// <param name="centerLon">Map centre longitude in degrees.</param>
    /// <param name="radius">Planet radius in metres.</param>
    /// <param name="lat">Latitude to convert, in degrees.</param>
    /// <param name="lon">Longitude to convert, in degrees.</param>
    /// <returns>
    /// Local <c>(x, y)</c> in metres: <c>x</c> is easting (longitude axis, scaled by
    /// <c>cos(centerLat)</c> because meridians converge toward the poles), <c>y</c> is northing
    /// (latitude axis, unscaled because a degree of latitude spans the same arc length
    /// everywhere on a sphere).
    /// </returns>
    public static (double X, double Y) LocalFromGeographic(double centerLat, double centerLon, double radius, double lat, double lon)
    {
        double cosine = Math.Cos(DegreesToRadians(centerLat));
        double x = DegreesToRadians(lon - centerLon) * radius * cosine;
        double y = DegreesToRadians(lat - centerLat) * radius;
        return (x, y);
    }

    /// <summary>
    /// Converts local metres relative to a map centre back to latitude/longitude, ported from
    /// <c>MapperState.xyll</c>. The exact inverse of <see cref="LocalFromGeographic"/>.
    /// </summary>
    /// <param name="centerLat">Map centre latitude in degrees.</param>
    /// <param name="centerLon">Map centre longitude in degrees.</param>
    /// <param name="radius">Planet radius in metres.</param>
    /// <param name="x">Local easting in metres.</param>
    /// <param name="y">Local northing in metres.</param>
    /// <returns>Geographic <c>(lat, lon)</c> in degrees.</returns>
    public static (double Lat, double Lon) GeographicFromLocal(double centerLat, double centerLon, double radius, double x, double y)
    {
        double cosine = Math.Cos(DegreesToRadians(centerLat));
        double lat = centerLat + RadiansToDegrees(y / radius);
        double lon = centerLon + RadiansToDegrees(x / (radius * cosine));
        return (lat, lon);
    }

    /// <summary>
    /// Great-circle distance in metres between two geographic points using the haversine
    /// formula, ported from <c>map_pml.surface_distance</c>.
    /// </summary>
    /// <param name="radius">Planet radius in metres (kept explicit so the calculation stays body-specific).</param>
    /// <param name="latA">First point latitude in degrees.</param>
    /// <param name="lonA">First point longitude in degrees.</param>
    /// <param name="latB">Second point latitude in degrees.</param>
    /// <param name="lonB">Second point longitude in degrees.</param>
    /// <returns>Great-circle distance in metres.</returns>
    /// <remarks>
    /// The final <c>Math.Min(1.0, ...)</c> protects <see cref="Math.Asin(double)"/> from a tiny
    /// floating-point overshoot past <c>1.0</c> for near-antipodal/near-identical points, exactly
    /// as Python's <c>min(1, math.sqrt(value))</c> does.
    /// </remarks>
    public static double SurfaceDistance(double radius, double latA, double lonA, double latB, double lonB)
    {
        double dLat = DegreesToRadians(latB - latA);
        double dLon = DegreesToRadians(lonB - lonA);
        double value = Math.Pow(Math.Sin(dLat / 2.0), 2.0)
            + Math.Cos(DegreesToRadians(latA)) * Math.Cos(DegreesToRadians(latB)) * Math.Pow(Math.Sin(dLon / 2.0), 2.0);
        return 2.0 * radius * Math.Asin(Math.Min(1.0, Math.Sqrt(value)));
    }

    /// <summary>
    /// Returns the signed angular error from <paramref name="current"/> heading to
    /// <paramref name="target"/> bearing, ported from <c>MapperState.heading_error</c>.
    /// </summary>
    /// <param name="current">Current heading in degrees.</param>
    /// <param name="target">Target bearing in degrees.</param>
    /// <returns>
    /// Signed error in the half-open range <c>[-180, 180)</c> degrees; negative means "turn
    /// left". The <c>+540 modulo 360 - 180</c> construction crosses the 359°/000° boundary
    /// without a branch, exactly as the Python one-liner does.
    /// </returns>
    public static double HeadingError(double current, double target)
    {
        return Modulo(target - current + 540.0, 360.0) - 180.0;
    }

    /// <summary>
    /// Returns the smallest signed angular difference between two bearings, in the half-open
    /// range <c>[-180, 180)</c> degrees. A small general-purpose companion to
    /// <see cref="HeadingError"/> used wherever two arbitrary angles (not "current vs target
    /// heading") need comparing.
    /// </summary>
    public static double AngleDelta(double from, double to) => HeadingError(from, to);

    /// <summary>
    /// Projects a destination point a given distance and bearing from an origin, using the
    /// same local equirectangular projection as <see cref="LocalFromGeographic"/> (not a
    /// great-circle projection), so a destination computed here stays consistent with the local
    /// metre coordinates the rest of the map uses.
    /// </summary>
    /// <param name="originX">Origin local easting in metres.</param>
    /// <param name="originY">Origin local northing in metres.</param>
    /// <param name="bearingDegrees">Bearing in degrees, where 0 is north and values increase clockwise.</param>
    /// <param name="distanceMetres">Distance in metres along that bearing.</param>
    /// <returns>Destination local <c>(x, y)</c> in metres.</returns>
    /// <remarks>
    /// Uses the same <c>x = originX + distance·sin(angle)</c>, <c>y = originY + distance·cos(angle)</c>
    /// construction as the circular search route in <c>MapperState.update_next</c>, generalised
    /// to an arbitrary origin/bearing/distance for future callers (radar, steering). Note that
    /// <see cref="Services.SearchRouteCalculator"/> itself does <em>not</em> call this method: its
    /// per-point angle is the sum of two terms (<c>radians(azimuth) + index · step</c>), and
    /// reaching that angle via this method's single <c>radians(bearingDegrees)</c> call would
    /// reorder the floating-point operations relative to Python's formula. Use this method only
    /// when the bearing is a single already-combined value.
    /// </remarks>
    public static (double X, double Y) DestinationPoint(double originX, double originY, double bearingDegrees, double distanceMetres)
    {
        double angle = DegreesToRadians(bearingDegrees);
        return (originX + (distanceMetres * Math.Sin(angle)), originY + (distanceMetres * Math.Cos(angle)));
    }

    /// <summary>
    /// Converts degrees to radians, exposed publicly so callers that must build up an angle from
    /// several additive terms (for example <c>SearchRouteCalculator</c>'s per-point bearing) can
    /// replicate Python's exact operation order — <c>radians(a) + b</c>, not
    /// <c>DestinationPoint</c>'s single-shot <c>radians(a + b)</c> — rather than relying on the
    /// internal <see cref="DegreesToRadiansFactor"/> being equal under reassociation.
    /// </summary>
    public static double ToRadians(double degrees) => DegreesToRadians(degrees);

    /// <summary>Converts radians to degrees using the same precomputed factor as <c>math.degrees</c>; see <see cref="ToRadians"/>.</summary>
    public static double ToDegrees(double radians) => RadiansToDegrees(radians);

    private static double DegreesToRadians(double degrees) => degrees * DegreesToRadiansFactor;

    private static double RadiansToDegrees(double radians) => radians * RadiansToDegreesFactor;

    /// <summary>
    /// Precomputed exactly as CPython's internal <c>degToRad = Py_MATH_PI / 180.0</c> constant,
    /// so <see cref="DegreesToRadians"/> performs the same single multiplication Python's
    /// <c>math.radians</c> does rather than a differently-grouped <c>degrees * Math.PI / 180.0</c>
    /// (mathematically equal, but not guaranteed bit-identical due to floating-point
    /// non-associativity) — numeric fidelity requires avoiding that reordering.
    /// </summary>
    private const double DegreesToRadiansFactor = Math.PI / 180.0;

    /// <summary>Precomputed exactly as CPython's internal <c>radToDeg = 180.0 / Py_MATH_PI</c> constant; see <see cref="DegreesToRadiansFactor"/>.</summary>
    private const double RadiansToDegreesFactor = 180.0 / Math.PI;

    /// <summary>
    /// A modulo that always returns a non-negative result for a positive modulus, matching
    /// Python's <c>%</c> operator semantics (C#'s <c>%</c> operator can return a negative result
    /// for a negative dividend, unlike Python's).
    /// </summary>
    private static double Modulo(double value, double modulus) => ((value % modulus) + modulus) % modulus;
}
