using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhinoSurfaceMapper.Infrastructure.Persistence;

/// <summary>
/// Converts a JSON scalar to and from a boxed <see cref="object"/>, preserving the exact
/// dynamic type the Python reference's own <c>dict</c>-based <c>json.loads</c> would have
/// produced — an <see cref="int"/> for a bare integer literal, a <see cref="double"/> for any
/// other JSON number (including an integral one written with a decimal point, such as
/// <c>3.0</c>), a <see cref="bool"/>, a <see cref="string"/>, or <see langword="null"/>.
/// </summary>
/// <remarks>
/// <para>
/// This converter exists because <c>RawMapDocument</c>'s per-marker coordinate fields and
/// <c>SearchAzimuth</c> are deliberately boxed as <see cref="object"/>? (see that type's
/// remarks): <c>MapValidator</c> must be able to tell a bare JSON integer (<c>3</c>) apart from
/// a JSON float (<c>3.0</c>) or a boolean, exactly as Python's dynamically-typed <c>dict</c>
/// lets <c>type(value) is not int</c> make that distinction. Deserializing straight into
/// <see cref="object"/> without this converter would box every JSON value as a
/// <see cref="JsonElement"/> instead, which <c>Domain.Services.NumberCoercion</c> and
/// <c>MapValidator</c> do not understand.
/// </para>
/// <para>
/// <see cref="JsonElement.TryGetInt32(out int)"/> is what supplies the "bare integer vs.
/// anything else" distinction: it parses the raw UTF-8 number text directly and returns
/// <see langword="false"/> for text containing a decimal point or exponent (such as
/// <c>"3.0"</c>), exactly matching Python's own int/float literal distinction.
/// </para>
/// </remarks>
internal sealed class RawValueJsonConverter : JsonConverter<object?>
{
    /// <inheritdoc />
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ToRawValue(document.RootElement);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            default:
                // No field routed through this converter ever holds another CLR type; see the
                // read side, which only ever produces one of the above.
                throw new JsonException($"Unsupported raw map value type '{value.GetType()}'.");
        }
    }

    /// <summary>
    /// Converts one parsed JSON scalar to the boxed CLR value that preserves its exact dynamic
    /// type, as described in this type's remarks.
    /// </summary>
    /// <exception cref="JsonException"><paramref name="element"/> is a JSON array or object — never valid for the raw scalar fields this converter serves.</exception>
    public static object? ToRawValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        // Cast the int branch to object explicitly: without it, C#'s conditional operator finds
        // the common type of `int` and `double` (double) and implicitly widens the int branch
        // before boxing, silently turning every bare integer into a boxed System.Double — which
        // would defeat the entire point of this converter. Boxing each branch to its own exact
        // type first keeps them distinct.
        JsonValueKind.Number => element.TryGetInt32(out int integer) ? (object)integer : element.GetDouble(),
        _ => throw new JsonException($"Unsupported JSON value kind '{element.ValueKind}' for a raw map value."),
    };
}
