using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace AchieveAi.LmDotnetTools.McpMiddleware;

/// <summary>
///     JSON options for MCP content blocks that are safe to use with a derived declared type.
/// </summary>
/// <remarks>
///     <para>
///         The MCP SDK attaches its hand-written <c>ContentBlock.Converter</c> to the
///         <see cref="ContentBlock" /> base type. System.Text.Json resolves converters from the
///         <em>declared</em> type, so the converter only runs when a block is written through
///         <see cref="ContentBlock" /> - as it is inside <c>CallToolResult.Content</c>.
///     </para>
///     <para>
///         Serializing a derived block directly (<c>Serialize(imageBlock)</c>) misses that converter
///         and falls back to the default handling for <see cref="ImageContentBlock.Data" />, a
///         <c>ReadOnlyMemory&lt;byte&gt;</c>. Because <c>Data</c> already holds base64 <em>text</em>,
///         the default handling base64-encodes it a second time and emits
///         <c>"data":"aVZCT1J3..."</c> instead of the spec form <c>"data":"iVBORw0..."</c>. The result
///         round-trips within .NET, so the corruption stays invisible until a non-SDK peer reads it.
///     </para>
///     <para>
///         Use <see cref="DefaultOptions" /> for any serialization that names a derived block type.
///     </para>
/// </remarks>
public static class McpContentJson
{
    /// <summary>
    ///     The MCP SDK defaults, plus a converter that routes <see cref="ImageContentBlock" /> back
    ///     through the base-type converter so derived and base declared types agree.
    /// </summary>
    public static JsonSerializerOptions DefaultOptions { get; } = CreateDefaultOptions();

    private static JsonSerializerOptions CreateDefaultOptions()
    {
        var options = new JsonSerializerOptions(McpJsonUtilities.DefaultOptions);
        options.Converters.Add(new DerivedContentBlockConverter<ImageContentBlock>());
        options.Converters.Add(new DerivedContentBlockConverter<AudioContentBlock>());
        options.MakeReadOnly();

        return options;
    }

    /// <summary>
    ///     Delegates a derived content block to the <see cref="ContentBlock" /> converter the SDK
    ///     supplies, rather than re-implementing its wire format.
    /// </summary>
    private sealed class DerivedContentBlockConverter<TBlock> : JsonConverter<TBlock>
        where TBlock : ContentBlock
    {
        public override TBlock? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // Declared type ContentBlock, so this resolves the SDK's converter, not this one.
            var block = JsonSerializer.Deserialize<ContentBlock>(ref reader, options);

            return block switch
            {
                null => null,
                TBlock typed => typed,
                _ => throw new JsonException(
                    $"Expected a {typeof(TBlock).Name} but the payload declared type '{block.Type}'."
                ),
            };
        }

        public override void Write(Utf8JsonWriter writer, TBlock value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize<ContentBlock>(writer, value, options);
    }
}
