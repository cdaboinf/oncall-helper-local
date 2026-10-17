using System.Buffers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

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

/// <summary>Serializer options and schema settings for the tools.</summary>
public static class ToolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // A custom options object must name its type-metadata resolver (the built-in default does it implicitly).
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new LenientStringConverter() }
    };

    /// <summary>
    /// The lenient converter makes the schema generator drop the "type" of every text parameter. Put it back, so the
    /// tool contract Claude reads (and validates against) still says text. null is allowed so that a blank value is
    /// explained by the server ("description is required") instead of by an unhelpful client-side schema error.
    /// </summary>
    public static readonly AIJsonSchemaCreateOptions Schema = new()
    {
        TransformSchemaNode = (ctx, node) =>
        {
            // The hook sees an array as one node whose items the converter left empty: say they are text.
            if (ctx.TypeInfo.Type == typeof(string[]) && node is JsonObject array && array["items"] is JsonObject items && items.Count == 0)
                items["type"] = "string";
            if (ctx.TypeInfo.Type != typeof(string)) return node;
            var typed = node as JsonObject ?? new JsonObject();
            typed["type"] = new JsonArray("string", "null");
            return typed;
        }
    };

    /// <summary>
    /// Register the [McpServerTool] methods of a class by hand, instead of WithToolsFromAssembly, so that each tool
    /// is created with the lenient serializer options AND the schema fix above.
    /// </summary>
    public static IMcpServerBuilder WithLenientTools<TTools>(this IMcpServerBuilder builder) where TTools : class
    {
        foreach (var method in typeof(TTools).GetMethods(BindingFlags.Instance | BindingFlags.Public))
        {
            if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
            builder.Services.AddSingleton(sp => McpServerTool.Create(
                method,
                ctx => ActivatorUtilities.CreateInstance<TTools>(ctx.Services ?? sp),
                new McpServerToolCreateOptions { SerializerOptions = Options, SchemaCreateOptions = Schema, Services = sp }));
        }
        return builder;
    }
}
