using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoSurfaceMapper.Infrastructure.Persistence;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for every map/preferences document this layer
/// reads or writes, guaranteeing the design's numeric-fidelity requirement (N4): byte-compatible
/// JSON regardless of which implementation (Python or .NET) last wrote a file.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description><see cref="JsonSerializerOptions.WriteIndented"/> reproduces Python's <c>json.dump(indent=2)</c> two-space indentation.</description></item>
///   <item><description><see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> reproduces <c>ensure_ascii=False</c>: non-ASCII characters (accented names, "á") are written literally rather than as <c>\uXXXX</c> escapes.</description></item>
///   <item><description><see cref="JsonNumberHandling.AllowReadingFromString"/> reproduces Python's lenient <c>float(value)</c>/<c>int(value)</c> coercion for concretely-typed numeric DTO properties (for example a hand-edited <c>"planet_radius": "6371000"</c> still loads).</description></item>
///   <item><description><see cref="JsonNamingPolicy.SnakeCaseLower"/> is the fallback naming policy for any property without an explicit <see cref="JsonPropertyNameAttribute"/>; every DTO property in this namespace that must match a specific persisted key (for example the abbreviated <c>coverage_width_m</c>) carries that attribute explicitly instead of relying on the policy, exactly as the design's shared-options snippet specifies.</description></item>
///   <item><description><see cref="RawValueJsonConverter"/> is registered so DTO fields boxed as <see cref="object"/>? round-trip their exact dynamic type — see that converter's remarks.</description></item>
/// </list>
/// <para>
/// Doubles are written using <see cref="JsonSerializerOptions"/>'s default "shortest
/// round-trippable" formatting (no custom number converter overrides it), which is what keeps
/// maps written by either implementation interchangeable.
/// </para>
/// </remarks>
internal static class MapJsonSerialization
{
    /// <summary>The shared options instance; reused (never re-constructed per call) per <see cref="JsonSerializerOptions"/>'s own performance guidance.</summary>
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        };
        options.Converters.Add(new RawValueJsonConverter());
        return options;
    }
}
