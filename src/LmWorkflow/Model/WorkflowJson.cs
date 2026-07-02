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

    /// <summary>
    ///     Strict variant of <see cref="Options"/> used only when an LLM AUTHORS a definition through the
    ///     workflow tools. It adds <see cref="JsonUnmappedMemberHandling.Disallow"/> so a misspelled or
    ///     invented field (e.g. <c>tasks</c> instead of <c>taskList</c>, <c>agentType</c> instead of
    ///     <c>subagent_type</c>) throws a <see cref="JsonException"/> naming the offending property instead
    ///     of being silently dropped — which used to leave the field null and produce a workflow that
    ///     validated clean yet did nothing. Persistence deliberately keeps using the lenient
    ///     <see cref="Options"/> so already-saved snapshots stay loadable across shape changes.
    /// </summary>
    public static JsonSerializerOptions StrictOptions { get; } = CreateStrictOptions();

    /// <summary>Deserializes a workflow definition from its JSON form.</summary>
    /// <exception cref="JsonException">The JSON is invalid or deserializes to a null definition.</exception>
    public static WorkflowDefinition Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, Options)
            ?? throw new JsonException("Workflow JSON deserialized to a null definition.");
    }

    /// <summary>
    ///     Deserializes a workflow definition, rejecting unknown/misspelled fields (see
    ///     <see cref="StrictOptions"/>). Use for the LLM authoring path so wrong field names surface as an
    ///     actionable error rather than silently vanishing.
    /// </summary>
    /// <exception cref="JsonException">
    ///     The JSON is invalid, contains an unmapped property, or deserializes to a null definition.
    /// </exception>
    public static WorkflowDefinition DeserializeStrict(string json)
    {
        ArgumentException.ThrowIfNullOrEmpty(json);
        return JsonSerializer.Deserialize<WorkflowDefinition>(json, StrictOptions)
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

    private static JsonSerializerOptions CreateStrictOptions()
    {
        var options = CreateOptions();
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }
}
