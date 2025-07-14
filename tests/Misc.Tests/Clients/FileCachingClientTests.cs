using System.IO;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.Misc.Clients;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Storage;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Clients;

[TestClass]
public class FileCachingClientTests
{
    private string _testCacheDirectory = null!;
    private FileKvStore _kvStore = null!;
    private LlmCacheOptions _cacheOptions = null!;
    private Mock<IOpenClient> _mockInnerClient = null!;
    private FileCachingClient _cachingClient = null!;

    [TestInitialize]
    public void Setup()
    {
        // Create a unique test directory for each test
        _testCacheDirectory = Path.Combine(Path.GetTempPath(), "FileCachingClientTests", Guid.NewGuid().ToString());
        
        _kvStore = new FileKvStore(_testCacheDirectory);
        _cacheOptions = new LlmCacheOptions
        {
            CacheDirectory = _testCacheDirectory,
            EnableCaching = true,
            CacheExpiration = TimeSpan.FromHours(1),
            CleanupOnStartup = false // Disable for testing
        };
        
        _mockInnerClient = new Mock<IOpenClient>();
        _cachingClient = new FileCachingClient(_mockInnerClient.Object, _kvStore, _cacheOptions);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _cachingClient?.Dispose();
        _kvStore?.Dispose();
        
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

    #region Constructor Tests

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void Constructor_WithNullInnerClient_ThrowsArgumentNullException()
    {
        // Act & Assert
        using var client = new FileCachingClient(null!, _kvStore, _cacheOptions);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void Constructor_WithNullKvStore_ThrowsArgumentNullException()
    {
        // Act & Assert
        using var client = new FileCachingClient(_mockInnerClient.Object, null!, _cacheOptions);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentNullException))]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        using var client = new FileCachingClient(_mockInnerClient.Object, _kvStore, null!);
    }

    [TestMethod]
    [ExpectedException(typeof(ArgumentException))]
    public void Constructor_WithInvalidOptions_ThrowsArgumentException()
    {
        // Arrange
        var invalidOptions = new LlmCacheOptions
        {
            CacheDirectory = "", // Invalid empty directory
            EnableCaching = true
        };

        // Act & Assert
        using var client = new FileCachingClient(_mockInnerClient.Object, _kvStore, invalidOptions);
    }

    [TestMethod]
    public void Constructor_WithValidParameters_InitializesCorrectly()
    {
        // Act
        using var client = new FileCachingClient(_mockInnerClient.Object, _kvStore, _cacheOptions);

        // Assert
        Assert.AreEqual(_testCacheDirectory, client.CacheDirectory);
        Assert.AreSame(_cacheOptions, client.Options);
    }

    #endregion

    #region Non-Streaming Caching Tests

    [TestMethod]
    public async Task CreateChatCompletionsAsync_WithCachingDisabled_CallsInnerClientDirectly()
    {
        // Arrange
        var disabledOptions = _cacheOptions.With(o => o.EnableCaching = false);
        using var client = new FileCachingClient(_mockInnerClient.Object, _kvStore, disabledOptions);
        
        var request = CreateTestRequest();
        var expectedResponse = CreateTestResponse();
        
        _mockInnerClient.Setup(c => c.CreateChatCompletionsAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await client.CreateChatCompletionsAsync(request);

        // Assert
        Assert.AreSame(expectedResponse, result);
        _mockInnerClient.Verify(c => c.CreateChatCompletionsAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task CreateChatCompletionsAsync_WithCacheMiss_CallsInnerClientAndCachesResult()
    {
        // Arrange
        var request = CreateTestRequest();
        var expectedResponse = CreateTestResponse();
        
        _mockInnerClient.Setup(c => c.CreateChatCompletionsAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // Act
        var result = await _cachingClient.CreateChatCompletionsAsync(request);

        // Assert
        Assert.AreSame(expectedResponse, result);
        _mockInnerClient.Verify(c => c.CreateChatCompletionsAsync(request, It.IsAny<CancellationToken>()), Times.Once);
        
        // Wait a bit for caching to complete
        await Task.Delay(100);
        
        // Verify item was cached
        var cacheCount = await _cachingClient.GetCacheCountAsync();
        Assert.AreEqual(1, cacheCount);
    }

    [TestMethod]
    public async Task CreateChatCompletionsAsync_WithCacheHit_ReturnsFromCacheWithoutCallingInnerClient()
    {
        // Arrange
        var request = CreateTestRequest();
        var expectedResponse = CreateTestResponse();
        
        _mockInnerClient.Setup(c => c.CreateChatCompletionsAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        // First call to populate cache
        await _cachingClient.CreateChatCompletionsAsync(request);
        await Task.Delay(100); // Wait for caching to complete
        
        _mockInnerClient.Reset();

        // Act
        var result = await _cachingClient.CreateChatCompletionsAsync(request);

        // Assert
        Assert.AreEqual(expectedResponse.Id, result.Id);
        Assert.AreEqual(expectedResponse.VarObject, result.VarObject);
        _mockInnerClient.Verify(c => c.CreateChatCompletionsAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task CreateChatCompletionsAsync_WithExpiredCache_CallsInnerClientAgain()
    {
        // Arrange
        var expiredOptions = _cacheOptions.With(o => o.CacheExpiration = TimeSpan.FromMilliseconds(50));
        using var client = new FileCachingClient(_mockInnerClient.Object, _kvStore, expiredOptions);
        
        var request = CreateTestRequest();
        var firstResponse = CreateTestResponse("first-call");
        var secondResponse = CreateTestResponse("second-call");
        
        _mockInnerClient.SetupSequence(c => c.CreateChatCompletionsAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(firstResponse)
            .ReturnsAsync(secondResponse);

        // Act
        var firstResult = await client.CreateChatCompletionsAsync(request);
        await Task.Delay(100); // Wait for cache to expire
        var secondResult = await client.CreateChatCompletionsAsync(request);

        // Assert
        Assert.AreEqual(firstResponse.Id, firstResult.Id);
        Assert.AreEqual(secondResponse.Id, secondResult.Id);
        _mockInnerClient.Verify(c => c.CreateChatCompletionsAsync(request, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    #endregion

    #region Streaming Caching Tests

    [TestMethod]
    public async Task StreamingChatCompletionsAsync_WithCachingDisabled_CallsInnerClientDirectly()
    {
        // Arrange
        var disabledOptions = _cacheOptions.With(o => o.EnableCaching = false);
        using var client = new FileCachingClient(_mockInnerClient.Object, _kvStore, disabledOptions);
        
        var request = CreateTestRequest();
        var responses = new[] { CreateTestResponse("1"), CreateTestResponse("2") };
        
        _mockInnerClient.Setup(c => c.StreamingChatCompletionsAsync(request, It.IsAny<CancellationToken>()))
            .Returns(responses.ToAsyncEnumerable());

        // Act
        var results = new List<ChatCompletionResponse>();
        await foreach (var response in client.StreamingChatCompletionsAsync(request))
        {
            results.Add(response);
        }

        // Assert
        Assert.AreEqual(2, results.Count);
        Assert.AreEqual("1", results[0].Id);
        Assert.AreEqual("2", results[1].Id);
        _mockInnerClient.Verify(c => c.StreamingChatCompletionsAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task StreamingChatCompletionsAsync_WithCacheMiss_CallsInnerClientAndCachesResult()
    {
        // Arrange
        var request = CreateTestRequest();
        var responses = new[] { CreateTestResponse("1"), CreateTestResponse("2") };
        
        _mockInnerClient.Setup(c => c.StreamingChatCompletionsAsync(request, It.IsAny<CancellationToken>()))
            .Returns(responses.ToAsyncEnumerable());

        // Act
        var results = new List<ChatCompletionResponse>();
        await foreach (var response in _cachingClient.StreamingChatCompletionsAsync(request))
        {
            results.Add(response);
        }

        // Assert
        Assert.AreEqual(2, results.Count);
        _mockInnerClient.Verify(c => c.StreamingChatCompletionsAsync(request, It.IsAny<CancellationToken>()), Times.Once);
        
        // Wait for caching to complete
        await Task.Delay(100);
        
        // Verify item was cached
        var cacheCount = await _cachingClient.GetCacheCountAsync();
        Assert.AreEqual(1, cacheCount);
    }

    [TestMethod]
    public async Task StreamingChatCompletionsAsync_WithCacheHit_ReturnsFromCacheWithoutCallingInnerClient()
    {
        // Arrange
        var request = CreateTestRequest();
        var responses = new[] { CreateTestResponse("1"), CreateTestResponse("2") };
        
        _mockInnerClient.Setup(c => c.StreamingChatCompletionsAsync(request, It.IsAny<CancellationToken>()))
            .Returns(responses.ToAsyncEnumerable());

        // First call to populate cache
        var firstResults = new List<ChatCompletionResponse>();
        await foreach (var response in _cachingClient.StreamingChatCompletionsAsync(request))
        {
            firstResults.Add(response);
        }
        await Task.Delay(100); // Wait for caching to complete
        
        _mockInnerClient.Reset();

        // Act
        var secondResults = new List<ChatCompletionResponse>();
        await foreach (var response in _cachingClient.StreamingChatCompletionsAsync(request))
        {
            secondResults.Add(response);
        }

        // Assert
        Assert.AreEqual(2, secondResults.Count);
        Assert.AreEqual("1", secondResults[0].Id);
        Assert.AreEqual("2", secondResults[1].Id);
        _mockInnerClient.Verify(c => c.StreamingChatCompletionsAsync(It.IsAny<ChatCompletionRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region Cache Management Tests

    [TestMethod]
    public async Task ClearCacheAsync_RemovesAllCachedItems()
    {
        // Arrange
        var request1 = CreateTestRequest("prompt1");
        var request2 = CreateTestRequest("prompt2");
        var response1 = CreateTestResponse("1");
        var response2 = CreateTestResponse("2");
        
        _mockInnerClient.Setup(c => c.CreateChatCompletionsAsync(request1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response1);
        _mockInnerClient.Setup(c => c.CreateChatCompletionsAsync(request2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response2);

        // Populate cache
        await _cachingClient.CreateChatCompletionsAsync(request1);
        await _cachingClient.CreateChatCompletionsAsync(request2);
        await Task.Delay(100); // Wait for caching to complete

        // Act
        await _cachingClient.ClearCacheAsync();

        // Assert
        var cacheCount = await _cachingClient.GetCacheCountAsync();
        Assert.AreEqual(0, cacheCount);
    }

    [TestMethod]
    public async Task GetCacheCountAsync_ReturnsCorrectCount()
    {
        // Arrange
        var request1 = CreateTestRequest("prompt1");
        var request2 = CreateTestRequest("prompt2");
        var response1 = CreateTestResponse("1");
        var response2 = CreateTestResponse("2");
        
        _mockInnerClient.Setup(c => c.CreateChatCompletionsAsync(request1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response1);
        _mockInnerClient.Setup(c => c.CreateChatCompletionsAsync(request2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response2);

        // Act & Assert
        var initialCount = await _cachingClient.GetCacheCountAsync();
        Assert.AreEqual(0, initialCount);

        await _cachingClient.CreateChatCompletionsAsync(request1);
        await Task.Delay(100);
        
        var countAfterOne = await _cachingClient.GetCacheCountAsync();
        Assert.AreEqual(1, countAfterOne);

        await _cachingClient.CreateChatCompletionsAsync(request2);
        await Task.Delay(100);
        
        var countAfterTwo = await _cachingClient.GetCacheCountAsync();
        Assert.AreEqual(2, countAfterTwo);
    }

    #endregion

    #region Error Handling Tests

    [TestMethod]
    public async Task CreateChatCompletionsAsync_WithInnerClientException_DoesNotCache()
    {
        // Arrange
        var request = CreateTestRequest();
        var expectedException = new InvalidOperationException("Inner client error");
        
        _mockInnerClient.Setup(c => c.CreateChatCompletionsAsync(request, It.IsAny<CancellationToken>()))
            .ThrowsAsync(expectedException);

        // Act & Assert
        var actualException = await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => _cachingClient.CreateChatCompletionsAsync(request));
        
        Assert.AreSame(expectedException, actualException);
        
        // Verify nothing was cached
        var cacheCount = await _cachingClient.GetCacheCountAsync();
        Assert.AreEqual(0, cacheCount);
    }

    [TestMethod]
    [ExpectedException(typeof(ObjectDisposedException))]
    public async Task CreateChatCompletionsAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        _cachingClient.Dispose();

        // Act & Assert
        await _cachingClient.CreateChatCompletionsAsync(CreateTestRequest());
    }

    [TestMethod]
    [ExpectedException(typeof(ObjectDisposedException))]
    public async Task StreamingChatCompletionsAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        _cachingClient.Dispose();

        // Act & Assert
        await foreach (var response in _cachingClient.StreamingChatCompletionsAsync(CreateTestRequest()))
        {
            // Should not reach here
        }
    }

    #endregion

    #region Cancellation Tests

    [TestMethod]
    public async Task CreateChatCompletionsAsync_WithCancellation_RespectsCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var request = CreateTestRequest();
        
        _mockInnerClient.Setup(c => c.CreateChatCompletionsAsync(request, It.IsAny<CancellationToken>()))
            .Returns(async (ChatCompletionRequest _, CancellationToken cancellationToken) =>
            {
                await Task.Delay(1000, cancellationToken); // Long delay to test cancellation
                return CreateTestResponse();
            });

        cts.CancelAfter(50); // Cancel after 50ms

        // Act & Assert
        try
        {
            await _cachingClient.CreateChatCompletionsAsync(request, cts.Token);
            Assert.Fail("Expected cancellation exception");
        }
        catch (OperationCanceledException)
        {
            // Expected - TaskCanceledException inherits from OperationCanceledException
        }
    }

    #endregion

    #region Helper Methods

    private static ChatCompletionRequest CreateTestRequest(string prompt = "Test prompt")
    {
        return new ChatCompletionRequest
        {
            Model = "gpt-4",
            Messages = new List<ChatMessage>
            {
                new() { Role = RoleEnum.User, Content = prompt }
            }
        };
    }

    private static ChatCompletionResponse CreateTestResponse(string id = "test-id")
    {
        return new ChatCompletionResponse
        {
            Id = id,
            VarObject = "chat.completion",
            Created = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = "gpt-4",
            Choices = new List<Choice>
            {
                new()
                {
                    Index = 0,
                    Message = new ChatMessage 
                    { 
                        Role = RoleEnum.Assistant, 
                        Content = "Test response" // Keep it simple - just a string
                    },
                    FinishReason = Choice.FinishReasonEnum.Stop
                }
            },
            Usage = new Usage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15
            }
        };
    }

    #endregion
} 