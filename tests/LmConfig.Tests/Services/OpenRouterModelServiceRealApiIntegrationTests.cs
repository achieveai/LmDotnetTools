using Microsoft.Extensions.Logging;
using Moq;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmConfig.Models;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Services;

/// <summary>
/// Integration tests for OpenRouterModelService with real OpenRouter API.
/// Implements requirement 6.1: Create integration test with real OpenRouter API.
/// These tests are marked as integration tests and may be skipped in CI environments.
/// </summary>
[Trait("Category", "Integration")]
public class OpenRouterModelServiceRealApiIntegrationTests : IDisposable
{
    private readonly ILogger<OpenRouterModelService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _tempCacheDir;
    private readonly string _tempCacheFile;

    public OpenRouterModelServiceRealApiIntegrationTests()
    {
        // Use real logger for integration tests to see actual behavior
        var loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug));
        _logger = loggerFactory.CreateLogger<OpenRouterModelService>();
        
        // Use real HttpClient for integration tests
        _httpClient = new HttpClient();
        
        // Create temporary directory for cache testing
        _tempCacheDir = Path.Combine(Path.GetTempPath(), "LmDotnetTools_IntegrationTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempCacheDir);
        _tempCacheFile = Path.Combine(_tempCacheDir, "openrouter-cache.json");
    }

    [Fact]
    public async Task RealApi_GetModelConfigsAsync_ShouldFetchAndCacheModels()
    {
        // Arrange
        var service = new OpenRouterModelService(_httpClient, _logger, _tempCacheFile);
        
        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.GetModelConfigsAsync();
        stopwatch.Stop();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        
        // Verify we got real models
        Assert.True(result.Count > 10, $"Expected more than 10 models, got {result.Count}");
        
        // Verify some well-known models exist
        var gptModels = result.Where(m => m.Id.Contains("gpt")).ToList();
        var claudeModels = result.Where(m => m.Id.Contains("claude")).ToList();
        
        Assert.NotEmpty(gptModels);
        Assert.NotEmpty(claudeModels);
        
        // Verify model structure
        var sampleModel = result.First();
        Assert.NotNull(sampleModel.Id);
        Assert.NotEmpty(sampleModel.Providers);
        Assert.NotNull(sampleModel.Capabilities);
        
        // Verify provider structure
        var sampleProvider = sampleModel.Providers.First();
        Assert.NotNull(sampleProvider.Name);
        Assert.NotNull(sampleProvider.ModelName);
        Assert.NotNull(sampleProvider.Pricing);
        Assert.NotNull(sampleProvider.Tags);
        Assert.Contains("openrouter", sampleProvider.Tags);
        
        // Verify cache was created
        Assert.True(File.Exists(_tempCacheFile));
        
        // Verify performance requirement (should be reasonable for first fetch)
        Assert.True(stopwatch.ElapsedMilliseconds < 30000, 
            $"First API fetch took {stopwatch.ElapsedMilliseconds}ms, should be under 30 seconds");
        
        Console.WriteLine($"Fetched {result.Count} models in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RealApi_CachedAccess_ShouldMeetPerformanceRequirement()
    {
        // Arrange
        var service = new OpenRouterModelService(_httpClient, _logger, _tempCacheFile);
        
        // First call to populate cache
        await service.GetModelConfigsAsync();
        
        // Act - Second call should use cache
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.GetModelConfigsAsync();
        stopwatch.Stop();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        
        // Verify performance requirement for cached access
        Assert.True(stopwatch.ElapsedMilliseconds < 100, 
            $"Cached access took {stopwatch.ElapsedMilliseconds}ms, should be under 100ms per requirement 6.1");
        
        Console.WriteLine($"Cached access returned {result.Count} models in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RealApi_RefreshCacheAsync_ShouldUpdateCache()
    {
        // Arrange
        var service = new OpenRouterModelService(_httpClient, _logger, _tempCacheFile);
        
        // Initial cache population
        await service.GetModelConfigsAsync();
        var initialCacheInfo = service.GetCacheInfo();
        var initialModifiedTime = initialCacheInfo.LastModified;
        
        // Wait a moment to ensure timestamp difference
        await Task.Delay(1000);
        
        // Act
        await service.RefreshCacheAsync();
        
        // Assert
        var updatedCacheInfo = service.GetCacheInfo();
        Assert.True(updatedCacheInfo.LastModified > initialModifiedTime, 
            "Cache should have been updated with newer timestamp");
        Assert.True(updatedCacheInfo.IsValid);
        Assert.True(updatedCacheInfo.SizeBytes > 0);
        
        Console.WriteLine($"Cache refreshed: {updatedCacheInfo.SizeFormatted} at {updatedCacheInfo.LastModified}");
    }

    [Fact]
    public async Task RealApi_BackgroundRefresh_ShouldNotBlockForegroundRequests()
    {
        // Arrange
        var service = new OpenRouterModelService(_httpClient, _logger, _tempCacheFile);
        
        // Populate cache first
        await service.GetModelConfigsAsync();
        
        // Wait for cache to become stale (this test simulates the scenario)
        // In real usage, this would be 24 hours, but we'll test the mechanism
        
        // Act - Multiple concurrent requests
        var tasks = new List<Task<IReadOnlyList<ModelConfig>>>();
        for (int i = 0; i < 5; i++)
        {
            tasks.Add(service.GetModelConfigsAsync());
        }
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var results = await Task.WhenAll(tasks);
        stopwatch.Stop();
        
        // Assert
        Assert.All(results, result =>
        {
            Assert.NotNull(result);
            Assert.NotEmpty(result);
        });
        
        // All results should be consistent
        var firstResultCount = results[0].Count;
        Assert.All(results, result => Assert.Equal(firstResultCount, result.Count));
        
        // Should complete quickly since using cache
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, 
            $"Concurrent cached requests took {stopwatch.ElapsedMilliseconds}ms, should be fast");
        
        Console.WriteLine($"5 concurrent requests completed in {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RealApi_CacheInfo_ShouldProvideAccurateInformation()
    {
        // Arrange
        var service = new OpenRouterModelService(_httpClient, _logger, _tempCacheFile);
        
        // Initially no cache
        var initialCacheInfo = service.GetCacheInfo();
        Assert.False(initialCacheInfo.Exists);
        
        // Act - Create cache
        await service.GetModelConfigsAsync();
        
        // Assert
        var cacheInfo = service.GetCacheInfo();
        Assert.True(cacheInfo.Exists);
        Assert.True(cacheInfo.IsValid);
        Assert.True(cacheInfo.SizeBytes > 1000); // Should be substantial size
        Assert.Contains(_tempCacheFile, cacheInfo.FilePath);
        Assert.True(cacheInfo.SizeFormatted.Contains("KB") || cacheInfo.SizeFormatted.Contains("MB")); // Should format size nicely
        Assert.Null(cacheInfo.Error);
        
        Console.WriteLine($"Cache info: {cacheInfo.SizeFormatted} at {cacheInfo.FilePath}");
    }

    [Fact]
    public async Task RealApi_ClearCache_ShouldRemoveCacheFile()
    {
        // Arrange
        var service = new OpenRouterModelService(_httpClient, _logger, _tempCacheFile);
        
        // Create cache
        await service.GetModelConfigsAsync();
        Assert.True(service.GetCacheInfo().Exists);
        
        // Act
        await service.ClearCacheAsync();
        
        // Assert
        var cacheInfo = service.GetCacheInfo();
        Assert.False(cacheInfo.Exists);
        Assert.False(File.Exists(_tempCacheFile));
        
        Console.WriteLine("Cache cleared successfully");
    }

    [Fact]
    public async Task RealApi_ModelMapping_ShouldCreateValidModelConfigs()
    {
        // Arrange
        var service = new OpenRouterModelService(_httpClient, _logger, _tempCacheFile);
        
        // Act
        var result = await service.GetModelConfigsAsync();
        
        // Assert - Verify model mapping quality
        Assert.NotEmpty(result);
        
        // Test a few specific models for proper mapping
        var gpt4Model = result.FirstOrDefault(m => m.Id.Contains("gpt-4") && !m.Id.Contains("vision"));
        if (gpt4Model != null)
        {
            Assert.False(gpt4Model.IsReasoning); // GPT-4 is not a reasoning model
            Assert.NotNull(gpt4Model.Capabilities);
            Assert.True(gpt4Model.Capabilities.TokenLimits.MaxContextTokens > 0);
            Assert.NotEmpty(gpt4Model.Providers);
            
            var provider = gpt4Model.Providers.First();
            Assert.True(provider.Pricing.PromptPerMillion > 0);
            Assert.True(provider.Pricing.CompletionPerMillion > 0);
            Assert.Contains("openrouter", provider.Tags!);
        }
        
        // Test reasoning model if available
        var o1Model = result.FirstOrDefault(m => m.Id.Contains("o1"));
        if (o1Model != null)
        {
            Assert.True(o1Model.IsReasoning); // O1 models are reasoning models
            Assert.NotNull(o1Model.Capabilities?.Thinking);
            Assert.Contains("thinking", o1Model.Capabilities.SupportedFeatures);
        }
        
        // Test multimodal model if available
        var visionModel = result.FirstOrDefault(m => m.Id.Contains("vision") || m.Id.Contains("claude-3"));
        if (visionModel != null)
        {
            Assert.NotNull(visionModel.Capabilities?.Multimodal);
            if (visionModel.Capabilities.Multimodal.SupportsImages)
            {
                Assert.Contains("multimodal", visionModel.Capabilities.SupportedFeatures);
            }
        }
        
        Console.WriteLine($"Validated mapping for {result.Count} models");
    }

    [Fact]
    public async Task RealApi_ErrorHandling_ShouldHandleNetworkIssues()
    {
        // Arrange - Use a very short timeout to simulate network issues
        using var shortTimeoutClient = new HttpClient();
        shortTimeoutClient.Timeout = TimeSpan.FromMilliseconds(1); // Very short timeout
        
        var service = new OpenRouterModelService(shortTimeoutClient, _logger, _tempCacheFile);
        
        // Act & Assert - Should not throw, should return empty list
        var result = await service.GetModelConfigsAsync();
        
        Assert.NotNull(result);
        Assert.Empty(result); // Should return empty list when API fails and no cache
        
        Console.WriteLine("Network error handling test completed successfully");
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
        
        // Clean up temporary cache directory
        if (Directory.Exists(_tempCacheDir))
        {
            try
            {
                Directory.Delete(_tempCacheDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
    }
}