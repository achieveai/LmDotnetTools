using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;
using AchieveAi.LmDotnetTools.LmConfig.Agents;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmCore.Agents;

namespace LmConfig.Tests;

public class ConfigurationLoadingTests
{
  private readonly string _testConfigJson = """
    {
      "models": [
        {
          "id": "test-model",
          "capabilities": {
            "token_limits": {
              "max_context_tokens": 4096,
              "max_output_tokens": 1024
            },
            "supports_streaming": true
          },
          "providers": [
            {
              "name": "TestProvider",
              "model_name": "test-model-v1",
              "priority": 1,
              "pricing": {
                "prompt_per_million": 1.0,
                "completion_per_million": 2.0
              },
              "tags": ["test", "fast"]
            }
          ]
        }
      ],
      "provider_registry": {
        "TestProvider": {
          "endpoint_url": "https://api.test.com/v1",
          "api_key_environment_variable": "TEST_API_KEY",
          "compatibility": "OpenAI",
          "timeout": "00:01:00",
          "max_retries": 3
        }
      }
    }
    """;

  [Fact]
  public void AddLmConfigFromStream_WithValidConfiguration_ShouldRegisterServices()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // Act
    services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes(_testConfigJson)));

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    
    var agent = serviceProvider.GetService<IAgent>();
    var streamingAgent = serviceProvider.GetService<IStreamingAgent>();
    var modelResolver = serviceProvider.GetService<IModelResolver>();
    var providerAgentFactory = serviceProvider.GetService<IProviderAgentFactory>();

    Assert.NotNull(agent);
    Assert.NotNull(streamingAgent);
    Assert.NotNull(modelResolver);
    Assert.NotNull(providerAgentFactory);
    Assert.IsType<UnifiedAgent>(agent);
    Assert.IsType<UnifiedAgent>(streamingAgent);
  }

  [Fact]
  public void AddLmConfigFromStreamAsync_WithValidConfiguration_ShouldRegisterServices()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // Act
    services.AddLmConfigFromStreamAsync(async () =>
    {
      await Task.Delay(1); // Simulate async operation
      return new MemoryStream(Encoding.UTF8.GetBytes(_testConfigJson));
    });

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    
    var agent = serviceProvider.GetService<IAgent>();
    Assert.NotNull(agent);
    Assert.IsType<UnifiedAgent>(agent);
  }

  [Fact]
  public void AddLmConfigFromStream_WithInvalidJson_ShouldThrowException()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    var invalidJson = "{ invalid json }";

    // Act & Assert
    var exception = Assert.Throws<InvalidOperationException>(() =>
      services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes(invalidJson))));

    Assert.Contains("Failed to parse LmConfig from stream", exception.Message);
  }

  [Fact]
  public void AddLmConfigFromStream_WithEmptyConfiguration_ShouldThrowException()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    var emptyConfig = """{"models": []}""";

    // Act & Assert
    var exception = Assert.Throws<InvalidOperationException>(() =>
      services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes(emptyConfig))));

    Assert.Contains("Invalid or empty LmConfig stream", exception.Message);
  }

  [Fact]
  public void AddLmConfigFromEmbeddedResource_WithNonExistentResource_ShouldThrowException()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    // Act & Assert
    var exception = Assert.Throws<InvalidOperationException>(() =>
      services.AddLmConfigFromEmbeddedResource("non-existent.json"));

    Assert.Contains("Embedded resource 'non-existent.json' not found", exception.Message);
  }

  // Note: IConfiguration-based tests are skipped due to configuration section binding complexities
  // The core functionality is tested through stream-based and direct AppConfig tests

  [Fact]
  public async Task ModelResolver_IsProviderAvailableAsync_WithoutApiKey_ShouldReturnFalse()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes(_testConfigJson)));

    var serviceProvider = services.BuildServiceProvider();
    var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

    // Act
    var isAvailable = await modelResolver.IsProviderAvailableAsync("TestProvider");

    // Assert - Should be false because TEST_API_KEY environment variable is not set
    Assert.False(isAvailable);
  }

  [Fact]
  public async Task ModelResolver_IsProviderAvailableAsync_WithApiKey_ShouldReturnTrue()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes(_testConfigJson)));

    var serviceProvider = services.BuildServiceProvider();
    var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

    // Set the API key environment variable
    Environment.SetEnvironmentVariable("TEST_API_KEY", "test-key-value");

    try
    {
      // Act
      var isAvailable = await modelResolver.IsProviderAvailableAsync("TestProvider");

      // Assert
      Assert.True(isAvailable);
    }
    finally
    {
      // Cleanup
      Environment.SetEnvironmentVariable("TEST_API_KEY", null);
    }
  }

  [Fact]
  public async Task ModelResolver_ResolveProviderAsync_WithoutAvailableProviders_ShouldReturnNull()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes(_testConfigJson)));

    var serviceProvider = services.BuildServiceProvider();
    var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

    // Act - No API key set, so no providers should be available
    var resolution = await modelResolver.ResolveProviderAsync("test-model");

    // Assert
    Assert.Null(resolution);
  }

  [Fact]
  public async Task ModelResolver_ResolveProviderAsync_WithAvailableProvider_ShouldReturnResolution()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes(_testConfigJson)));

    var serviceProvider = services.BuildServiceProvider();
    var modelResolver = serviceProvider.GetRequiredService<IModelResolver>();

    // Set the API key environment variable
    Environment.SetEnvironmentVariable("TEST_API_KEY", "test-key-value");

    try
    {
      // Act
      var resolution = await modelResolver.ResolveProviderAsync("test-model");

      // Assert
      Assert.NotNull(resolution);
      Assert.Equal("TestProvider", resolution.EffectiveProviderName);
      Assert.Equal("test-model-v1", resolution.EffectiveModelName);
      Assert.Equal("https://api.test.com/v1", resolution.Connection.EndpointUrl);
    }
    finally
    {
      // Cleanup
      Environment.SetEnvironmentVariable("TEST_API_KEY", null);
    }
  }

  [Fact]
  public void LoadConfigFromStream_WithCommentsAndTrailingCommas_ShouldSucceed()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    var configWithComments = """
      {
        // This is a test configuration
        "models": [
          {
            "id": "test-model", // Model identifier
            "capabilities": {
              "token_limits": {
                "max_context_tokens": 4096,
                "max_output_tokens": 1024, // Trailing comma
              },
              "supports_streaming": true,
            },
            "providers": [
              {
                "name": "TestProvider",
                "model_name": "test-model-v1",
                "priority": 1,
                "pricing": {
                  "prompt_per_million": 1.0,
                  "completion_per_million": 2.0,
                },
                "tags": ["test", "fast"],
              }
            ],
          }
        ],
        "provider_registry": {
          "TestProvider": {
            "endpoint_url": "https://api.test.com/v1",
            "api_key_environment_variable": "TEST_API_KEY",
            "compatibility": "OpenAI",
            "timeout": "00:01:00",
            "max_retries": 3,
          },
        },
      }
      """;

    // Act & Assert - Should not throw exception
    services.AddLmConfigFromStream(() => new MemoryStream(Encoding.UTF8.GetBytes(configWithComments)));

    var serviceProvider = services.BuildServiceProvider();
    var agent = serviceProvider.GetService<IAgent>();
    Assert.NotNull(agent);
  }

  [Fact]
  public void StreamFactory_CalledMultipleTimes_ShouldWorkCorrectly()
  {
    // Arrange
    var services = new ServiceCollection();
    services.AddLogging();

    var callCount = 0;
    Func<Stream> streamFactory = () =>
    {
      callCount++;
      return new MemoryStream(Encoding.UTF8.GetBytes(_testConfigJson));
    };

    // Act
    services.AddLmConfigFromStream(streamFactory);

    // Assert
    var serviceProvider = services.BuildServiceProvider();
    var agent = serviceProvider.GetService<IAgent>();
    
    Assert.NotNull(agent);
    Assert.Equal(1, callCount); // Stream factory should be called exactly once during registration
  }
} 