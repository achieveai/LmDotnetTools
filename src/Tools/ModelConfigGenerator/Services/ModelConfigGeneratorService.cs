using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.ModelConfigGenerator.Configuration;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.ModelConfigGenerator.Services;

/// <summary>
/// Service for generating Models.config files from OpenRouter data with filtering capabilities.
/// </summary>
public partial class ModelConfigGeneratorService
{
    private readonly OpenRouterModelService _openRouterService;
    private readonly ILogger<ModelConfigGeneratorService> _logger;

    // Model family patterns for filtering
    private static readonly Dictionary<string, Regex> ModelFamilyPatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["llama"] = MyRegex(),
        ["qwen"] = MyRegex1(),
        ["kimi"] = MyRegex2(),
        ["deepseek"] = MyRegex3(),
        ["claude"] = MyRegex4(),
        ["gpt"] = MyRegex5(),
        ["gemini"] = MyRegex6(),
        ["grok"] = MyRegex7(),
        ["glm"] = MyRegex8(),
        ["openrouter"] = MyRegex9(),
        ["mistral"] = MyRegex10(),
        ["cohere"] = MyRegex11(),
        ["yi"] = MyRegex12(),
        ["phi"] = MyRegex13(),
        ["falcon"] = MyRegex14(),
        ["wizardlm"] = MyRegex15(),
        ["vicuna"] = MyRegex16(),
        ["alpaca"] = MyRegex17(),
        ["nous"] = MyRegex18(),
    };

    // Known problematic models that consistently return 404 or cause issues
    private static readonly HashSet<string> ProblematicModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "qwen/qwen3-coder",
        "qwen/qwen3-235b-a22b-2507",
        "qwen/qwen3-30b-a3b",
        "qwen/qwen3-8b",
        "qwen/qwen3-235b-a22b",
        "thudm/glm-4-32b",
        "thudm/glm-z1-32b",
        "anthropic/claude-opus-4",
        "sentientagi/dobby-mini-unhinged-plus-llama-3.1-8b",
        "mistralai/mistral-small-3.2-24b-instruct",
    };

    public ModelConfigGeneratorService(
        OpenRouterModelService openRouterService,
        ILogger<ModelConfigGeneratorService> logger
    )
    {
        _openRouterService = openRouterService ?? throw new ArgumentNullException(nameof(openRouterService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Generates a Models.config file based on the provided options.
    /// </summary>
    public async Task<bool> GenerateConfigAsync(GeneratorOptions options, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation(
                "Starting model config generation with options: {Options}",
                JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true })
            );

            // Fetch all models from OpenRouter
            _logger.LogInformation("Fetching models from OpenRouter API...");
            var allModels = await _openRouterService.GetModelConfigsAsync(cancellationToken);
            _logger.LogInformation("Retrieved {Count} models from OpenRouter", allModels.Count);

            // Apply filters
            var filteredModels = ApplyFilters(allModels, options);
            _logger.LogInformation("After filtering: {Count} models remaining", filteredModels.Count);

            if (!filteredModels.Any())
            {
                _logger.LogWarning("No models match the specified criteria. No config file will be generated.");
                return false;
            }

            // Create AppConfig with filtered models
            var appConfig = CreateAppConfig(filteredModels);

            // Serialize and save
            await SaveConfigAsync(appConfig, options, cancellationToken);

            _logger.LogInformation(
                "Successfully generated Models.config at {Path} with {Count} models",
                options.OutputPath,
                filteredModels.Count
            );

            // Log statistics
            LogGenerationStatistics(filteredModels, options);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate Models.config");
            return false;
        }
    }

    /// <summary>
    /// Applies all configured filters to the model list.
    /// </summary>
    private IReadOnlyList<ModelConfig> ApplyFilters(IReadOnlyList<ModelConfig> models, GeneratorOptions options)
    {
        var filtered = models.AsEnumerable();

        // Filter by model families
        if (options.ModelFamilies.Any())
        {
            filtered = filtered.Where(model => MatchesAnyFamily(model, options.ModelFamilies));
            _logger.LogDebug(
                "Filtered by families {Families}: {Count} models remaining",
                string.Join(", ", options.ModelFamilies),
                filtered.Count()
            );
        }

        // Filter by reasoning capability
        if (options.ReasoningOnly)
        {
            filtered = filtered.Where(model => model.IsReasoning || model.HasCapability("thinking"));
            _logger.LogDebug("Filtered by reasoning only: {Count} models remaining", filtered.Count());
        }

        // Filter by multimodal capability
        if (options.MultimodalOnly)
        {
            filtered = filtered.Where(model => model.HasCapability("multimodal"));
            _logger.LogDebug("Filtered by multimodal only: {Count} models remaining", filtered.Count());
        }

        // Filter by minimum context length
        if (options.MinContextLength > 0)
        {
            filtered = filtered.Where(model =>
                model.Capabilities?.TokenLimits?.MaxContextTokens >= options.MinContextLength
            );
            _logger.LogDebug(
                "Filtered by min context length {MinContext}: {Count} models remaining",
                options.MinContextLength,
                filtered.Count()
            );
        }

        // Filter by maximum cost
        if (options.MaxCostPerMillion > 0)
        {
            filtered = filtered.Where(model =>
                model.Providers.Any(p => (decimal)p.Pricing.PromptPerMillion <= options.MaxCostPerMillion)
            );
            _logger.LogDebug(
                "Filtered by max cost {MaxCost}: {Count} models remaining",
                options.MaxCostPerMillion,
                filtered.Count()
            );
        }

        // Filter by model update date
        if (options.ModelUpdatedSince.HasValue)
        {
            var beforeCount = filtered.Count();
            filtered = filtered.Where(model =>
                model.CreatedDate.HasValue && model.CreatedDate.Value.Date >= options.ModelUpdatedSince.Value.Date
            );
            var afterCount = filtered.Count();
            _logger.LogDebug(
                "Filtered by models updated since {Date}: {Count} models remaining ({ExcludedCount} excluded)",
                options.ModelUpdatedSince.Value.ToShortDateString(),
                afterCount,
                beforeCount - afterCount
            );
        }

        // Apply max models limit
        if (options.MaxModels > 0)
        {
            filtered = filtered.Take(options.MaxModels);
            _logger.LogDebug("Limited to max {MaxModels} models", options.MaxModels);
        }

        return filtered.ToList();
    }

    /// <summary>
    /// Checks if a model matches any of the specified families.
    /// </summary>
    private static bool MatchesAnyFamily(ModelConfig model, IReadOnlyList<string> families)
    {
        return families.Any(family => MatchesFamily(model, family));
    }

    /// <summary>
    /// Checks if a model matches a specific family pattern.
    /// </summary>
    private static bool MatchesFamily(ModelConfig model, string family)
    {
        if (!ModelFamilyPatterns.TryGetValue(family, out var pattern))
        {
            // If no predefined pattern, treat as a simple string match
            return model.Id.Contains(family, StringComparison.OrdinalIgnoreCase);
        }

        return pattern.IsMatch(model.Id);
    }

    /// <summary>
    /// Creates an AppConfig with the filtered models and standard provider registry.
    /// </summary>
    private static AppConfig CreateAppConfig(IReadOnlyList<ModelConfig> models)
    {
        // Create a standard provider registry with common providers
        var providerRegistry = new Dictionary<string, ProviderConnectionInfo>
        {
            ["OpenAI"] = new()
            {
                EndpointUrl = "https://api.openai.com/v1",
                ApiKeyEnvironmentVariable = "OPENAI_API_KEY",
                Compatibility = "OpenAI",
                Headers = null,
                Timeout = TimeSpan.FromMinutes(1),
                MaxRetries = 3,
                Description = "Official OpenAI API endpoint",
            },
            ["Anthropic"] = new()
            {
                EndpointUrl = "https://api.anthropic.com",
                ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY",
                Compatibility = "Anthropic",
                Headers = new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" },
                Timeout = TimeSpan.FromMinutes(2),
                MaxRetries = 3,
                Description = "Official Anthropic API endpoint",
            },
            ["OpenRouter"] = new()
            {
                EndpointUrl = "https://openrouter.ai/api/v1",
                ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
                Compatibility = "OpenAI",
                Headers = new Dictionary<string, string>
                {
                    ["HTTP-Referer"] = "https://github.com/achieveai/LmDotnetTools",
                    ["X-Title"] = "LmDotnetTools",
                },
                Timeout = TimeSpan.FromMinutes(2),
                MaxRetries = 3,
                Description = "OpenRouter API providing access to multiple LLM providers",
            },
            ["Google"] = new()
            {
                EndpointUrl = "https://generativelanguage.googleapis.com/v1beta",
                ApiKeyEnvironmentVariable = "GOOGLE_API_KEY",
                Compatibility = "Google",
                Headers = null,
                Timeout = TimeSpan.FromMinutes(1),
                MaxRetries = 3,
                Description = "Google Generative AI API",
            },
            ["Cohere"] = new()
            {
                EndpointUrl = "https://api.cohere.ai/v1",
                ApiKeyEnvironmentVariable = "COHERE_API_KEY",
                Compatibility = "Cohere",
                Headers = null,
                Timeout = TimeSpan.FromMinutes(1),
                MaxRetries = 3,
                Description = "Cohere API endpoint",
            },
        };

        return new AppConfig { Models = models, ProviderRegistry = providerRegistry };
    }

    /// <summary>
    /// Serializes and saves the AppConfig to the specified file.
    /// </summary>
    private async Task SaveConfigAsync(
        AppConfig appConfig,
        GeneratorOptions options,
        CancellationToken cancellationToken
    )
    {
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = options.FormatJson,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath));
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var json = JsonSerializer.Serialize(appConfig, jsonOptions);
        await File.WriteAllTextAsync(options.OutputPath, json, cancellationToken);

        _logger.LogInformation(
            "Saved config to {Path} ({Size} bytes)",
            Path.GetFullPath(options.OutputPath),
            json.Length
        );
    }

    /// <summary>
    /// Logs detailed statistics about the generated configuration.
    /// </summary>
    private void LogGenerationStatistics(IReadOnlyList<ModelConfig> models, GeneratorOptions options)
    {
        _logger.LogInformation("=== Generation Statistics ===");

        // Count by family
        var familyCounts = new Dictionary<string, int>();
        foreach (var model in models)
        {
            foreach (var (family, pattern) in ModelFamilyPatterns)
            {
                if (pattern.IsMatch(model.Id))
                {
                    familyCounts[family] = familyCounts.GetValueOrDefault(family) + 1;
                    break; // Only count in the first matching family
                }
            }
        }

        _logger.LogInformation("Models by family: {@FamilyCounts}", familyCounts.OrderByDescending(x => x.Value));

        // Count by capabilities
        var reasoningCount = models.Count(m => m.IsReasoning || m.HasCapability("thinking"));
        var multimodalCount = models.Count(m => m.HasCapability("multimodal"));
        var functionCallingCount = models.Count(m => m.HasCapability("function_calling"));

        _logger.LogInformation(
            "Model capabilities: {@Capabilities}",
            new
            {
                Reasoning = reasoningCount,
                Multimodal = multimodalCount,
                FunctionCalling = functionCallingCount,
                Total = models.Count,
            }
        );

        // Provider distribution
        var providerCounts = models
            .SelectMany(m => m.Providers.Select(p => p.Name))
            .GroupBy(name => name)
            .ToDictionary(g => g.Key, g => g.Count());

        _logger.LogInformation(
            "Provider distribution: {@ProviderCounts}",
            providerCounts.OrderByDescending(x => x.Value)
        );

        // Cost and context analysis
        var costs = models.SelectMany(m => m.Providers.Select(p => p.Pricing.PromptPerMillion)).ToList();
        var contexts = models
            .Where(m => m.Capabilities?.TokenLimits?.MaxContextTokens > 0)
            .Select(m => m.Capabilities!.TokenLimits!.MaxContextTokens)
            .ToList();

        if (costs.Count != 0 && contexts.Count != 0)
        {
            _logger.LogInformation(
                "Cost and context analysis: {@Analysis}",
                new
                {
                    CostStats = new
                    {
                        Average = Math.Round(costs.Average(), 4),
                        Min = costs.Min(),
                        Max = costs.Max(),
                        Median = costs.OrderBy(x => x).Skip(costs.Count / 2).First(),
                    },
                    ContextStats = new
                    {
                        Average = Math.Round(contexts.Average()),
                        Min = contexts.Min(),
                        Max = contexts.Max(),
                        Median = contexts.OrderBy(x => x).Skip(contexts.Count / 2).First(),
                    },
                }
            );
        }

        // Detailed model list for debugging
        if (options.Verbose)
        {
            _logger.LogDebug(
                "Generated models: {@ModelDetails}",
                models.Select(m => new
                {
                    m.Id,
                    Family = GetModelFamily(m.Id),
                    IsReasoning = m.IsReasoning,
                    HasMultimodal = m.HasCapability("multimodal"),
                    ContextLength = m.Capabilities?.TokenLimits?.MaxContextTokens,
                    ProviderCount = m.Providers.Count,
                    MinCost = m.Providers.Min(p => p.Pricing.PromptPerMillion),
                })
            );
        }
    }

    /// <summary>
    /// Gets all supported model families.
    /// </summary>
    public static IReadOnlyList<string> GetSupportedFamilies()
    {
        return ModelFamilyPatterns.Keys.ToList();
    }

    /// <summary>
    /// Gets the family name for a model ID.
    /// </summary>
    private static string GetModelFamily(string modelId)
    {
        foreach (var (family, pattern) in ModelFamilyPatterns)
        {
            if (pattern.IsMatch(modelId))
            {
                return family;
            }
        }
        return "unknown";
    }

    [GeneratedRegex(@"llama|meta-llama", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex();

    [GeneratedRegex(@"qwen|alibaba", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex1();

    [GeneratedRegex(@"kimi|moonshot", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex2();

    [GeneratedRegex(@"deepseek", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex3();

    [GeneratedRegex(@"claude|anthropic", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex4();

    [GeneratedRegex(@"gpt|openai", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex5();

    [GeneratedRegex(@"gemini|google", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex6();

    [GeneratedRegex(@"grok|xai", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex7();

    [GeneratedRegex(@"glm|thudm|chatglm", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex8();

    [GeneratedRegex(@"openrouter/|cloaked", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex9();

    [GeneratedRegex(@"mistral", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex10();

    [GeneratedRegex(@"cohere|command", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex11();

    [GeneratedRegex(@"yi-|01-ai", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex12();

    [GeneratedRegex(@"phi-|microsoft", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex13();

    [GeneratedRegex(@"falcon", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex14();

    [GeneratedRegex(@"wizard", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex15();

    [GeneratedRegex(@"vicuna", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex16();

    [GeneratedRegex(@"alpaca", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex17();

    [GeneratedRegex(@"nous|hermes", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-US")]
    private static partial Regex MyRegex18();
}
