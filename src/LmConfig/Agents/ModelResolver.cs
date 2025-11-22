using AchieveAi.LmDotnetTools.LmConfig.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.LmConfig.Agents;

/// <summary>
/// Implementation of IModelResolver that handles complex provider resolution logic
/// based on configuration, availability, and selection criteria.
/// </summary>
public class ModelResolver : IModelResolver
{
    private readonly AppConfig _config;
    private readonly ILogger<ModelResolver> _logger;

    public ModelResolver(IOptions<AppConfig> config, ILogger<ModelResolver> logger)
    {
        _config = config.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ProviderResolution?> ResolveProviderAsync(
        string modelId,
        ProviderSelectionCriteria? criteria = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("Model ID cannot be null or empty", nameof(modelId));
        }

        _logger.LogDebug(
            "Resolving provider for model {ModelId} with criteria {Criteria}",
            modelId,
            criteria?.ToString() ?? "default"
        );

        var model = _config.GetModel(modelId);
        if (model == null)
        {
            _logger.LogWarning("Model {ModelId} not found in configuration", modelId);
            return null;
        }

        var availableProviders = await GetAvailableProvidersAsync(modelId, criteria, cancellationToken);
        var bestProvider = availableProviders.FirstOrDefault();

        if (bestProvider != null)
        {
            _logger.LogInformation(
                "Resolved provider for model {ModelId}: {Provider}",
                modelId,
                bestProvider.ToString()
            );
        }
        else
        {
            _logger.LogWarning("No suitable provider found for model {ModelId}", modelId);
        }

        return bestProvider;
    }

    public async Task<IReadOnlyList<ProviderResolution>> GetAvailableProvidersAsync(
        string modelId,
        ProviderSelectionCriteria? criteria = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("Model ID cannot be null or empty", nameof(modelId));
        }

        var model = _config.GetModel(modelId);
        if (model == null)
        {
            return [];
        }

        criteria ??= ProviderSelectionCriteria.Default;

        var resolutions = new List<ProviderResolution>();

        foreach (var provider in model.Providers)
        {
            // Check if provider should be excluded
            if (ShouldExcludeProvider(provider, criteria))
            {
                continue;
            }

            // Check if provider is available
            if (!await IsProviderAvailableAsync(provider.Name, cancellationToken))
            {
                continue;
            }

            var connection = _config.GetProviderConnection(provider.Name);
            if (connection == null)
            {
                _logger.LogWarning("No connection info found for provider {ProviderName}", provider.Name);
                continue;
            }

            // Add main provider resolution
            resolutions.Add(
                new ProviderResolution
                {
                    Model = model,
                    Provider = provider,
                    Connection = connection,
                }
            );

            // Add sub-provider resolutions if available
            if (provider.SubProviders != null)
            {
                foreach (var subProvider in provider.SubProviders)
                {
                    if (ShouldExcludeSubProvider(subProvider, criteria))
                    {
                        continue;
                    }

                    // Check if sub-provider is available (use main provider's connection)
                    if (!await IsProviderAvailableAsync(provider.Name, cancellationToken))
                    {
                        continue;
                    }

                    resolutions.Add(
                        new ProviderResolution
                        {
                            Model = model,
                            Provider = provider,
                            Connection = connection,
                            SubProvider = subProvider,
                        }
                    );
                }
            }
        }

