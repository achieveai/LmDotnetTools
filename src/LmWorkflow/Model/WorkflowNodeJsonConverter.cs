using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     Polymorphic converter for <see cref="WorkflowNode"/>. It peeks the <c>type</c> discriminator and
///     dispatches to the matching concrete record. An unrecognized (or absent) discriminator — including
///     out-of-V1 types such as <c>reduce</c> — is materialized as an <see cref="UnknownNode"/> so the
///     validator can reject it with a clear message rather than the deserializer throwing.
/// </summary>
public sealed class WorkflowNodeJsonConverter : JsonConverter<WorkflowNode>
{
    /// <inheritdoc />
    public override WorkflowNode? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (JsonNode.Parse(ref reader) is not JsonObject obj)
        {
            throw new JsonException("A workflow node must be a JSON object.");
        }

        var type = GetString(obj, "type");
        return type switch
        {
            "start" => obj.Deserialize<StartNode>(options),
            "procedural" => obj.Deserialize<ProceduralNode>(options),
            "conditional" => obj.Deserialize<ConditionalNode>(options),
            "terminal" => obj.Deserialize<TerminalNode>(options),
            _ => ReadUnknown(obj, type),
        };
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        WorkflowNode value,
        JsonSerializerOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        // Serialize against the concrete runtime type so every derived property is emitted. The
        // converter only intercepts the WorkflowNode base type, so this does not recurse.
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }

    private static UnknownNode ReadUnknown(JsonObject obj, string? rawType) =>
        new()
        {
            Id = GetString(obj, "id") ?? string.Empty,
            Title = GetString(obj, "title") ?? string.Empty,
            ControllerInstructions = GetString(obj, "controllerInstructions"),
            // On a round trip the node serializes its computed type ("unknown") plus the original
            // discriminator in a "rawType" property; prefer that property so the original survives, falling
            // back to the wire "type" on the first read of an out-of-V1 node.
            RawType = GetString(obj, "rawType") ?? rawType ?? string.Empty,
        };

    private static string? GetString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node)
        && node is JsonValue value
        && value.TryGetValue<string>(out var parsed)
            ? parsed
            : null;
}
