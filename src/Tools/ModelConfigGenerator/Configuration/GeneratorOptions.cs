namespace AchieveAi.LmDotnetTools.ModelConfigGenerator.Configuration;

/// <summary>
///     Configuration options for the model config generator.
/// </summary>
public record GeneratorOptions
{
    /// <summary>
    ///     Output file path for the generated Models.config file.
    /// </summary>
    public string OutputPath { get; init; } = "Models.config";

    /// <summary>
    ///     Model families to include in the output. If empty, all families are included.
    ///     Examples: "llama", "qwen", "kimi", "deepseek", "claude", "gpt", "gemini"
    /// </summary>
    public IReadOnlyList<string> ModelFamilies { get; init; } = [];

    /// <summary>
    ///     Whether to include detailed capabilities information in the output.
    /// </summary>
    public bool IncludeCapabilities { get; init; } = true;

    /// <summary>
    ///     Whether to format the JSON output with indentation for readability.
    /// </summary>
    public bool FormatJson { get; init; } = true;

    /// <summary>
    ///     Whether to enable verbose logging.
    /// </summary>
    public bool Verbose { get; init; } = false;

    /// <summary>
    ///     Maximum number of models to include. If 0, no limit is applied.
    /// </summary>
    public int MaxModels { get; init; } = 0;

    /// <summary>
    ///     Whether to include only reasoning models.
    /// </summary>
    public bool ReasoningOnly { get; init; } = false;

    /// <summary>
    ///     Whether to include only multimodal models.
    /// </summary>
    public bool MultimodalOnly { get; init; } = false;

    /// <summary>
    ///     Minimum context length to include. Models with smaller context will be filtered out.
    /// </summary>
    public int MinContextLength { get; init; } = 0;

    /// <summary>
    ///     Maximum cost per million tokens. Models more expensive than this will be filtered out.
    /// </summary>
    public decimal MaxCostPerMillion { get; init; } = 0;

    /// <summary>
    ///     Filter models to only include those updated since this date.
    ///     Models without date information will be excluded.
    /// </summary>
    public DateTime? ModelUpdatedSince { get; init; }
}
