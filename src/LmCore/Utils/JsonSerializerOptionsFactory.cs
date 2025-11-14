using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmCore.Utils;

/// <summary>
/// Factory for creating JsonSerializerOptions with LmCore converters.
/// Provides base configurations that can be extended by other projects.
/// </summary>
public static class JsonSerializerOptionsFactory
{
    /// <summary>
    /// Creates JsonSerializerOptions with all LmCore-native converters registered.
    /// This includes IMessage converters, ImmutableDictionary support, and LmCore-specific converters.
    /// </summary>
    /// <param name="writeIndented">Whether to format JSON with indentation</param>
    /// <param name="namingPolicy">JSON property naming policy</param>
    /// <param name="caseInsensitive">Whether property names should be case-insensitive</param>
    /// <param name="allowTrailingCommas">Whether to allow trailing commas in JSON</param>
    /// <returns>JsonSerializerOptions with LmCore converters registered</returns>
    public static JsonSerializerOptions CreateBase(
        bool writeIndented = false,
        JsonNamingPolicy? namingPolicy = null,
        bool caseInsensitive = false,
        bool allowTrailingCommas = true
    )
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = writeIndented,
            PropertyNameCaseInsensitive = caseInsensitive,
            AllowTrailingCommas = allowTrailingCommas,
        };

        if (namingPolicy != null)
        {
            options.PropertyNamingPolicy = namingPolicy;
        }

        AddLmCoreConverters(options);
        return options;
    }

    /// <summary>
    /// Adds all LmCore-native converters to the provided JsonSerializerOptions.
    /// This method can be used by other projects to extend their own configurations.
    /// </summary>
    /// <param name="options">The JsonSerializerOptions to add converters to</param>
    public static void AddLmCoreConverters(JsonSerializerOptions options)
    {
        // IMessage polymorphic converters
        options.Converters.Add(new IMessageJsonConverter());
        options.Converters.Add(new TextMessageJsonConverter());
        options.Converters.Add(new ImageMessageJsonConverter());
        options.Converters.Add(new CompositeMessageJsonConverter());
        options.Converters.Add(new ToolsCallMessageJsonConverter());
        options.Converters.Add(new ToolsCallUpdateMessageJsonConverter());
        options.Converters.Add(new ToolsCallResultMessageJsonConverter());
        options.Converters.Add(new ToolsCallAggregateMessageJsonConverter());
        options.Converters.Add(new TextUpdateMessageJsonConverter());
        options.Converters.Add(new UsageMessageJsonConverter());

        // Collection converters
        options.Converters.Add(new ImmutableDictionaryJsonConverterFactory());

        // LmCore enum converters
        options.Converters.Add(new JsonPropertyNameEnumConverter<Role>());

        // Generic Union converters (using only built-in .NET types)
        options.Converters.Add(new UnionJsonConverter<string, IReadOnlyList<string>>());

        // Domain-specific converters
        options.Converters.Add(new GenerateReplyOptionsJsonConverter());
        options.Converters.Add(new UsageShadowPropertiesJsonConverter());
        options.Converters.Add(new ExtraPropertiesConverter());
    }

    /// <summary>
    /// Creates JsonSerializerOptions optimized for production use.
    /// Uses compact formatting and standard settings.
    /// </summary>
    public static JsonSerializerOptions CreateForProduction() => CreateBase(writeIndented: false);

    /// <summary>
    /// Creates JsonSerializerOptions optimized for testing and debugging.
    /// Uses indented formatting for better readability.
    /// </summary>
    public static JsonSerializerOptions CreateForTesting() => CreateBase(writeIndented: true);

    /// <summary>
    /// Creates JsonSerializerOptions with case-insensitive property matching.
    /// Useful for scenarios where JSON property casing might vary.
    /// </summary>
    public static JsonSerializerOptions CreateCaseInsensitive() => CreateBase(caseInsensitive: true);

    /// <summary>
    /// Creates JsonSerializerOptions with camelCase property naming.
    /// Commonly used for web APIs and JavaScript interop.
    /// </summary>
    public static JsonSerializerOptions CreateWithCamelCase() => CreateBase(namingPolicy: JsonNamingPolicy.CamelCase);

    /// <summary>
    /// Creates JsonSerializerOptions with snake_case property naming.
    /// Commonly used for Python APIs and some REST services.
    /// </summary>
    public static JsonSerializerOptions CreateWithSnakeCase() =>
        CreateBase(namingPolicy: JsonNamingPolicy.SnakeCaseLower);

    /// <summary>
    /// Creates minimal JsonSerializerOptions with only basic settings.
    /// No LmCore converters are registered - useful for simple scenarios.
    /// </summary>
    public static JsonSerializerOptions CreateMinimal(bool writeIndented = false, JsonNamingPolicy? namingPolicy = null)
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = writeIndented,
            AllowTrailingCommas = true,
        };

        if (namingPolicy != null)
        {
            options.PropertyNamingPolicy = namingPolicy;
        }

        return options;
    }
}
