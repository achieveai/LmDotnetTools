namespace AchieveAi.LmDotnetTools.LmCore.Agents;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Models;


public record GenerateReplyOptions
{
    public string ModelId { get; init; } = string.Empty;

    public float? TopP { get; init; }

    public string[]? Providers { get; init; }

    public bool? Stream { get; init; }

    public bool? SafePrompt { get; init; }

    public int? RandomSeed { get; init; }

    public Dictionary<string, object?> ExtraProperties { get; init; } = new Dictionary<string, object?>();

    public float? Temperature { get; init; }

    public int? MaxToken { get; init; }

    public string[]? StopSequence { get; init; }

    public FunctionContract[]? Functions { get; set; }

    public ResponseFormat? ResponseFormat { get; set; }

    /// <summary>
    /// Creates a new GenerateReplyOptions by merging this instance with properties from another instance.
    /// If a property is set in the other instance, it overrides the value in this instance.
    /// For ExtraProperties, the dictionaries are merged recursively.
    /// </summary>
    /// <param name="other">The other options to merge with.</param>
    /// <returns>A new GenerateReplyOptions with merged properties.</returns>
    public GenerateReplyOptions Merge(GenerateReplyOptions? other)
    {
        if (other == null)
        {
            return this;
        }

        // Deep merge the extra properties
        var mergedExtraProps = new Dictionary<string, object?>();
        
        // First copy the original properties
        foreach (var prop in ExtraProperties)
        {
            mergedExtraProps[prop.Key] = CloneExtraPropertyValue(prop.Value);
        }
        
        // Then merge with the other properties
        foreach (var extraProperty in other.ExtraProperties)
        {
            mergedExtraProps[extraProperty.Key] = MergeExtraPropertyValues(
                ExtraProperties.TryGetValue(extraProperty.Key, out var value) ? value : null,
                extraProperty.Value);
        }

        // Merge main properties, using other's values if they're set
        return this with
        {
            ModelId = !string.IsNullOrEmpty(other.ModelId) ? other.ModelId : ModelId,
            TopP = other.TopP ?? TopP,
            Providers = other.Providers ?? Providers,
            Stream = other.Stream ?? Stream,
            SafePrompt = other.SafePrompt ?? SafePrompt,
            RandomSeed = other.RandomSeed ?? RandomSeed,
            Temperature = other.Temperature ?? Temperature,
            MaxToken = other.MaxToken ?? MaxToken,
            StopSequence = other.StopSequence ?? StopSequence,
            Functions = other.Functions ?? Functions,
            ExtraProperties = mergedExtraProps
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
        if (original is Dictionary<string, object?> originalDict &&
            other is Dictionary<string, object?> otherDict)
        {
            var result = new Dictionary<string, object?>(originalDict);
            foreach (var kv in otherDict)
            {
                result[kv.Key] = MergeExtraPropertyValues(
                    originalDict.TryGetValue(kv.Key, out var value) ? value : null,
                    kv.Value);
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
            for (int i = 0; i < array.Length; i++)
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
