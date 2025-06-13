namespace AchieveAi.LmDotnetTools.LmCore.Utils;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
/// Helper utilities for working with JSON schema objects
/// </summary>
public static class SchemaHelper
{
    /// <summary>
    /// Creates a JsonSchemaObject from a .NET Type
    /// </summary>
    /// <param name="type">The .NET type to convert</param>
    /// <returns>A JsonSchemaObject representing the type</returns>
    public static JsonSchemaObject CreateJsonSchemaFromType(Type type)
    {
        JsonNode dotnetSchema = JsonSerializerOptions.Default.GetJsonSchemaAsNode(type);
        string schemaJson = dotnetSchema.ToJsonString();
        var deserOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        deserOptions.Converters.Add(new JsonStringEnumConverter());
        deserOptions.Converters.Add(new UnionJsonConverter<string, IReadOnlyList<string>>());

        // Deserialize to JsonSchemaObject
        return JsonSerializer.Deserialize<JsonSchemaObject>(schemaJson, deserOptions)!;
    }
}