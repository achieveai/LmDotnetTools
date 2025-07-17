using System.IO;
using System.Net.Http;
using AchieveAi.LmDotnetTools.LmConfig.Http;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Extensions;
using AchieveAi.LmDotnetTools.Misc.Http;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Integration;

[TestClass]
public class LlmCacheIntegrationTests
{
    private string _testCacheDirectory = null!;
    private IServiceProvider _serviceProvider = null!;
    private ServiceCollection _services = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a unique test directory for each test
        _testCacheDirectory = Path.Combine(Path.GetTempPath(), "LlmCacheIntegrationTests", Guid.NewGuid().ToString());
        
        _services = new ServiceCollection();
    }

    [TestCleanup]
    public void Cleanup()
    {
        (_serviceProvider as IDisposable)?.Dispose();
        
        // Clean up test directory
        if (Directory.Exists(_testCacheDirectory))
        {
            try
            {
                Directory.Delete(_testCacheDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #region Service Registration Tests

    [TestMethod]
    public void AddLlmFileCache_WithOptions_RegistersServicesCorrectly()
    {
        // Arrange
        var options = new LlmCacheOptions
        {
            CacheDirectory = _testCacheDirectory,
            EnableCaching = true,
            CacheExpiration = TimeSpan.FromHours(2)
        };

        // Act
        _services.AddLlmFileCache(options);
        _serviceProvider = _services.BuildServiceProvider();

        // Assert
        var kvStore = _serviceProvider.GetService<IKvStore>();
        Assert.IsNotNull(kvStore);
        Assert.IsInstanceOfType(kvStore, typeof(FileKvStore));
        Assert.AreEqual(_testCacheDirectory, ((FileKvStore)kvStore).CacheDirectory);

        var registeredOptions = _serviceProvider.GetService<LlmCacheOptions>();
        Assert.IsNotNull(registeredOptions);
        Assert.AreEqual(_testCacheDirectory, registeredOptions.CacheDirectory);
        Assert.AreEqual(TimeSpan.FromHours(2), registeredOptions.CacheExpiration);
    }

    [TestMethod]
    public void AddLlmFileCache_WithConfiguration_RegistersServicesCorrectly()
    {
        // Arrange
        var configDict = new Dictionary<string, string?>
        {
            { "LlmCache:CacheDirectory", _testCacheDirectory },
            { "LlmCache:EnableCaching", "true" },
            { "LlmCache:CacheExpiration", "03:00:00" }, // 3 hours
            { "LlmCache:MaxCacheItems", "5000" }
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();

        // Act
        _services.AddLlmFileCache(configuration);
        _serviceProvider = _services.BuildServiceProvider();

        // Assert
        var kvStore = _serviceProvider.GetService<IKvStore>();
        Assert.IsNotNull(kvStore);
        Assert.IsInstanceOfType(kvStore, typeof(FileKvStore));
        Assert.AreEqual(_testCacheDirectory, ((FileKvStore)kvStore).CacheDirectory);

        // Note: Options would be configured via IOptions<T> pattern in real apps
        // For this test, we're just verifying the services are registered
    }

    [TestMethod]
    public void AddLlmFileCache_WithDirectOptions_RegistersServicesCorrectly()
    {
        // Act
        _services.AddLlmFileCache(new LlmCacheOptions
        {
            CacheDirectory = _testCacheDirectory,
            EnableCaching = true,
            CacheExpiration = TimeSpan.FromMinutes(30),
            MaxCacheItems = 1000
        });
        _serviceProvider = _services.BuildServiceProvider();

        // Assert
        var kvStore = _serviceProvider.GetService<FileKvStore>();
        Assert.IsNotNull(kvStore);
        Assert.AreEqual(_testCacheDirectory, kvStore.CacheDirectory);

        var options = _serviceProvider.GetService<LlmCacheOptions>();
        Assert.IsNotNull(options);
        Assert.AreEqual(TimeSpan.FromMinutes(30), options.CacheExpiration);
        Assert.AreEqual(1000, options.MaxCacheItems);
    }

    [TestMethod]
    public void AddLlmFileCacheFromEnvironment_RegistersServicesWithEnvironmentValues()
    {
        // Arrange
        Environment.SetEnvironmentVariable("LLM_CACHE_DIRECTORY", _testCacheDirectory);
        Environment.SetEnvironmentVariable("LLM_CACHE_ENABLED", "true");
        Environment.SetEnvironmentVariable("LLM_CACHE_EXPIRATION_HOURS", "6");
        Environment.SetEnvironmentVariable("LLM_CACHE_MAX_ITEMS", "2000");

        try
        {
            // Act
            _services.AddLlmFileCacheFromEnvironment();
            _serviceProvider = _services.BuildServiceProvider();

            // Assert
            var kvStore = _serviceProvider.GetService<FileKvStore>();
            Assert.IsNotNull(kvStore);
            Assert.AreEqual(_testCacheDirectory, kvStore.CacheDirectory);

            var options = _serviceProvider.GetService<LlmCacheOptions>();
            Assert.IsNotNull(options);
            Assert.IsTrue(options.EnableCaching);
            Assert.AreEqual(TimeSpan.FromHours(6), options.CacheExpiration);
            Assert.AreEqual(2000, options.MaxCacheItems);
        }
        finally
        {
            // Cleanup environment variables
            Environment.SetEnvironmentVariable("LLM_CACHE_DIRECTORY", null);
            Environment.SetEnvironmentVariable("LLM_CACHE_ENABLED", null);
            Environment.SetEnvironmentVariable("LLM_CACHE_EXPIRATION_HOURS", null);
            Environment.SetEnvironmentVariable("LLM_CACHE_MAX_ITEMS", null);
        }
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void AddLlmFileCache_WithInvalidOptions_ThrowsArgumentException()
    {
        // Arrange
        var invalidOptions = new LlmCacheOptions
        {
            CacheDirectory = "", // Invalid
            EnableCaching = true
        };

        // Act & Assert
        _services.AddLlmFileCache(invalidOptions);
    }

    #endregion

    #region Cache Statistics Tests

    [TestMethod]
    public async Task GetCacheStatisticsAsync_WithConfiguredCache_ReturnsCorrectStatistics()
    {
        // Arrange
        var options = new LlmCacheOptions
        {
            CacheDirectory = _testCacheDirectory,
            EnableCaching = true,
            CacheExpiration = TimeSpan.FromHours(1),
            MaxCacheItems = 500,
            MaxCacheSizeBytes = 1024 * 1024 * 10 // 10 MB
        };

        _services.AddLlmFileCache(options);
        _serviceProvider = _services.BuildServiceProvider();

        // Act
        var stats = await _services.GetCacheStatisticsAsync();

        // Assert
        Assert.IsNotNull(stats);
        Assert.IsTrue(stats.IsEnabled);
        Assert.AreEqual(_testCacheDirectory, stats.CacheDirectory);
        Assert.AreEqual(0, stats.ItemCount); // No items cached yet
        Assert.AreEqual(500, stats.MaxItems);
        Assert.AreEqual(1024 * 1024 * 10, stats.MaxSizeBytes);
    }

    [TestMethod]
    public async Task GetCacheStatisticsAsync_WithoutCache_ReturnsDisabledStatistics()
    {
        // Arrange
        _serviceProvider = _services.BuildServiceProvider();

        // Act
        var stats = await _services.GetCacheStatisticsAsync();

        // Assert
        Assert.IsNotNull(stats);
        Assert.IsFalse(stats.IsEnabled);
        Assert.AreEqual(string.Empty, stats.CacheDirectory);
        Assert.AreEqual(0, stats.ItemCount);
    }

    #endregion

    #region Cache Management Tests

    [TestMethod]
    public async Task ClearLlmCacheAsync_WithConfiguredCache_ClearsCache()
    {
        // Arrange
        var options = new LlmCacheOptions
        {
            CacheDirectory = _testCacheDirectory,
            EnableCaching = true
        };

        _services.AddLlmFileCache(options);
        _serviceProvider = _services.BuildServiceProvider();

        // Add some test data to cache
        var kvStore = _serviceProvider.GetRequiredService<FileKvStore>();
        await kvStore.SetAsync("test_key", "test_value");

        // Verify data is there
        var initialCount = await kvStore.GetCountAsync();
        Assert.AreEqual(1, initialCount);

        // Act
        await _services.ClearLlmCacheAsync();

        // Assert
        var finalCount = await kvStore.GetCountAsync();
        Assert.AreEqual(0, finalCount);
    }

    [TestMethod]
    public async Task ClearLlmCacheAsync_WithoutCache_DoesNotThrow()
    {
        // Arrange
        _serviceProvider = _services.BuildServiceProvider();

        // Act & Assert - should not throw
        await _services.ClearLlmCacheAsync();
    }

    #endregion

    #region Handler Builder Tests

    [TestMethod]
    public void IHttpHandlerBuilder_RegistersCacheWrapper()
    {
        // Arrange
        var options = new LlmCacheOptions
        {
            CacheDirectory = _testCacheDirectory,
            EnableCaching = true
        };

        _services.AddLlmFileCache(options);
        _serviceProvider = _services.BuildServiceProvider();

        // Act
        var builder = _serviceProvider.GetService<IHttpHandlerBuilder>();

        // Assert
        Assert.IsNotNull(builder);

        var handler = builder!.Build(new HttpClientHandler());
        Assert.IsInstanceOfType(handler, typeof(CachingHttpMessageHandler));
    }

    #endregion

    #region Validation Tests

    [TestMethod]
    public void LlmCacheOptions_Validation_WorksCorrectly()
    {
        // Test valid options
        var validOptions = new LlmCacheOptions
        {
            CacheDirectory = _testCacheDirectory,
            EnableCaching = true,
            CacheExpiration = TimeSpan.FromHours(1),
            MaxCacheItems = 1000,
            MaxCacheSizeBytes = 1024 * 1024
        };

        var validErrors = validOptions.Validate();
        Assert.AreEqual(0, validErrors.Count);

        // Test invalid options
        var invalidOptions = new LlmCacheOptions
        {
            CacheDirectory = "", // Invalid
            CacheExpiration = TimeSpan.FromMilliseconds(-1), // Invalid
            MaxCacheItems = -1, // Invalid
            MaxCacheSizeBytes = -1 // Invalid
        };

        var invalidErrors = invalidOptions.Validate();
        Assert.IsTrue(invalidErrors.Count >= 4); // Should have multiple validation errors
    }

    #endregion

    #region Environment Configuration Tests

    [TestMethod]
    public void LlmCacheOptions_FromEnvironment_ParsesCorrectly()
    {
        // Arrange
        Environment.SetEnvironmentVariable("LLM_CACHE_DIRECTORY", _testCacheDirectory);
        Environment.SetEnvironmentVariable("LLM_CACHE_ENABLED", "false");
        Environment.SetEnvironmentVariable("LLM_CACHE_EXPIRATION_HOURS", "12");
        Environment.SetEnvironmentVariable("LLM_CACHE_MAX_ITEMS", "5000");
        Environment.SetEnvironmentVariable("LLM_CACHE_MAX_SIZE_MB", "100");
        Environment.SetEnvironmentVariable("LLM_CACHE_CLEANUP_ON_STARTUP", "false");

        try
        {
            // Act
            var options = LlmCacheOptions.FromEnvironment();

            // Assert
            Assert.AreEqual(_testCacheDirectory, options.CacheDirectory);
            Assert.IsFalse(options.EnableCaching);
            Assert.AreEqual(TimeSpan.FromHours(12), options.CacheExpiration);
            Assert.AreEqual(5000, options.MaxCacheItems);
            Assert.AreEqual(100 * 1024 * 1024, options.MaxCacheSizeBytes);
            Assert.IsFalse(options.CleanupOnStartup);
        }
        finally
        {
            // Cleanup
            Environment.SetEnvironmentVariable("LLM_CACHE_DIRECTORY", null);
            Environment.SetEnvironmentVariable("LLM_CACHE_ENABLED", null);
            Environment.SetEnvironmentVariable("LLM_CACHE_EXPIRATION_HOURS", null);
            Environment.SetEnvironmentVariable("LLM_CACHE_MAX_ITEMS", null);
            Environment.SetEnvironmentVariable("LLM_CACHE_MAX_SIZE_MB", null);
            Environment.SetEnvironmentVariable("LLM_CACHE_CLEANUP_ON_STARTUP", null);
        }
    }

    [TestMethod]
    public void LlmCacheOptions_DefaultCacheDirectory_IsValid()
    {
        // Act
        var defaultDir = LlmCacheOptions.GetDefaultCacheDirectory();

        // Assert
        Assert.IsFalse(string.IsNullOrEmpty(defaultDir));
        Assert.IsTrue(defaultDir.Contains("LLM_CACHE"));
        Assert.IsTrue(Path.IsPathRooted(defaultDir)); // Should be an absolute path
        
        // Should be a valid path
        var validationOptions = new LlmCacheOptions { CacheDirectory = defaultDir };
        var errors = validationOptions.Validate();
        Assert.AreEqual(0, errors.Count);
    }

    #endregion

    #region Helper Classes

    /// <summary>
    /// Mock implementation of IOpenClient for testing purposes.
    /// </summary>
    private class MockOpenClient : IOpenClient
    {
        public Task<AchieveAi.LmDotnetTools.OpenAIProvider.Models.ChatCompletionResponse> CreateChatCompletionsAsync(
            AchieveAi.LmDotnetTools.OpenAIProvider.Models.ChatCompletionRequest chatCompletionRequest, 
            CancellationToken cancellationToken = default)
        {
            var response = new AchieveAi.LmDotnetTools.OpenAIProvider.Models.ChatCompletionResponse
            {
                Id = "mock-response",
                VarObject = "chat.completion",
                Created = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = "mock-model"
            };
            
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<AchieveAi.LmDotnetTools.OpenAIProvider.Models.ChatCompletionResponse> StreamingChatCompletionsAsync(
            AchieveAi.LmDotnetTools.OpenAIProvider.Models.ChatCompletionRequest chatCompletionRequest, 
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = new AchieveAi.LmDotnetTools.OpenAIProvider.Models.ChatCompletionResponse
            {
                Id = "mock-streaming-response",
                VarObject = "chat.completion.chunk",
                Created = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = "mock-model"
            };

            await Task.Delay(10, cancellationToken);
            yield return response;
        }

        public void Dispose()
        {
            // Mock implementation - nothing to dispose
        }
    }

    #endregion

    [TestMethod]
    public void LlmCacheOptions_ImmutableRecord_WorksCorrectly()
    {
        // Arrange
        var originalOptions = new LlmCacheOptions
        {
            CacheDirectory = "./OriginalCache",
            EnableCaching = true,
            CacheExpiration = TimeSpan.FromHours(24),
            MaxCacheItems = 1000
        };

        // Act - Create a variation using 'with' expression
        var modifiedOptions = originalOptions with 
        { 
            CacheDirectory = "./ModifiedCache",
            CacheExpiration = TimeSpan.FromHours(48)
        };

        // Assert - Original is unchanged
        Assert.AreEqual("./OriginalCache", originalOptions.CacheDirectory);
        Assert.AreEqual(TimeSpan.FromHours(24), originalOptions.CacheExpiration);
        Assert.AreEqual(1000, originalOptions.MaxCacheItems);

        // Assert - Modified has new values
        Assert.AreEqual("./ModifiedCache", modifiedOptions.CacheDirectory);
        Assert.AreEqual(TimeSpan.FromHours(48), modifiedOptions.CacheExpiration);
        Assert.AreEqual(1000, modifiedOptions.MaxCacheItems); // Unchanged property

        // Assert - Value equality works
        var duplicateOptions = new LlmCacheOptions
        {
            CacheDirectory = "./OriginalCache",
            EnableCaching = true,
            CacheExpiration = TimeSpan.FromHours(24),
            MaxCacheItems = 1000
        };
        Assert.AreEqual(originalOptions, duplicateOptions);
    }
} 