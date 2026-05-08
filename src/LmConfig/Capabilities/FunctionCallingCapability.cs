using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Capabilities;

/// <summary>
///     Represents the function calling capabilities of a model.
/// </summary>
public record FunctionCallingCapability
{
    /// <summary>
    ///     Whether the model supports function/tool calling.
    /// </summary>
    [JsonPropertyName("supports_tools")]
    public bool SupportsTools { get; init; } = false;

    /// <summary>
    ///     Whether the model supports calling multiple functions in parallel.
    /// </summary>
    [JsonPropertyName("supports_parallel_calls")]
    public bool SupportsParallelCalls { get; init; } = false;

    /// <summary>
    ///     Whether the model supports tool choice configuration (auto, none, required, specific tool).
    /// </summary>
    [JsonPropertyName("supports_tool_choice")]
    public bool SupportsToolChoice { get; init; } = false;

    /// <summary>
    ///     Maximum number of tools that can be provided in a single request.
    /// </summary>
    [JsonPropertyName("max_tools_per_request")]
    public int? MaxToolsPerRequest { get; init; }

    /// <summary>
    ///     Maximum number of function calls that can be made in a single response.
    /// </summary>
    [JsonPropertyName("max_calls_per_response")]
    public int? MaxCallsPerResponse { get; init; }

    /// <summary>
    ///     Supported tool types (e.g., "function", "code_interpreter", "retrieval").
    /// </summary>
    [JsonPropertyName("supported_tool_types")]
    public IReadOnlyList<string> SupportedToolTypes { get; init; } = [];

    /// <summary>
    ///     Whether the model supports structured schema for function parameters.
    /// </summary>
    [JsonPropertyName("supports_structured_parameters")]
    public bool SupportsStructuredParameters { get; init; } = false;

    /// <summary>
    ///     Whether the model supports function descriptions for better tool selection.
    /// </summary>
    [JsonPropertyName("supports_function_descriptions")]
    public bool SupportsFunctionDescriptions { get; init; } = false;

    /// <summary>
    ///     Whether the model supports parameter descriptions for better understanding.
    /// </summary>
    [JsonPropertyName("supports_parameter_descriptions")]
    public bool SupportsParameterDescriptions { get; init; } = false;

    /// <summary>
    ///     Whether the model supports required parameter specification.
    /// </summary>
    [JsonPropertyName("supports_required_parameters")]
    public bool SupportsRequiredParameters { get; init; } = false;

    /// <summary>
    ///     Whether the model supports custom parameter validation rules.
    /// </summary>
    [JsonPropertyName("supports_parameter_validation")]
    public bool SupportsParameterValidation { get; init; } = false;

    /// <summary>
    ///     Whether the model supports nested objects in function parameters.
    /// </summary>
    [JsonPropertyName("supports_nested_parameters")]
    public bool SupportsNestedParameters { get; init; } = false;

    /// <summary>
    ///     Whether the model supports array parameters.
    /// </summary>
    [JsonPropertyName("supports_array_parameters")]
    public bool SupportsArrayParameters { get; init; } = false;

    /// <summary>
    ///     Whether the model supports enum constraints for parameters.
    /// </summary>
    [JsonPropertyName("supports_enum_parameters")]
    public bool SupportsEnumParameters { get; init; } = false;

    /// <summary>
    ///     Maximum depth allowed for nested parameter objects.
    /// </summary>
    [JsonPropertyName("max_parameter_depth")]
    public int? MaxParameterDepth { get; init; }

    /// <summary>
    ///     Supported JSON schema versions for parameter validation.
    /// </summary>
    [JsonPropertyName("supported_schema_versions")]
    public IReadOnlyList<string> SupportedSchemaVersions { get; init; } = [];
}
