using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Agents;
/// <summary>
/// Options for generating a reply.
/// </summary>
[JsonConverter(typeof(GenerateReplyOptionsJsonConverter))]
public record GenerateReplyOptions
{
    [JsonPropertyName("model")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; init; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RandomSeed { get; init; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxToken { get; init; }

    [JsonPropertyName("stop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? StopSequence { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionContract[]? Functions { get; init; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResponseFormat? ResponseFormat { get; init; }

    /// <summary>
    /// Run ID for tracking individual agent execution runs within a conversation.
    /// Maps to the AG-UI runId concept.
    /// </summary>
    [JsonIgnore]
    public string? RunId { get; init; }

    /// <summary>
    /// Parent Run ID for branching/time travel (creates git-like lineage).
    /// </summary>
    [JsonIgnore]
    public string? ParentRunId { get; init; }

    /// <summary>
    /// Thread ID for conversation continuity.
    /// Maps to the AG-UI threadId concept.
    /// </summary>
    [JsonIgnore]
    public string? ThreadId { get; init; }

    [JsonIgnore]
    public ImmutableDictionary<string, object?> ExtraProperties { get; init; } =
        ImmutableDictionary<string, object?>.Empty;

    public GenerateReplyOptions Merge(GenerateReplyOptions? other)
    {
        if (other == null)
        {
            return this;
        }

        // Deep merge the extra properties
        var mergedExtraProps = ImmutableDictionary<string, object?>.Empty;

        // First copy the original properties
        foreach (var prop in ExtraProperties)
        {
            mergedExtraProps = mergedExtraProps.SetItem(prop.Key, CloneExtraPropertyValue(prop.Value));
        }

        // Then merge with the other properties
        foreach (var extraProperty in other.ExtraProperties)
        {
            mergedExtraProps = mergedExtraProps.SetItem(
                extraProperty.Key,
                MergeExtraPropertyValues(
                    ExtraProperties.TryGetValue(extraProperty.Key, out var value) ? value : null,
                    extraProperty.Value
                )
            );
        }

        // Merge main properties, using other's values if they're set
        return this with
        {
            ModelId = !string.IsNullOrEmpty(other.ModelId) ? other.ModelId : ModelId,
            TopP = other.TopP ?? TopP,
            RandomSeed = other.RandomSeed ?? RandomSeed,
            Temperature = other.Temperature ?? Temperature,
            MaxToken = other.MaxToken ?? MaxToken,
            StopSequence = other.StopSequence ?? StopSequence,
            Functions = other.Functions ?? Functions,
            ExtraProperties = mergedExtraProps,
        };
    }

    private static object? MergeExtraPropertyValues(object? original, object? other)
    {
        if (other == null)
        {
            return original;
        }

        if (original == null)
        {
            return CloneExtraPropertyValue(other);
        }

        // Both values are dictionaries - merge them recursively
        if (original is Dictionary<string, object?> originalDict && other is Dictionary<string, object?> otherDict)
        {
            var result = new Dictionary<string, object?>(originalDict);
            foreach (var kv in otherDict)
            {
                result[kv.Key] = MergeExtraPropertyValues(
                    originalDict.TryGetValue(kv.Key, out var value) ? value : null,
                    kv.Value
                );
            }
            return result;
        }

        // Both values are JsonNodes - deep clone and merge them
        if (original is JsonNode originalJson && other is JsonNode otherJson)
        {
            // For JsonNode, we prioritize the other value
            return otherJson.DeepClone();
        }

        // For non-dictionary types, prefer the other value
        return CloneExtraPropertyValue(other);
    }

    private static object? CloneExtraPropertyValue(object? value)
    {
        if (value == null)
        {
            return null;
        }

        if (value is Dictionary<string, object?> dict)
        {
            var clonedDict = new Dictionary<string, object?>();
            foreach (var kv in dict)
            {
                clonedDict[kv.Key] = CloneExtraPropertyValue(kv.Value);
            }
            return clonedDict;
        }

        // JsonNode has built-in deep clone
        if (value is JsonNode jsonNode)
        {
            return jsonNode.DeepClone();
        }

        // For primitive types and strings, they are value types or immutable
        if (value.GetType().IsPrimitive || value is string || value is DateTime || value is Guid)
        {
            return value;
        }

        // For arrays, create a new array and clone each element
        if (value is Array array)
        {
            var elementType = array.GetType().GetElementType();
            var clonedArray = Array.CreateInstance(elementType!, array.Length);
            for (var i = 0; i < array.Length; i++)
            {
                var element = array.GetValue(i);
                clonedArray.SetValue(CloneExtraPropertyValue(element), i);
            }
            return clonedArray;
        }

        // For other types, return as is (shallow copy) - consider implementing special handling for specific types if needed
        return value;
    }
}
