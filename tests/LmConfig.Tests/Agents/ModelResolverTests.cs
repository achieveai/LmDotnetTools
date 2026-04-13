using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using LmConfig.Tests.TestUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LmConfig.Tests.Agents;

/// <summary>
/// Tests for ModelResolver validation logic.
/// </summary>
public class ModelResolverTests
{
    private readonly ILogger<ModelResolver> _logger;

    public ModelResolverTests()
    {
        _logger = new LoggerFactory().CreateLogger<ModelResolver>();
    }

    [Fact]
    public async Task ValidateConfigurationAsync_AllProvidersInvalid_ReturnsError()
    {
        // Arrange
        var models = new List<ModelConfig>
        {
            new ModelConfig
            {
                Id = "test-model",
                Providers = new[]
                {
                    new ProviderConfig
                    {
                        Name = "Provider1",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                    new ProviderConfig
                    {
                        Name = "Provider2",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                },
            },
        };

        var appConfig = new AppConfig
        {
            Models = models,
            ProviderRegistry = new Dictionary<string, ProviderConnectionInfo>(),
            // No provider connections configured - all will fail
        };

        var options = Options.Create(appConfig);
        var resolver = new ModelResolver(options, _logger);

        // Act
        var result = await resolver.ValidateConfigurationAsync();

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("test-model has no valid providers"));
        Assert.Contains(result.Errors, e => e.Contains("No connection info found for provider Provider1"));
        Assert.Contains(result.Errors, e => e.Contains("No connection info found for provider Provider2"));
    }

    [Fact]
    public async Task ValidateConfigurationAsync_AtLeastOneProviderValid_NoError()
    {
        // Arrange
        Environment.SetEnvironmentVariable("VALID_PROVIDER_API_KEY", "test-key");

        var models = new List<ModelConfig>
        {
            new ModelConfig
            {
                Id = "test-model",
                Providers = new[]
                {
                    new ProviderConfig
                    {
                        Name = "InvalidProvider",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                    new ProviderConfig
                    {
                        Name = "ValidProvider",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                },
            },
        };

        var providerRegistry = new Dictionary<string, ProviderConnectionInfo>
        {
            ["ValidProvider"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://valid.example.com",
                ApiKeyEnvironmentVariable = "VALID_PROVIDER_API_KEY",
                Compatibility = "OpenAI",
            },
            // InvalidProvider has no connection info
        };

        var appConfig = new AppConfig { Models = models, ProviderRegistry = providerRegistry };

        var options = Options.Create(appConfig);
        var resolver = new ModelResolver(options, _logger);

        // Act
        var result = await resolver.ValidateConfigurationAsync();

        // Assert
        Assert.True(result.IsValid); // Should be valid since one provider is valid
        Assert.Empty(result.Errors); // No errors since at least one provider works
        Assert.Contains(
            result.Warnings,
            w => w.Contains("test-model has 1 invalid provider(s) but 1 valid provider(s)")
        );
        Assert.Contains(result.Warnings, w => w.Contains("No connection info found for provider InvalidProvider"));

        // Cleanup
        Environment.SetEnvironmentVariable("VALID_PROVIDER_API_KEY", null);
    }

    [Fact]
    public async Task ValidateConfigurationAsync_AllProvidersValid_NoErrorsOrWarnings()
    {
        // Arrange
        Environment.SetEnvironmentVariable("PROVIDER1_API_KEY", "test-key-1");
        Environment.SetEnvironmentVariable("PROVIDER2_API_KEY", "test-key-2");

        var models = new List<ModelConfig>
        {
            new ModelConfig
            {
                Id = "test-model",
                Providers = new[]
                {
                    new ProviderConfig
                    {
                        Name = "Provider1",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                    new ProviderConfig
                    {
                        Name = "Provider2",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                },
            },
        };

        var providerRegistry = new Dictionary<string, ProviderConnectionInfo>
        {
            ["Provider1"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://provider1.example.com",
                ApiKeyEnvironmentVariable = "PROVIDER1_API_KEY",
                Compatibility = "OpenAI",
            },
            ["Provider2"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://provider2.example.com",
                ApiKeyEnvironmentVariable = "PROVIDER2_API_KEY",
                Compatibility = "OpenAI",
            },
        };

        var appConfig = new AppConfig { Models = models, ProviderRegistry = providerRegistry };

        var options = Options.Create(appConfig);
        var resolver = new ModelResolver(options, _logger);

        // Act
        var result = await resolver.ValidateConfigurationAsync();

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        // Only warnings from providers themselves should be present, not model-level warnings
        Assert.DoesNotContain(result.Warnings, w => w.Contains("invalid provider(s)"));

        // Cleanup
        Environment.SetEnvironmentVariable("PROVIDER1_API_KEY", null);
        Environment.SetEnvironmentVariable("PROVIDER2_API_KEY", null);
    }

    [Fact]
    public async Task ValidateConfigurationAsync_MixedProviderValidity_CorrectlyReportsWarnings()
    {
        // Arrange
        Environment.SetEnvironmentVariable("VALID1_API_KEY", "test-key-1");
        Environment.SetEnvironmentVariable("VALID2_API_KEY", "test-key-2");

        var models = new List<ModelConfig>
        {
            new ModelConfig
            {
                Id = "test-model",
                Providers = new[]
                {
                    new ProviderConfig
                    {
                        Name = "InvalidProvider1",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                    new ProviderConfig
                    {
                        Name = "ValidProvider1",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                    new ProviderConfig
                    {
                        Name = "InvalidProvider2",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                    new ProviderConfig
                    {
                        Name = "ValidProvider2",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                },
            },
        };

        var providerRegistry = new Dictionary<string, ProviderConnectionInfo>
        {
            ["ValidProvider1"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://valid1.example.com",
                ApiKeyEnvironmentVariable = "VALID1_API_KEY",
                Compatibility = "OpenAI",
            },
            ["ValidProvider2"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://valid2.example.com",
                ApiKeyEnvironmentVariable = "VALID2_API_KEY",
                Compatibility = "OpenAI",
            },
            // InvalidProvider1 and InvalidProvider2 have no connection info
        };

        var appConfig = new AppConfig { Models = models, ProviderRegistry = providerRegistry };

        var options = Options.Create(appConfig);
        var resolver = new ModelResolver(options, _logger);

        // Act
        var result = await resolver.ValidateConfigurationAsync();

        // Assert
        Assert.True(result.IsValid); // Should be valid since we have valid providers
        Assert.Empty(result.Errors);
        Assert.Contains(
            result.Warnings,
            w => w.Contains("test-model has 2 invalid provider(s) but 2 valid provider(s)")
        );
        Assert.Contains(result.Warnings, w => w.Contains("No connection info found for provider InvalidProvider1"));
        Assert.Contains(result.Warnings, w => w.Contains("No connection info found for provider InvalidProvider2"));

        // Cleanup
        Environment.SetEnvironmentVariable("VALID1_API_KEY", null);
        Environment.SetEnvironmentVariable("VALID2_API_KEY", null);
    }

    [Fact]
    public async Task ValidateConfigurationAsync_ProviderWithEmptyName_CountsAsInvalid()
    {
        // Arrange
        Environment.SetEnvironmentVariable("VALID_API_KEY", "test-key");

        var models = new List<ModelConfig>
        {
            new ModelConfig
            {
                Id = "test-model",
                Providers = new[]
                {
                    new ProviderConfig
                    {
                        Name = "", // Empty name
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                    new ProviderConfig
                    {
                        Name = "ValidProvider",
                        ModelName = "test-model-v1",
                        Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                    },
                },
            },
        };

        var providerRegistry = new Dictionary<string, ProviderConnectionInfo>
        {
            ["ValidProvider"] = new ProviderConnectionInfo
            {
                EndpointUrl = "https://valid.example.com",
                ApiKeyEnvironmentVariable = "VALID_API_KEY",
                Compatibility = "OpenAI",
            },
        };

        var appConfig = new AppConfig { Models = models, ProviderRegistry = providerRegistry };

        var options = Options.Create(appConfig);
        var resolver = new ModelResolver(options, _logger);

        // Act
        var result = await resolver.ValidateConfigurationAsync();

        // Assert
        Assert.True(result.IsValid); // Still valid because we have one valid provider
        Assert.Empty(result.Errors);
        Assert.Contains(
            result.Warnings,
            w => w.Contains("test-model has 1 invalid provider(s) but 1 valid provider(s)")
        );
        Assert.Contains(result.Warnings, w => w.Contains("Provider with empty name"));

        // Cleanup
        Environment.SetEnvironmentVariable("VALID_API_KEY", null);
    }
}
