using System.Reflection;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using AchieveAi.LmDotnetTools.LmEmbeddings.Providers.OpenAI;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using MemoryServer.Models;
using MemoryServer.Utils;
using Microsoft.Extensions.Options;
using EmbeddingOptions = AchieveAi.LmDotnetTools.LmEmbeddings.Models.EmbeddingOptions;
using RerankingOptions = AchieveAi.LmDotnetTools.LmEmbeddings.Models.RerankingOptions;

namespace MemoryServer.Services;

/// <summary>
/// Implementation of LmConfig integration service for centralized model management.
/// </summary>
public class LmConfigService : ILmConfigService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly AppConfig _appConfig;
    private readonly MemoryServerOptions _memoryOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LmConfigService> _logger;
    private readonly IModelResolver _modelResolver;
    private readonly UnifiedAgent _unifiedAgent;

    public LmConfigService(
        IOptions<MemoryServerOptions> memoryOptions,
        IServiceProvider serviceProvider,
        ILogger<LmConfigService> logger,
        IModelResolver modelResolver,
        UnifiedAgent unifiedAgent,
        IOptions<AppConfig> appConfig
    )
    {
        _memoryOptions = memoryOptions.Value;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _modelResolver = modelResolver;
        _unifiedAgent = unifiedAgent;

        // Use the shared AppConfig instance from DI
        _appConfig = appConfig.Value ?? throw new ArgumentNullException(nameof(appConfig));
    }

    /// <summary>
    /// Gets the optimal model configuration for a specific capability.
    /// </summary>
    public Task<ModelConfig?> GetOptimalModelAsync(
        string capability,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogError(
            "DEBUG: GetOptimalModelAsync called with capability: {Capability}",
            capability
        );

        var modelsWithCapability = GetModelsWithCapability(capability);
        _logger.LogError(
            "DEBUG: Found {Count} models with capability {Capability}",
            modelsWithCapability.Count,
            capability
        );

        if (!modelsWithCapability.Any())
        {
            _logger.LogWarning("No models found for capability: {Capability}", capability);
            return Task.FromResult<ModelConfig?>(null);
        }

        // Apply cost optimization if enabled
        if (_memoryOptions.LmConfig?.CostOptimization?.Enabled == true)
        {
            var maxCost = _memoryOptions.LmConfig.CostOptimization.MaxCostPerRequest;
            modelsWithCapability = modelsWithCapability
                .Where(m =>
                    (decimal)m.GetPrimaryProvider().Pricing.PromptPerMillion <= maxCost * 1_000_000
                )
                .ToList();
        }

        // Select based on fallback strategy
        var strategy = _memoryOptions.LmConfig?.FallbackStrategy ?? "cost-optimized";

        var selectedModel = strategy.ToLower() switch
        {
            "cost-optimized" => modelsWithCapability
                .OrderBy(m => m.GetPrimaryProvider().Pricing.PromptPerMillion)
                .FirstOrDefault(),
            "performance-first" => modelsWithCapability
                .OrderByDescending(m => m.GetPrimaryProvider().Priority)
                .FirstOrDefault(),
            _ => modelsWithCapability.FirstOrDefault(),
        };

        if (selectedModel != null)
        {
            _logger.LogDebug(
                "Selected model {ModelId} for capability {Capability} using strategy {Strategy}",
                selectedModel.Id,
                capability,
                strategy
            );
        }

        return Task.FromResult(selectedModel);
    }

    /// <summary>
    /// Creates an agent for a specific capability using the optimal model.
    /// </summary>
    public async Task<IAgent> CreateAgentAsync(
        string capability,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogInformation(
                "CreateAgentAsync called with capability: {Capability}",
                capability
            );

            // First get the optimal model for this capability
            var optimalModel = await GetOptimalModelAsync(capability, cancellationToken);
            _logger.LogInformation(
                "GetOptimalModelAsync returned: {ModelId}",
                optimalModel?.Id ?? "null"
            );

            if (optimalModel == null)
            {
                _logger.LogError("No optimal model found for capability: {Capability}", capability);
                throw new InvalidOperationException($"No model found for capability: {capability}");
            }

            // Return UnifiedAgent directly - it will handle model resolution, provider dispatching,
            // and model name translation when GenerateReplyAsync is called
            _logger.LogInformation(
                "Successfully created UnifiedAgent for model: {ModelId}",
                optimalModel.Id
            );

            return _unifiedAgent;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create agent for capability: {Capability}", capability);
            throw;
        }
    }

    /// <summary>
    /// Creates an agent for a specific model ID and capability, bypassing the automatic model selection.
    /// </summary>
    public Task<IAgent> CreateAgentWithModelAsync(
        string modelId,
        string capability,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            _logger.LogInformation(
                "CreateAgentWithModelAsync called with modelId: {ModelId}, capability: {Capability}",
                modelId,
                capability
            );

            // Validate that the model exists in configuration
            var model = _appConfig.GetModel(modelId);
            if (model == null)
            {
                _logger.LogError("Model {ModelId} not found in configuration", modelId);
                throw new InvalidOperationException(
                    $"Model '{modelId}' not found in configuration. Available models: {string.Join(", ", _appConfig.Models.Select(m => m.Id))}"
                );
            }

            // Validate that the model supports the required capability (optional validation)
            var capabilitySupported = model.HasCapability(capability);
            if (!capabilitySupported)
            {
                _logger.LogWarning(
                    "Model {ModelId} may not fully support capability {Capability}, but proceeding with user request",
                    modelId,
                    capability
                );
            }

            // Return UnifiedAgent directly - it will handle model resolution, provider dispatching,
            // and model name translation when GenerateReplyAsync is called with the specific modelId
            _logger.LogInformation(
                "Successfully created UnifiedAgent for specific model: {ModelId}",
                modelId
            );

            return Task.FromResult<IAgent>(_unifiedAgent);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to create agent for model: {ModelId}, capability: {Capability}",
                modelId,
                capability
            );
            throw;
        }
    }

    /// <summary>
    /// Creates an embedding service using environment variables.
    /// </summary>
    public Task<IEmbeddingService> CreateEmbeddingServiceAsync(
        CancellationToken cancellationToken = default
    )
    {
        var apiKey = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "EMBEDDING_API_KEY"
        );
        var baseUrl = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "EMBEDDING_BASE_URL",
            null,
            "https://api.openai.com/v1"
        );
        var model = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "EMBEDDING_MODEL",
            null,
            "text-embedding-3-small"
        );

        var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient("LmDotNet.Reranking");
        var logger = _serviceProvider.GetRequiredService<ILogger<OpenAIEmbeddingService>>();

        if (string.IsNullOrEmpty(apiKey) || apiKey.StartsWith("${"))
        {
            throw new InvalidOperationException(
                "Embedding API key not configured. Set EMBEDDING_API_KEY environment variable."
            );
        }

        var embeddingService = new OpenAIEmbeddingService(
            logger,
            httpClient,
            new EmbeddingOptions
            {
                ApiKey = apiKey,
                BaseUrl = baseUrl,
                DefaultModel = model,
            }
        );

        _logger.LogInformation(
            "Created embedding service using model {Model} at {BaseUrl}",
            model,
            baseUrl
        );

        return Task.FromResult(embeddingService as IEmbeddingService);
    }

    /// <summary>
    /// Creates a reranking service using environment variables.
    /// </summary>
    public Task<IRerankService> CreateRerankServiceAsync(
        CancellationToken cancellationToken = default
    )
    {
        var apiKey = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "RERANKING_API_KEY"
        );
        var baseUrl = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "RERANKING_BASE_URL",
            null,
            "https://api.jina.ai/v1/rerank"
        );
        var model = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "RERANKING_MODEL",
            null,
            "jina-reranker-m0"
        );

        var httpClientFactory = _serviceProvider.GetRequiredService<IHttpClientFactory>();
        var httpClient = httpClientFactory.CreateClient("LmDotNet.Reranking");
        var logger = _serviceProvider.GetRequiredService<ILogger<RerankingService>>();

        var options = new RerankingOptions
        {
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            DefaultModel = model,
        };

        return Task.FromResult(new RerankingService(options, logger, httpClient) as IRerankService);
    }

    /// <summary>
    /// Gets the complete application configuration.
    /// </summary>
    public AppConfig GetConfiguration()
    {
        return _appConfig;
    }

    /// <summary>
    /// Gets all models that support a specific capability.
    /// </summary>
    public IReadOnlyList<ModelConfig> GetModelsWithCapability(string capability)
    {
        return _appConfig.GetModelsWithCapability(capability);
    }

    /// <summary>
    /// Validates that required models are configured for memory operations.
    /// </summary>
    public bool ValidateRequiredModels()
    {
        var requiredCapabilities = new[] { "chat" };

        foreach (var capability in requiredCapabilities)
        {
            var models = GetModelsWithCapability(capability);
            if (!models.Any())
            {
                _logger.LogError(
                    "No models configured for required capability: {Capability}",
                    capability
                );
                return false;
            }
        }

        // Validate embedding service configuration
        var embeddingApiKey = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "EMBEDDING_API_KEY"
        );
        if (string.IsNullOrEmpty(embeddingApiKey) || embeddingApiKey.StartsWith("${"))
        {
            _logger.LogError(
                "Embedding API key not configured. Set EMBEDDING_API_KEY environment variable."
            );
            return false;
        }

        // Validate reranking service configuration (optional)
        var rerankingApiKey = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback(
            "RERANKING_API_KEY"
        );
        if (!string.IsNullOrEmpty(rerankingApiKey) && !rerankingApiKey.StartsWith("${"))
        {
            _logger.LogInformation("Reranking service configured");
        }
        else
        {
            _logger.LogWarning(
                "Reranking API key not configured. Reranking functionality will not be available."
            );
        }

        _logger.LogInformation("All required models and services validated successfully");
        return true;
    }

    /// <summary>
    /// Gets the effective model name that should be used for API calls for a specific capability.
    /// </summary>
    public async Task<string?> GetEffectiveModelNameAsync(
        string capability,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            // First get the optimal model for this capability
            var optimalModel = await GetOptimalModelAsync(capability, cancellationToken);
            if (optimalModel == null)
            {
                _logger.LogWarning(
                    "No optimal model found for capability: {Capability}",
                    capability
                );
                return null;
            }

            // Then resolve the provider for that model to get the effective model name
            var providerResolution = await _modelResolver.ResolveProviderAsync(
                optimalModel.Id,
                cancellationToken: cancellationToken
            );
            if (providerResolution == null)
            {
                _logger.LogWarning(
                    "No provider resolution found for model: {ModelId}",
                    optimalModel.Id
                );
                return null;
            }

            // Return the effective model name that should be used for API calls
            return providerResolution.EffectiveModelName;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to get effective model name for capability: {Capability}",
                capability
            );
            return null;
        }
    }

    #region Private Methods

    private AppConfig LoadAppConfig()
    {
        // First, check if AppConfig is directly provided in configuration
        if (_memoryOptions.LmConfig?.AppConfig != null)
        {
            _logger.LogInformation(
                "Using AppConfig directly provided in configuration with {ModelCount} models",
                _memoryOptions.LmConfig.AppConfig.Models.Count
            );
            return _memoryOptions.LmConfig.AppConfig;
        }

        // Try to load from embedded resource first
        try
        {
            var config = LoadFromEmbeddedResource();
            if (config != null)
            {
                _logger.LogInformation(
                    "Loaded LmConfig with {ModelCount} models from embedded resource",
                    config.Models.Count
                );
                return config;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load from embedded resource, falling back to file system"
            );
        }

        // Fallback to file system if embedded resource fails
        var configPath = _memoryOptions.LmConfig?.ConfigPath ?? "config/models.json";

        if (!File.Exists(configPath))
        {
            var assemblyPath = Path.GetDirectoryName(typeof(AppConfig).Assembly.Location)!;
            if (!File.Exists(Path.Combine(assemblyPath, configPath)))
            {
                throw new InvalidOperationException(
                    $"LmConfig not found. Either provide AppConfig directly in configuration, ensure the embedded resource exists, or ensure the file exists at: {configPath}"
                );
            }

            configPath = Path.Combine(assemblyPath, configPath);
        }

        try
        {
            var configJson = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(configJson, _jsonOptions);

            if (config?.Models?.Any() != true)
            {
                throw new InvalidOperationException(
                    $"Invalid or empty LmConfig file at {configPath}. The configuration must contain at least one model."
                );
            }

            _logger.LogInformation(
                "Loaded LmConfig with {ModelCount} models from file {ConfigPath}",
                config.Models.Count,
                configPath
            );

            return config;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse LmConfig file at {configPath}: {ex.Message}",
                ex
            );
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load LmConfig from {configPath}: {ex.Message}",
                ex
            );
        }
    }

    private AppConfig? LoadFromEmbeddedResource()
    {
        if (!EmbeddedResourceHelper.TryLoadEmbeddedResource("models.json", out var configJson))
        {
            _logger.LogDebug("Embedded resource models.json not found");
            return null;
        }

        var config = JsonSerializer.Deserialize<AppConfig>(configJson, _jsonOptions);

        if (config?.Models?.Any() != true)
        {
            throw new InvalidOperationException(
                "Invalid or empty embedded LmConfig resource. The configuration must contain at least one model."
            );
        }

        return config;
    }
    #endregion
}
