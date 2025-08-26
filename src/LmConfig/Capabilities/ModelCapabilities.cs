using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Capabilities;

/// <summary>
/// Represents the complete capabilities of a language model.
/// </summary>
public record ModelCapabilities
{
    /// <summary>
    /// Thinking/reasoning capabilities of the model.
    /// </summary>
    [JsonPropertyName("thinking")]
    public ThinkingCapability? Thinking { get; init; }

    /// <summary>
    /// Multimodal capabilities for image/audio/video support.
    /// </summary>
    [JsonPropertyName("multimodal")]
    public MultimodalCapability? Multimodal { get; init; }

    /// <summary>
    /// Function calling capabilities for tool support.
    /// </summary>
    [JsonPropertyName("function_calling")]
    public FunctionCallingCapability? FunctionCalling { get; init; }

    /// <summary>
    /// Context and token limits for the model.
    /// </summary>
    [JsonPropertyName("token_limits")]
    public required TokenLimits TokenLimits { get; init; }

    /// <summary>
    /// Response format capabilities for structured outputs.
    /// </summary>
    [JsonPropertyName("response_formats")]
    public ResponseFormatCapability? ResponseFormats { get; init; }

    /// <summary>
    /// Whether the model supports streaming responses.
    /// </summary>
    [JsonPropertyName("supports_streaming")]
    public bool SupportsStreaming { get; init; } = true;

    /// <summary>
    /// Additional model-specific features (e.g., "thinking", "multimodal", "function-calling").
    /// </summary>
    [JsonPropertyName("supported_features")]
    public IReadOnlyList<string> SupportedFeatures { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Performance characteristics of the model.
    /// </summary>
    [JsonPropertyName("performance")]
    public PerformanceCharacteristics? Performance { get; init; }

    /// <summary>
    /// Model version or API version information.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>
    /// Whether the model is currently in preview/beta status.
    /// </summary>
    [JsonPropertyName("is_preview")]
    public bool IsPreview { get; init; } = false;

    /// <summary>
    /// Whether the model is deprecated and may be removed in the future.
    /// </summary>
    [JsonPropertyName("is_deprecated")]
    public bool IsDeprecated { get; init; } = false;

    /// <summary>
    /// Additional custom capabilities or provider-specific features.
    /// </summary>
    [JsonPropertyName("custom_capabilities")]
    public IDictionary<string, object>? CustomCapabilities { get; init; }

    /// <summary>
    /// Checks if the model has a specific capability or multiple capabilities.
    /// </summary>
    /// <param name="capability">The capability to check for. Multiple capabilities can be specified separated by ',' or ';'.</param>
    /// <returns>True if the model has all specified capabilities, false otherwise.</returns>
    public bool HasCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
            return false;

        // Split by comma or semicolon and check all capabilities
        var capabilities = capability
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(c => c.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c));

        return capabilities.All(HasSingleCapability);
    }

    /// <summary>
    /// Checks if the model has a single specific capability.
    /// </summary>
    /// <param name="capability">The single capability to check for.</param>
    /// <returns>True if the model has the capability, false otherwise.</returns>
    private bool HasSingleCapability(string capability)
    {
        return capability.ToLowerInvariant() switch
        {
            "thinking" => Thinking?.Type != ThinkingType.None,
            "multimodal" => Multimodal?.SupportsImages == true
                || Multimodal?.SupportsAudio == true
                || Multimodal?.SupportsVideo == true,
            "function_calling" or "tools" => FunctionCalling?.SupportsTools == true,
            "streaming" => SupportsStreaming,
            "json_mode" => ResponseFormats?.SupportsJsonMode == true,
            "json_schema" => ResponseFormats?.SupportsJsonSchema == true,
            "structured_output" => ResponseFormats?.SupportsStructuredOutput == true,
            _ => SupportedFeatures.Contains(capability)
                || CustomCapabilities?.ContainsKey(capability) == true,
        };
    }

    /// <summary>
    /// Gets all supported capabilities as a list of strings.
    /// </summary>
    /// <returns>A list of all capabilities supported by the model.</returns>
    public IReadOnlyList<string> GetAllCapabilities()
    {
        var capabilities = new List<string>();

        if (Thinking?.Type != ThinkingType.None)
            capabilities.Add("thinking");

        if (
            Multimodal?.SupportsImages == true
            || Multimodal?.SupportsAudio == true
            || Multimodal?.SupportsVideo == true
        )
            capabilities.Add("multimodal");

        if (FunctionCalling?.SupportsTools == true)
            capabilities.Add("function_calling");

        if (SupportsStreaming)
            capabilities.Add("streaming");

        if (ResponseFormats?.SupportsJsonMode == true)
            capabilities.Add("json_mode");

        if (ResponseFormats?.SupportsJsonSchema == true)
            capabilities.Add("json_schema");

        if (ResponseFormats?.SupportsStructuredOutput == true)
            capabilities.Add("structured_output");

        capabilities.AddRange(SupportedFeatures);

        if (CustomCapabilities != null)
            capabilities.AddRange(CustomCapabilities.Keys);

        return capabilities.Distinct().ToList();
    }
}
