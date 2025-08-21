namespace MemoryServer.DocumentSegmentation.Models;

/// <summary>
/// Template for LLM prompts with system and user components.
/// </summary>
public class PromptTemplate
{
    /// <summary>
    /// System prompt that sets context and instructions
    /// </summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>
    /// User prompt with placeholders for dynamic content
    /// </summary>
    public string UserPrompt { get; set; } = string.Empty;

    /// <summary>
    /// Expected response format (e.g., "json", "text")
    /// </summary>
    public string ExpectedFormat { get; set; } = "json";

    /// <summary>
    /// Maximum tokens for the response
    /// </summary>
    public int MaxTokens { get; set; } = 1200;

    /// <summary>
    /// Temperature setting for response generation
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// Additional prompt configuration metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Configuration for document segmentation options.
/// </summary>
public class DocumentSegmentationOptions
{
    /// <summary>
    /// Size thresholds for determining when to segment
    /// </summary>
    public SegmentationThresholds Thresholds { get; set; } = new();

    /// <summary>
    /// LLM-specific segmentation options
    /// </summary>
    public LlmSegmentationOptions LlmOptions { get; set; } = new();

    /// <summary>
    /// Quality assessment options
    /// </summary>
    public QualityOptions Quality { get; set; } = new();

    /// <summary>
    /// Performance and optimization options
    /// </summary>
    public PerformanceOptions Performance { get; set; } = new();

    /// <summary>
    /// Prompt configuration options
    /// </summary>
    public PromptOptions Prompts { get; set; } = new();
}

/// <summary>
/// Thresholds for triggering document segmentation.
/// </summary>
public class SegmentationThresholds
{
    /// <summary>
    /// Minimum document size in words to trigger segmentation
    /// </summary>
    public int MinDocumentSizeWords { get; set; } = 1500;

    /// <summary>
    /// Maximum document size in words that can be processed
    /// </summary>
    public int MaxDocumentSizeWords { get; set; } = 50000;

    /// <summary>
    /// Target segment size in words
    /// </summary>
    public int TargetSegmentSizeWords { get; set; } = 1000;

    /// <summary>
    /// Maximum segment size in words
    /// </summary>
    public int MaxSegmentSizeWords { get; set; } = 2000;

    /// <summary>
    /// Minimum segment size in words
    /// </summary>
    public int MinSegmentSizeWords { get; set; } = 100;
}

/// <summary>
/// LLM-specific options for segmentation.
/// </summary>
public class LlmSegmentationOptions
{
    /// <summary>
    /// Whether to use LLM for segmentation (vs. rule-based fallback)
    /// </summary>
    public bool EnableLlmSegmentation { get; set; } = true;

    /// <summary>
    /// Capability name for model selection
    /// </summary>
    public string SegmentationCapability { get; set; } = "document_segmentation";

    /// <summary>
    /// Maximum retries for LLM calls
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Timeout for LLM calls in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Quality assessment options.
/// </summary>
public class QualityOptions
{
    /// <summary>
    /// Minimum coherence score threshold
    /// </summary>
    public double MinCoherenceScore { get; set; } = 0.6;

    /// <summary>
    /// Minimum independence score threshold
    /// </summary>
    public double MinIndependenceScore { get; set; } = 0.5;

    /// <summary>
    /// Minimum topic consistency score threshold
    /// </summary>
    public double MinTopicConsistencyScore { get; set; } = 0.7;

    /// <summary>
    /// Whether to validate segment quality automatically
    /// </summary>
    public bool EnableQualityValidation { get; set; } = true;
}

/// <summary>
/// Performance and optimization options.
/// </summary>
public class PerformanceOptions
{
    /// <summary>
    /// Maximum concurrent segmentation operations
    /// </summary>
    public int MaxConcurrentOperations { get; set; } = 10;

    /// <summary>
    /// Whether to enable caching of segmentation results
    /// </summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>
    /// Cache expiration time in minutes
    /// </summary>
    public int CacheExpirationMinutes { get; set; } = 60;
}

/// <summary>
/// Prompt configuration options.
/// </summary>
public class PromptOptions
{
    /// <summary>
    /// Path to the YAML prompts file
    /// </summary>
    public string FilePath { get; set; } = "prompts.yml";

    /// <summary>
    /// Default language for prompts
    /// </summary>
    public string DefaultLanguage { get; set; } = "en";

    /// <summary>
    /// Whether to enable hot reload of prompts
    /// </summary>
    public bool EnableHotReload { get; set; } = true;

    /// <summary>
    /// Cache expiration time for loaded prompts
    /// </summary>
    public TimeSpan CacheExpiration { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Custom prompt file paths for specific languages or strategies
    /// </summary>
    public Dictionary<string, string> CustomPromptPaths { get; set; } = new();
}
