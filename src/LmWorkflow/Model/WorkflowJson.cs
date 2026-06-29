using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Model;

/// <summary>
///     Centralized JSON serialization for workflow definitions. Exposes the single shared
///     <see cref="Options"/> (camelCase, case-insensitive, string enums, plus the polymorphic node and
///     tolerant condition converters) and convenience <see cref="Deserialize"/>/<see cref="Serialize"/>
///     helpers so the wire contract is defined in exactly one place.
/// </summary>
public static class WorkflowJson
{
    /// <summary>The shared serializer options that define the workflow wire contract.</summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>Deserializes a workflow definition from its JSON form.</summary>
    /// <exception cref="JsonException">The JSON is invalid or deserializes to a null definition.</exception>
    public static WorkflowDefinition Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, Options)
            ?? throw new JsonException("Workflow JSON deserialized to a null definition.");
    }

    /// <summary>Serializes a workflow definition to its JSON form.</summary>
    public static string Serialize(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, Options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new WorkflowNodeJsonConverter());
        options.Converters.Add(new ConditionJsonConverter());
        return options;
    }
}
