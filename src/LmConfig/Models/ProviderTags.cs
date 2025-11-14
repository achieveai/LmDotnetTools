namespace AchieveAi.LmDotnetTools.LmConfig.Models;

/// <summary>
/// Predefined provider tags for categorization and filtering.
/// These constants ensure consistency and prevent typos in provider configurations.
/// </summary>
public static class ProviderTags
{
    // Performance characteristics

    /// <summary>
    /// Low cost providers suitable for budget-conscious applications.
    /// </summary>
    public const string Economic = "economic";

    /// <summary>
    /// Low latency providers optimized for real-time applications.
    /// </summary>
    public const string Fast = "fast";

    /// <summary>
    /// High uptime providers with reliable service guarantees.
    /// </summary>
    public const string Reliable = "reliable";

    /// <summary>
    /// Premium providers offering the highest output quality.
    /// </summary>
    public const string HighQuality = "high-quality";

    /// <summary>
    /// Premium tier providers with the highest quality and capability.
    /// </summary>
    public const string Premium = "premium";

    /// <summary>
    /// Ultra-fast inference providers optimized for speed over cost.
    /// </summary>
    public const string UltraFast = "ultra-fast";

    // Capability tags

    /// <summary>
    /// Providers optimized for complex reasoning and problem-solving tasks.
    /// </summary>
    public const string Reasoning = "reasoning";

    /// <summary>
    /// Providers optimized for conversational interactions.
    /// </summary>
    public const string Chat = "chat";

    /// <summary>
    /// Providers optimized for code generation and programming tasks.
    /// </summary>
    public const string Coding = "coding";

    /// <summary>
    /// Providers optimized for creative writing and content generation.
    /// </summary>
    public const string Creative = "creative";

    /// <summary>
    /// Providers that support multimodal inputs (images, audio, video).
    /// </summary>
    public const string Multimodal = "multimodal";

    /// <summary>
    /// Providers with advanced thinking capabilities (internal reasoning).
    /// </summary>
    public const string Thinking = "thinking";

    /// <summary>
    /// Providers suitable for handling complex, multi-step tasks.
    /// </summary>
    public const string ComplexTasks = "complex-tasks";

    // Provider characteristics

    /// <summary>
    /// Open source model providers.
    /// </summary>
    public const string OpenSource = "open-source";

    /// <summary>
    /// Enterprise-grade providers with SLA guarantees.
    /// </summary>
    public const string Enterprise = "enterprise";

    /// <summary>
    /// Experimental or beta providers with cutting-edge features.
    /// </summary>
    public const string Experimental = "experimental";

    /// <summary>
    /// Fallback providers used when primary providers are unavailable.
    /// </summary>
    public const string Fallback = "fallback";

    /// <summary>
    /// Provider aggregators that route requests to multiple underlying providers.
    /// </summary>
    public const string Aggregator = "aggregator";

    /// <summary>
    /// Providers specializing in high-performance inference with hardware acceleration.
    /// </summary>
    public const string HighPerformance = "high-performance";

    /// <summary>
    /// Providers that are OpenAI API compatible.
    /// </summary>
    public const string OpenAiCompatible = "openai-compatible";

    /// <summary>
    /// Providers that offer multiple models from different vendors.
    /// </summary>
    public const string MultiVendor = "multi-vendor";

    /// <summary>
    /// Providers focused on providing cost-effective alternatives.
    /// </summary>
    public const string CostEffective = "cost-effective";

    /// <summary>
    /// Gets all predefined provider tags.
    /// </summary>
    /// <returns>A read-only list of all predefined tags.</returns>
    public static IReadOnlyList<string> GetAllTags()
    {
        return
        [
            // Performance characteristics
            Economic,
            Fast,
            Reliable,
            HighQuality,
            Premium,
            UltraFast,
            // Capability tags
            Reasoning,
            Chat,
            Coding,
            Creative,
            Multimodal,
            Thinking,
            ComplexTasks,
            // Provider characteristics
            OpenSource,
            Enterprise,
            Experimental,
            Fallback,
            Aggregator,
            HighPerformance,
            OpenAiCompatible,
            MultiVendor,
            CostEffective,
        ];
    }

    /// <summary>
    /// Validates that all provided tags are predefined constants.
    /// </summary>
    /// <param name="tags">Tags to validate.</param>
    /// <returns>True if all tags are valid, false otherwise.</returns>
    public static bool AreValidTags(IEnumerable<string> tags)
    {
        if (tags == null)
        {
            return true;
        }

        var validTags = GetAllTags();
        return tags.All(tag => validTags.Contains(tag));
    }

    /// <summary>
    /// Gets any invalid tags from the provided collection.
    /// </summary>
    /// <param name="tags">Tags to check.</param>
    /// <returns>A list of invalid tags, empty if all tags are valid.</returns>
    public static IReadOnlyList<string> GetInvalidTags(IEnumerable<string> tags)
    {
        if (tags == null)
        {
            return [];
        }

        var validTags = GetAllTags();
        return tags.Where(tag => !validTags.Contains(tag)).ToList();
    }

    /// <summary>
    /// Gets tags grouped by category for documentation purposes.
    /// </summary>
    /// <returns>A dictionary mapping category names to their tags.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> GetTagsByCategory()
    {
        return new Dictionary<string, IReadOnlyList<string>>
        {
            ["Performance"] = [Economic, Fast, Reliable, HighQuality, Premium, UltraFast],
            ["Capabilities"] = [Reasoning, Chat, Coding, Creative, Multimodal, Thinking, ComplexTasks],
            ["Characteristics"] =
            [
                OpenSource,
                Enterprise,
                Experimental,
                Fallback,
                Aggregator,
                HighPerformance,
                OpenAiCompatible,
                MultiVendor,
                CostEffective,
            ],
        };
    }
}