        // Sort by preference based on criteria
        return SortProvidersByPreference(resolutions, criteria);
    }

    public Task<bool> IsProviderAvailableAsync(string providerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            return Task.FromResult(false);
        }

        var connection = _config.GetProviderConnection(providerName);
        if (connection == null)
        {
            return Task.FromResult(false);
        }

        // Validate the connection configuration
        var validation = connection.Validate();
        if (!validation.IsValid)
        {
            _logger.LogDebug(
                "Provider {ProviderName} is not available: {Errors}",
                providerName,
                string.Join(", ", validation.Errors)
            );
            return Task.FromResult(false);
        }

        // Check if API key is actually available
        var apiKey = connection.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogDebug(
                "Provider {ProviderName} is not available: API key not found in environment variable '{EnvVar}'",
                providerName,
                connection.ApiKeyEnvironmentVariable
            );
            return Task.FromResult(false);
        }

        // Provider is available if it has valid configuration and API key
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyList<ModelConfig>> GetModelsWithCapabilityAsync(
        string capability,
        ProviderSelectionCriteria? criteria = null,
        CancellationToken cancellationToken = default
    )
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            throw new ArgumentException("Capability cannot be null or empty", nameof(capability));
        }

        var modelsWithCapability = _config.GetModelsWithCapability(capability);

        if (criteria == null)
        {
            return modelsWithCapability;
        }

        // Filter models that have at least one provider matching the criteria
        var filteredModels = new List<ModelConfig>();

        foreach (var model in modelsWithCapability)
        {
            var providers = await GetAvailableProvidersAsync(model.Id, criteria, cancellationToken);
            if (providers.Any())
            {
                filteredModels.Add(model);
            }
        }

        return filteredModels;
    }

    public async Task<ProviderResolution?> ResolveByCapabilityAsync(
        string capability,
        ProviderSelectionCriteria? criteria = null,
        CancellationToken cancellationToken = default
    )
    {
        var modelsWithCapability = await GetModelsWithCapabilityAsync(capability, criteria, cancellationToken);

        foreach (var model in modelsWithCapability)
        {
            var resolution = await ResolveProviderAsync(model.Id, criteria, cancellationToken);
            if (resolution != null)
            {
                return resolution;
            }
        }

        return null;
    }

    public Task<ValidationResult> ValidateConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // Validate models
        if (!_config.Models.Any())
        {
            errors.Add("No models configured");
        }

        foreach (var model in _config.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
            {
                errors.Add("Model with empty ID found");
                continue;
            }

            if (!model.Providers.Any())
            {
                warnings.Add($"Model {model.Id} has no providers configured");
                continue;
            }

            // Track provider validation results for this model
            var providerErrors = new List<string>();
            var validProviderCount = 0;

            foreach (var provider in model.Providers)
            {
                if (string.IsNullOrWhiteSpace(provider.Name))
                {
                    providerErrors.Add($"Provider with empty name");
                    continue;
                }

                var connection = _config.GetProviderConnection(provider.Name);
                if (connection == null)
                {
                    providerErrors.Add($"No connection info found for provider {provider.Name}");
                    continue;
                }

                var connectionValidation = connection.Validate();
                if (!connectionValidation.IsValid)
                {
                    providerErrors.AddRange(connectionValidation.Errors.Select(e => $"Provider {provider.Name}: {e}"));
                }
                else
                {
                    validProviderCount++;
                }

                // Always add warnings regardless of provider validity
                warnings.AddRange(
                    connectionValidation.Warnings.Select(w => $"Provider {provider.Name} in model {model.Id}: {w}")
                );
            }

            // Only add model-level error if ALL providers have errors
            if (validProviderCount == 0 && providerErrors.Count != 0)
            {
                errors.Add($"Model {model.Id} has no valid providers - all providers failed validation:");
                errors.AddRange(providerErrors.Select(e => $"  - {e}"));
            }
            else if (providerErrors.Count != 0)
            {
                // If at least one provider is valid, treat invalid providers as warnings
                warnings.Add(
                    $"Model {model.Id} has {providerErrors.Count} invalid provider(s) but {validProviderCount} valid provider(s):"
                );
                warnings.AddRange(providerErrors.Select(e => $"  - {e}"));
            }
        }

        return Task.FromResult(
            new ValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                Warnings = warnings,
            }
        );
    }

    private static bool ShouldExcludeProvider(ProviderConfig provider, ProviderSelectionCriteria criteria)
    {
        // Check excluded providers
        if (criteria.ExcludeProviders?.Contains(provider.Name) == true)
        {
            return true;
        }

        // Check include-only providers
        if (criteria.IncludeOnlyProviders?.Any() == true && !criteria.IncludeOnlyProviders.Contains(provider.Name))
        {
            return true;
        }

        // Check required tags
        if (criteria.RequiredTags?.Any() == true && !provider.HasAllTags(criteria.RequiredTags))
        {
            return true;
        }

        // Check cost limits
        return
            criteria.MaxPromptCostPerMillion.HasValue
            && provider.Pricing.PromptPerMillion > (double)criteria.MaxPromptCostPerMillion.Value
            ? true
            : criteria.MaxCompletionCostPerMillion.HasValue
                && provider.Pricing.CompletionPerMillion > (double)criteria.MaxCompletionCostPerMillion.Value;
    }

    private static bool ShouldExcludeSubProvider(SubProviderConfig subProvider, ProviderSelectionCriteria criteria)
    {
        // Check excluded providers (sub-provider names)
        if (criteria.ExcludeProviders?.Contains(subProvider.Name) == true)
        {
            return true;
        }

        // Check include-only providers (sub-provider names)
        if (criteria.IncludeOnlyProviders?.Any() == true && !criteria.IncludeOnlyProviders.Contains(subProvider.Name))
        {
            return true;
        }

        // Check cost limits
        return
            criteria.MaxPromptCostPerMillion.HasValue
            && subProvider.Pricing.PromptPerMillion > (double)criteria.MaxPromptCostPerMillion.Value
            ? true
            : criteria.MaxCompletionCostPerMillion.HasValue
                && subProvider.Pricing.CompletionPerMillion > (double)criteria.MaxCompletionCostPerMillion.Value;
    }

    private static IReadOnlyList<ProviderResolution> SortProvidersByPreference(
        List<ProviderResolution> resolutions,
        ProviderSelectionCriteria criteria
    )
    {
        return [.. resolutions.OrderByDescending(r => CalculateProviderScore(r, criteria))];
    }

    private static double CalculateProviderScore(ProviderResolution resolution, ProviderSelectionCriteria criteria)
    {
        double score = resolution.EffectivePriority * 100; // Base score from priority

        // Adjust for preferred tags
        if (criteria.PreferredTags?.Any() == true && resolution.Provider.Tags != null)
        {
            var matchingTags = criteria.PreferredTags.Intersect(resolution.Provider.Tags).Count();
            score += matchingTags * 50; // Bonus for each matching preferred tag
        }

        // Adjust for cost preference
        if (criteria.PreferLowerCost)
        {
            var totalCost =
                resolution.EffectivePricing.PromptPerMillion + resolution.EffectivePricing.CompletionPerMillion;
            score += Math.Max(0, 1000 - totalCost); // Higher score for lower cost
        }

        // Adjust for performance preference
        if (criteria.PreferHigherPerformance && resolution.Provider.Tags != null)
        {
            var performanceTags = new[] { "fast", "ultra-fast", "high-performance", "speed-optimized" };
            if (resolution.Provider.Tags.Any(tag => performanceTags.Contains(tag)))
            {
                score += 75; // Bonus for performance tags
            }
        }

        return score;
    }
}
