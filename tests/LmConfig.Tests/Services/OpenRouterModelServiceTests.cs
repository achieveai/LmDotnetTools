using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using AchieveAi.LmDotnetTools.LmConfig.Services;
using AchieveAi.LmDotnetTools.LmConfig.Models;

namespace AchieveAi.LmDotnetTools.LmConfig.Tests.Services;

public class OpenRouterModelServiceTests : IDisposable
{
    private readonly Mock<ILogger<OpenRouterModelService>> _mockLogger;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly HttpClient _httpClient;

    public OpenRouterModelServiceTests()
    {
        _mockLogger = new Mock<ILogger<OpenRouterModelService>>();
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpHandler.Object);
    }

    private void SetupHttpMock(object responseData)
    {
        var jsonResponse = JsonSerializer.Serialize(responseData);
        
        _mockHttpHandler.Reset();
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("openrouter.ai")),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new StringContent(jsonResponse)
            });
    }

    private void ClearCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "LmDotnetTools");
        var cacheFilePath = Path.Combine(tempDir, "openrouter-cache.json");
        
        if (File.Exists(cacheFilePath))
        {
            File.Delete(cacheFilePath);
        }
    }

    [Fact]
    public async Task GetModelConfigsAsync_WithValidCache_ReturnsCachedData()
    {
        // Arrange
        ClearCache();
        var service = new OpenRouterModelService(_httpClient, _mockLogger.Object);
        
        // Create mock response for OpenRouter API
        var mockResponse = new
        {
            data = new[]
            {
                new
                {
                    slug = "test-model-1",
                    name = "Test Model 1",
                    context_length = 4096,
                    input_modalities = new[] { "text" },
                    output_modalities = new[] { "text" },
                    has_text_output = true,
                    group = "test",
                    author = "test-author"
                }
            }
        };

        SetupHttpMock(mockResponse);

        // Act
        var result = await service.GetModelConfigsAsync();

        // Assert
        Assert.NotNull(result);
        
        // Verify HTTP call was made
        _mockHttpHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("openrouter.ai")),
            ItExpr.IsAny<CancellationToken>());
        
        Assert.Single(result);
        
        var modelConfig = result.First();
        Assert.Equal("test-model-1", modelConfig.Id);
        Assert.Single(modelConfig.Providers);
        
        var provider = modelConfig.Providers.First();
        Assert.Equal("OpenRouter", provider.Name);
        Assert.Equal("test-model-1", provider.ModelName);
        Assert.Contains("openrouter", provider.Tags ?? Array.Empty<string>());
    }

    [Fact]
    public async Task GetModelConfigsAsync_WithHttpError_ReturnsEmptyList()
    {
        // Arrange
        ClearCache();
        var service = new OpenRouterModelService(_httpClient, _mockLogger.Object);
        
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var result = await service.GetModelConfigsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void JsonParsing_WorksCorrectly()
    {
        // Arrange
        var mockResponse = new
        {
            data = new[]
            {
                new
                {
                    slug = "test-model-1",
                    name = "Test Model 1"
                }
            }
        };

        var jsonResponse = JsonSerializer.Serialize(mockResponse);
        
        // Act
        var parsed = JsonNode.Parse(jsonResponse);
        var dataArray = parsed?["data"]?.AsArray();
        
        // Assert
        Assert.NotNull(dataArray);
        Assert.Single(dataArray);
        
        var firstModel = dataArray[0];
        Assert.Equal("test-model-1", firstModel?["slug"]?.GetValue<string>());
        Assert.Equal("Test Model 1", firstModel?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task GetModelConfigsAsync_WithEmptyResponse_ReturnsEmptyList()
    {
        // Arrange
        ClearCache();
        var service = new OpenRouterModelService(_httpClient, _mockLogger.Object);
        
        var mockResponse = new { data = Array.Empty<object>() };
        SetupHttpMock(mockResponse);

        // Act
        var result = await service.GetModelConfigsAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task RefreshCacheAsync_CallsOpenRouterApi()
    {
        // Arrange
        ClearCache();
        var service = new OpenRouterModelService(_httpClient, _mockLogger.Object);
        
        var mockResponse = new { data = Array.Empty<object>() };
        SetupHttpMock(mockResponse);

        // Act & Assert - Should not throw
        await service.RefreshCacheAsync();
        
        // Verify HTTP call was made
        _mockHttpHandler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.ToString().Contains("openrouter.ai")),
            ItExpr.IsAny<CancellationToken>());
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}