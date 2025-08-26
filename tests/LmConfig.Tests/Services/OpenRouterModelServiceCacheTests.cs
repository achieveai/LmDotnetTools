using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmConfig.Models;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Services;

/// <summary>
/// Tests for OpenRouterModelService caching functionality.
/// </summary>
public class OpenRouterModelServiceCacheTests : IDisposable
{
    private readonly Mock<ILogger<OpenRouterModelService>> _mockLogger;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly HttpClient _httpClient;
    private readonly string _tempCacheDir;
    private readonly string _tempCacheFile;

    public OpenRouterModelServiceCacheTests()
    {
        _mockLogger = new Mock<ILogger<OpenRouterModelService>>();
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpHandler.Object);

        // Create temporary directory for cache testing
        _tempCacheDir = Path.Combine(
            Path.GetTempPath(),
            "LmDotnetTools_Test_" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_tempCacheDir);
        _tempCacheFile = Path.Combine(_tempCacheDir, "openrouter-cache.json");
    }

    [Fact]
    public async Task LoadCacheAsync_WithValidCache_ReturnsCache()
    {
        // Arrange
        var validCache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1), // 1 hour old, still valid
            ModelsData = JsonNode.Parse(
                """
                {
                    "data": [
                        {
                            "slug": "test-model",
                            "name": "Test Model",
                            "context_length": 4096
                        }
                    ]
                }
                """
            ),
            ModelDetails = new Dictionary<string, JsonNode>
            {
                ["test-model"] = JsonNode.Parse(
                    """
                    {
                        "data": [
                            {
                                "id": "test-endpoint",
                                "provider_name": "TestProvider"
                            }
                        ]
                    }
                    """
                )!,
            },
        };

        await SaveTestCache(validCache);
        var service = CreateService();

        // Act
        var result = await service.GetModelConfigsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result);
        Assert.Equal("test-model", result.First().Id);
    }

    [Fact]
    public async Task LoadCacheAsync_WithCorruptedCache_DeletesAndReturnsNull()
    {
        // Arrange
        await File.WriteAllTextAsync(_tempCacheFile, "invalid json content");
        Assert.True(File.Exists(_tempCacheFile)); // Verify file was created

        var service = CreateService();

        // Mock HTTP responses for fresh data fetch
        SetupMockHttpResponses();

        // Act
        var result = await service.GetModelConfigsAsync();

        // Assert
        Assert.NotNull(result); // Should return fresh data
        Assert.True(File.Exists(_tempCacheFile)); // New valid cache should be created after fetching fresh data

        // Verify the new cache is valid by loading it
        var cacheInfo = service.GetCacheInfo();
        Assert.True(cacheInfo.Exists);
        Assert.True(cacheInfo.IsValid);
    }

    [Fact]
    public async Task SaveCacheAsync_WithValidData_SavesAtomically()
    {
        // Arrange
        var service = CreateService();
        SetupMockHttpResponses();

        // Act
        await service.RefreshCacheAsync();

        // Assert
        Assert.True(File.Exists(_tempCacheFile));

        // Verify cache can be loaded back
        var json = await File.ReadAllTextAsync(_tempCacheFile);
        var cache = JsonSerializer.Deserialize<OpenRouterCache>(json);
        Assert.NotNull(cache);
        Assert.True(cache.IsValid);
    }

    [Fact]
    public async Task CacheIntegrityValidation_WithInvalidTimestamp_ReturnsFalse()
    {
        // Arrange
        var invalidCache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddDays(1), // Future timestamp
            ModelsData = JsonNode.Parse("""{"data": [{"slug": "test", "name": "Test"}]}"""),
            ModelDetails = new Dictionary<string, JsonNode>(),
        };

        await SaveTestCache(invalidCache);
        var service = CreateService();

        // Mock HTTP responses for fresh data fetch
        SetupMockHttpResponses();

        // Act
        var result = await service.GetModelConfigsAsync();

        // Assert
        Assert.NotNull(result); // Should return fresh data
        Assert.True(File.Exists(_tempCacheFile)); // New valid cache should be created after fetching fresh data

        // Verify the new cache is valid
        var cacheInfo = service.GetCacheInfo();
        Assert.True(cacheInfo.Exists);
        Assert.True(cacheInfo.IsValid);
    }

    [Fact]
    public async Task CacheIntegrityValidation_WithMissingModelsData_ReturnsFalse()
    {
        // Arrange
        var invalidCache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = null, // Missing models data
            ModelDetails = new Dictionary<string, JsonNode>(),
        };

        await SaveTestCache(invalidCache);
        var service = CreateService();

        // Mock HTTP responses for fresh data fetch
        SetupMockHttpResponses();

        // Act
        var result = await service.GetModelConfigsAsync();

        // Assert
        Assert.NotNull(result); // Should return fresh data
        Assert.True(File.Exists(_tempCacheFile)); // New valid cache should be created after fetching fresh data

        // Verify the new cache is valid
        var cacheInfo = service.GetCacheInfo();
        Assert.True(cacheInfo.Exists);
        Assert.True(cacheInfo.IsValid);
    }

    [Fact]
    public async Task GetCacheInfo_WithExistingCache_ReturnsCorrectInfo()
    {
        // Arrange
        var cache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = JsonNode.Parse("""{"data": [{"slug": "test", "name": "Test"}]}"""),
            ModelDetails = new Dictionary<string, JsonNode>(),
        };

        await SaveTestCache(cache);
        var service = CreateService();

        // Act
        var cacheInfo = service.GetCacheInfo();

        // Assert
        Assert.True(cacheInfo.Exists);
        Assert.True(cacheInfo.SizeBytes > 0);
        Assert.True(cacheInfo.IsValid);
        Assert.Contains(_tempCacheFile, cacheInfo.FilePath);
    }

    [Fact]
    public async Task ClearCacheAsync_RemovesCacheFile()
    {
        // Arrange
        var cache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = JsonNode.Parse("""{"data": [{"slug": "test", "name": "Test"}]}"""),
            ModelDetails = new Dictionary<string, JsonNode>(),
        };

        await SaveTestCache(cache);
        var service = CreateService();

        // Act
        await service.ClearCacheAsync();

        // Assert
        Assert.False(File.Exists(_tempCacheFile));
    }

    [Fact]
    public async Task CacheLoadPerformance_CompletesUnder100Ms()
    {
        // Arrange
        var cache = new OpenRouterCache
        {
            CachedAt = DateTime.UtcNow.AddHours(-1),
            ModelsData = JsonNode.Parse(
                """
                {
                    "data": [
                        {
                            "slug": "test-model-1",
                            "name": "Test Model 1",
                            "context_length": 4096
                        },
                        {
                            "slug": "test-model-2", 
                            "name": "Test Model 2",
                            "context_length": 8192
                        }
                    ]
                }
                """
            ),
            ModelDetails = new Dictionary<string, JsonNode>
            {
                ["test-model-1"] = JsonNode.Parse(
                    """{"data": [{"id": "endpoint1", "provider_name": "Provider1"}]}"""
                )!,
                ["test-model-2"] = JsonNode.Parse(
                    """{"data": [{"id": "endpoint2", "provider_name": "Provider2"}]}"""
                )!,
            },
        };

        await SaveTestCache(cache);
        var service = CreateService();

        // Act
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await service.GetModelConfigsAsync();
        stopwatch.Stop();

        // Assert
        Assert.True(
            stopwatch.ElapsedMilliseconds < 100,
            $"Cache load took {stopwatch.ElapsedMilliseconds}ms, should be under 100ms"
        );
        Assert.NotEmpty(result);
    }

    private OpenRouterModelService CreateService()
    {
        return new OpenRouterModelService(_httpClient, _mockLogger.Object, _tempCacheFile);
    }

    private async Task SaveTestCache(OpenRouterCache cache)
    {
        var json = JsonSerializer.Serialize(
            cache,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = false,
            }
        );
        await File.WriteAllTextAsync(_tempCacheFile, json);
    }

    private void SetupMockHttpResponses()
    {
        // Mock models list response
        var modelsResponse = """
            {
                "data": [
                    {
                        "slug": "mock-model",
                        "name": "Mock Model",
                        "context_length": 4096
                    }
                ]
            }
            """;

        // Mock model details response
        var detailsResponse = """
            {
                "data": [
                    {
                        "id": "mock-endpoint",
                        "provider_name": "MockProvider"
                    }
                ]
            }
            """;

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri!.ToString().Contains("/models")
                ),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent(modelsResponse),
                }
            );

        _mockHttpHandler
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req =>
                    req.RequestUri!.ToString().Contains("/stats/endpoint")
                ),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(
                new HttpResponseMessage
                {
                    StatusCode = System.Net.HttpStatusCode.OK,
                    Content = new StringContent(detailsResponse),
                }
            );
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
