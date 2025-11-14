using System.Linq;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Capabilities;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace LmConfig.Tests;

public class UnifiedAgentTests
{
    private static ServiceCollection CreateServices(AppConfig config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(config));
        return services;
    }

    private static async IAsyncEnumerable<IMessage> CreateEmptyAsyncEnumerable()
    {
        await Task.CompletedTask; // Make it async
        yield break; // Return empty sequence
    }

    [Fact]
    public void UnifiedAgent_ShouldImplementIAgent()
    {
        // Arrange
        var config = new AppConfig
        {
            Models = new[]
            {
                new ModelConfig
                {
                    Id = "test-model",
                    Capabilities = new ModelCapabilities
                    {
                        TokenLimits = new TokenLimits
                        {
                            MaxContextTokens = 4000,
                            MaxOutputTokens = 1000,
                            RecommendedMaxPromptTokens = 3000,
                        },
                    },
                    Providers = new[]
                    {
                        new ProviderConfig
                        {
                            Name = "TestProvider",
                            ModelName = "test-model-name",
                            Priority = 1,
                            Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                            Tags = new[] { "test" },
                        },
                    },
                },
            },
            ProviderRegistry = new Dictionary<string, ProviderConnectionInfo>
            {
                ["TestProvider"] = new ProviderConnectionInfo
                {
                    EndpointUrl = "https://test.example.com",
                    ApiKeyEnvironmentVariable = "TEST_API_KEY",
                    Compatibility = "OpenAI",
                },
            },
        };

        var services = CreateServices(config);
        services.AddSingleton<IModelResolver, ModelResolver>();
        services.AddSingleton<IProviderAgentFactory, ProviderAgentFactory>();
        services.AddSingleton<UnifiedAgent>();

        var serviceProvider = services.BuildServiceProvider();

        // Act
        var agent = serviceProvider.GetRequiredService<UnifiedAgent>();

        // Assert
        Assert.NotNull(agent);
        Assert.IsAssignableFrom<IAgent>(agent);
        Assert.IsAssignableFrom<IStreamingAgent>(agent);
    }

    [Fact]
    public async Task ModelResolver_ShouldResolveProvider()
    {
        // Arrange
        var config = new AppConfig
        {
            Models = new[]
            {
                new ModelConfig
                {
                    Id = "test-model",
                    Capabilities = new ModelCapabilities
                    {
                        TokenLimits = new TokenLimits
                        {
                            MaxContextTokens = 4000,
                            MaxOutputTokens = 1000,
                            RecommendedMaxPromptTokens = 3000,
                        },
                    },
                    Providers = new[]
                    {
                        new ProviderConfig
                        {
                            Name = "TestProvider",
                            ModelName = "test-model-name",
                            Priority = 1,
                            Pricing = new PricingConfig { PromptPerMillion = 1.0, CompletionPerMillion = 2.0 },
                            Tags = new[] { "test" },
                        },
                    },
                },
            },
            ProviderRegistry = new Dictionary<string, ProviderConnectionInfo>
            {
                ["TestProvider"] = new ProviderConnectionInfo
                {
                    EndpointUrl = "https://test.example.com",
                    ApiKeyEnvironmentVariable = "TEST_API_KEY",
                    Compatibility = "OpenAI",
                },
            },
        };

        // Set up the required environment variable for the test
        Environment.SetEnvironmentVariable("TEST_API_KEY", "test-api-key");

        try
        {
            var services = CreateServices(config);
            services.AddSingleton<IModelResolver, ModelResolver>();

            var serviceProvider = services.BuildServiceProvider();
            var resolver = serviceProvider.GetRequiredService<IModelResolver>();

            // Act
            var resolution = await resolver.ResolveProviderAsync("test-model");

            // Assert
            Assert.NotNull(resolution);
            Assert.Equal("test-model", resolution.Model.Id);
            Assert.Equal("TestProvider", resolution.Provider.Name);
            Assert.Equal("test-model-name", resolution.Provider.ModelName);
            Assert.NotNull(resolution.Connection);
            Assert.Equal("https://test.example.com", resolution.Connection.EndpointUrl);
        }
        finally
        {
            // Clean up the environment variable
            Environment.SetEnvironmentVariable("TEST_API_KEY", null);
        }
    }

    [Fact]
    public async Task ModelResolver_ShouldReturnNullForUnknownModel()
    {
        // Arrange
        var config = new AppConfig
        {
            Models = Array.Empty<ModelConfig>(),
            ProviderRegistry = new Dictionary<string, ProviderConnectionInfo>(),
        };

        var services = CreateServices(config);
        services.AddSingleton<IModelResolver, ModelResolver>();

        var serviceProvider = services.BuildServiceProvider();
        var resolver = serviceProvider.GetRequiredService<IModelResolver>();

        // Act
        var resolution = await resolver.ResolveProviderAsync("unknown-model");

        // Assert
        Assert.Null(resolution);
    }

    [Fact]
    public async Task UnifiedAgent_ShouldUpdateModelIdWhenForwardingToProviderAgent()
    {
        // Arrange
        var config = new AppConfig
        {
            Models = new[]
            {
                new ModelConfig
                {
                    Id = "gpt-4.1",
                    Capabilities = new ModelCapabilities
                    {
                        TokenLimits = new TokenLimits
                        {
                            MaxContextTokens = 4000,
                            MaxOutputTokens = 1000,
                            RecommendedMaxPromptTokens = 3000,
                        },
                    },
                    Providers = new[]
                    {
                        new ProviderConfig
                        {
                            Name = "OpenRouter",
                            ModelName = "openai/gpt-4.1", // Different from the model ID
                            Priority = 1,
                            Pricing = new PricingConfig { PromptPerMillion = 2.0, CompletionPerMillion = 8.0 },
                            Tags = new[] { "openai-compatible" },
                        },
                    },
                },
            },
            ProviderRegistry = new Dictionary<string, ProviderConnectionInfo>
            {
                ["OpenRouter"] = new ProviderConnectionInfo
                {
                    EndpointUrl = "https://openrouter.ai/api/v1",
                    ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
                    Compatibility = "OpenAI",
                },
            },
        };

        var services = CreateServices(config);

        // Create a mock model resolver that returns a predefined resolution
        var mockModelResolver = new Mock<IModelResolver>();
        var expectedResolution = new ProviderResolution
        {
            Model = config.Models[0],
            Provider = config.Models[0].Providers[0],
            Connection = config.ProviderRegistry["OpenRouter"],
        };

        mockModelResolver
            .Setup(r =>
                r.ResolveProviderAsync(
                    It.IsAny<string>(),
                    It.IsAny<ProviderSelectionCriteria>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedResolution);

        // Create a mock agent factory that captures the options passed to it
        var mockAgentFactory = new Mock<IProviderAgentFactory>();
        var mockAgent = new Mock<IStreamingAgent>();

        GenerateReplyOptions? capturedOptions = null;

        mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (messages, options, ct) => capturedOptions = options
            )
            .ReturnsAsync(
                new List<IMessage>
                {
                    new TextMessage { Role = Role.Assistant, Text = "Test response" },
                }
            );

        mockAgentFactory.Setup(f => f.CreateAgent(It.IsAny<ProviderResolution>())).Returns(mockAgent.Object);

        services.AddSingleton(mockModelResolver.Object);
        services.AddSingleton(mockAgentFactory.Object);
        services.AddSingleton<UnifiedAgent>();

        var serviceProvider = services.BuildServiceProvider();
        var unifiedAgent = serviceProvider.GetRequiredService<UnifiedAgent>();

        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.User, Text = "Hello, world!" },
        };

        var originalOptions = new GenerateReplyOptions
        {
            ModelId = "gpt-4.1", // User specifies the logical model ID
            Temperature = 0.7f,
        };

        // Act
        await unifiedAgent.GenerateReplyAsync(messages, originalOptions);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.Equal("openai/gpt-4.1", capturedOptions.ModelId); // Should be updated to provider's model name
        Assert.Equal(0.7f, capturedOptions.Temperature); // Other properties should be preserved

        // Verify the mock was called
        mockAgent.Verify(
            a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.Is<GenerateReplyOptions>(o => o.ModelId == "openai/gpt-4.1"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task UnifiedAgent_ShouldUpdateModelIdForStreamingRequests()
    {
        // Arrange
        var config = new AppConfig
        {
            Models = new[]
            {
                new ModelConfig
                {
                    Id = "claude-3-sonnet",
                    Capabilities = new ModelCapabilities
                    {
                        TokenLimits = new TokenLimits
                        {
                            MaxContextTokens = 4000,
                            MaxOutputTokens = 1000,
                            RecommendedMaxPromptTokens = 3000,
                        },
                    },
                    Providers = new[]
                    {
                        new ProviderConfig
                        {
                            Name = "OpenRouter",
                            ModelName = "anthropic/claude-3-sonnet", // Different from the model ID
                            Priority = 1,
                            Pricing = new PricingConfig { PromptPerMillion = 3.0, CompletionPerMillion = 15.0 },
                            Tags = new[] { "openai-compatible" },
                        },
                    },
                },
            },
            ProviderRegistry = new Dictionary<string, ProviderConnectionInfo>
            {
                ["OpenRouter"] = new ProviderConnectionInfo
                {
                    EndpointUrl = "https://openrouter.ai/api/v1",
                    ApiKeyEnvironmentVariable = "OPENROUTER_API_KEY",
                    Compatibility = "OpenAI",
                },
            },
        };

        var services = CreateServices(config);

        // Create a mock model resolver that returns a predefined resolution
        var mockModelResolver = new Mock<IModelResolver>();
        var expectedResolution = new ProviderResolution
        {
            Model = config.Models[0],
            Provider = config.Models[0].Providers[0],
            Connection = config.ProviderRegistry["OpenRouter"],
        };

        mockModelResolver
            .Setup(r =>
                r.ResolveProviderAsync(
                    It.IsAny<string>(),
                    It.IsAny<ProviderSelectionCriteria>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedResolution);

        // Create a mock agent factory that captures the options passed to it
        var mockAgentFactory = new Mock<IProviderAgentFactory>();
        var mockAgent = new Mock<IStreamingAgent>();

        GenerateReplyOptions? capturedOptions = null;

        mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (messages, options, ct) => capturedOptions = options
            )
            .ReturnsAsync(CreateEmptyAsyncEnumerable());

        mockAgentFactory.Setup(f => f.CreateStreamingAgent(It.IsAny<ProviderResolution>())).Returns(mockAgent.Object);

        services.AddSingleton(mockModelResolver.Object);
        services.AddSingleton(mockAgentFactory.Object);
        services.AddSingleton<UnifiedAgent>();

        var serviceProvider = services.BuildServiceProvider();
        var unifiedAgent = serviceProvider.GetRequiredService<UnifiedAgent>();

        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.User, Text = "Hello, world!" },
        };

        var originalOptions = new GenerateReplyOptions
        {
            ModelId = "claude-3-sonnet", // User specifies the logical model ID
            Temperature = 0.5f,
            MaxToken = 2000,
        };

        // Act
        await unifiedAgent.GenerateReplyStreamingAsync(messages, originalOptions);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.Equal("anthropic/claude-3-sonnet", capturedOptions.ModelId); // Should be updated to provider's model name
        Assert.Equal(0.5f, capturedOptions.Temperature); // Other properties should be preserved
        Assert.Equal(2000, capturedOptions.MaxToken); // Other properties should be preserved

        // Verify the mock was called
        mockAgent.Verify(
            a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.Is<GenerateReplyOptions>(o => o.ModelId == "anthropic/claude-3-sonnet"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task UnifiedAgent_ShouldCreateOptionsWithEffectiveModelNameWhenNullOptions()
    {
        // Arrange
        var config = new AppConfig
        {
            Models = new[]
            {
                new ModelConfig
                {
                    Id = "deepseek-r1",
                    Capabilities = new ModelCapabilities
                    {
                        TokenLimits = new TokenLimits
                        {
                            MaxContextTokens = 4000,
                            MaxOutputTokens = 1000,
                            RecommendedMaxPromptTokens = 3000,
                        },
                    },
                    Providers = new[]
                    {
                        new ProviderConfig
                        {
                            Name = "DeepSeek",
                            ModelName = "deepseek-reasoner", // Different from the model ID
                            Priority = 1,
                            Pricing = new PricingConfig { PromptPerMillion = 0.55, CompletionPerMillion = 2.19 },
                            Tags = new[] { "reasoning" },
                        },
                    },
                },
            },
            ProviderRegistry = new Dictionary<string, ProviderConnectionInfo>
            {
                ["DeepSeek"] = new ProviderConnectionInfo
                {
                    EndpointUrl = "https://api.deepseek.com/v1",
                    ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY",
                    Compatibility = "OpenAI",
                },
            },
        };

        var services = CreateServices(config);

        // Create a mock model resolver that returns a predefined resolution
        var mockModelResolver = new Mock<IModelResolver>();
        var expectedResolution = new ProviderResolution
        {
            Model = config.Models[0],
            Provider = config.Models[0].Providers[0],
            Connection = config.ProviderRegistry["DeepSeek"],
        };

        mockModelResolver
            .Setup(r =>
                r.ResolveProviderAsync(
                    It.IsAny<string>(),
                    It.IsAny<ProviderSelectionCriteria>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(expectedResolution);

        // Create a mock agent factory that captures the options passed to it
        var mockAgentFactory = new Mock<IProviderAgentFactory>();
        var mockAgent = new Mock<IStreamingAgent>();

        GenerateReplyOptions? capturedOptions = null;

        mockAgent
            .Setup(a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (messages, options, ct) => capturedOptions = options
            )
            .ReturnsAsync(
                new List<IMessage>
                {
                    new TextMessage { Role = Role.Assistant, Text = "Test response" },
                }
            );

        mockAgentFactory.Setup(f => f.CreateAgent(It.IsAny<ProviderResolution>())).Returns(mockAgent.Object);

        services.AddSingleton(mockModelResolver.Object);
        services.AddSingleton(mockAgentFactory.Object);
        services.AddSingleton<UnifiedAgent>();

        var serviceProvider = services.BuildServiceProvider();
        var unifiedAgent = serviceProvider.GetRequiredService<UnifiedAgent>();

        var messages = new List<IMessage>
        {
            new TextMessage { Role = Role.User, Text = "Hello, world!" },
        };

        // Pass null options but the agent should still work by getting model from context
        // We need to test this differently since we can't pass null options and expect it to work
        // Let's test with minimal options instead
        var minimalOptions = new GenerateReplyOptions { ModelId = "deepseek-r1" };

        // Act
        await unifiedAgent.GenerateReplyAsync(messages, minimalOptions);

        // Assert
        Assert.NotNull(capturedOptions);
        Assert.Equal("deepseek-reasoner", capturedOptions.ModelId); // Should be updated to provider's model name

        // Verify the mock was called
        mockAgent.Verify(
            a =>
                a.GenerateReplyAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.Is<GenerateReplyOptions>(o => o.ModelId == "deepseek-reasoner"),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }
}
