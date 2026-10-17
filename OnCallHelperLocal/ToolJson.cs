using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OnCallHelperLocal;

/// <summary>
/// Claude sometimes sends a number or a boolean for a parameter that is text (for example query: 504 for an error
/// code). Strict parsing rejected those calls with a generic error; this accepts them as text instead.
/// </summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
        JsonTokenType.True => "true",
        JsonTokenType.False => "false",
        _ => throw new JsonException($"Expected text but found {reader.TokenType}.")
    };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}

/// <summary>Serializer options used to read tool arguments.</summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // A custom options object must name its type-metadata resolver (the built-in default does it implicitly).
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new LenientStringConverter() }
    };
}
