using AchieveAi.LmDotnetTools.LmEmbeddings.Interfaces;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;
using Moq;
using System.Diagnostics;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Tests.Interfaces;

/// <summary>
/// Comprehensive tests for IEmbeddingService interface
/// </summary>
public class IEmbeddingServiceTests
{
    private readonly Mock<ILogger> _mockLogger;

    public IEmbeddingServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
    }

    #region Test Data

    /// <summary>
    /// Test data for GetEmbeddingAsync method
    /// </summary>
    public static IEnumerable<object[]> GetEmbeddingTestCases => new List<object[]>
    {
        // Format: sentence, expectedEmbeddingSize, description
        new object[] { "Hello world", 1536, "Simple two-word sentence" },
        new object[] { "The quick brown fox jumps over the lazy dog.", 1536, "Complete sentence with punctuation" },
        new object[] { "AI and machine learning are transforming technology.", 1536, "Technical content" },
        new object[] { "🌟 Unicode and emojis work too! 🚀", 1536, "Unicode and emoji content" },
        new object[] { "Bonjour le monde", 1536, "French language content" },
        new object[] { "こんにちは世界", 1536, "Japanese language content" },
        new object[] { "A", 1536, "Single character" },
        new object[] { new string('x', 1000), 1536, "Long text (1000 characters)" },
        new object[] { "Multiple\nlines\nof\ntext", 1536, "Multi-line text" },
        new object[] { "Text with \"quotes\" and 'apostrophes'", 1536, "Text with quotes" }
    };

    /// <summary>
    /// Test data for invalid inputs
    /// </summary>
    public static IEnumerable<object[]> InvalidInputTestCases => new List<object[]>
    {
        // Format: sentence, expectedExceptionType, description
        new object[] { null!, typeof(ArgumentException), "Null input" },
        new object[] { "", typeof(ArgumentException), "Empty string input" },
        new object[] { "   ", typeof(ArgumentException), "Whitespace-only input" },
        new object[] { "\t\n\r", typeof(ArgumentException), "Tab and newline only input" }
    };

    /// <summary>
    /// Test data for GenerateEmbeddingsAsync method
    /// </summary>
    public static IEnumerable<object[]> GenerateEmbeddingsTestCases => new List<object[]>
    {
        // Format: inputs, model, description
        new object[] { new[] { "Hello" }, "text-embedding-3-small", "Single input" },
        new object[] { new[] { "Hello", "World" }, "text-embedding-3-small", "Multiple inputs" },
        new object[] { new[] { "Test", "Data", "For", "Batch" }, "text-embedding-3-large", "Batch processing" },
        new object[] { new[] { "Mixed", "🌟", "Content" }, "text-embedding-ada-002", "Mixed content types" }
    };

    #endregion

    #region GetEmbeddingAsync Tests

    [Theory]
    [MemberData(nameof(GetEmbeddingTestCases))]
    public async Task GetEmbeddingAsync_ValidInputs_ReturnsExpectedEmbedding(
        string sentence,
        int expectedEmbeddingSize,
        string description)
    {
        // Arrange
        Assert.NotNull(sentence); // Ensure sentence is not null for this test
        Debug.WriteLine($"Testing GetEmbeddingAsync with: {description}");
        Debug.WriteLine($"Input: '{sentence}' (Length: {sentence.Length})");
        Debug.WriteLine($"Expected embedding size: {expectedEmbeddingSize}");

        var mockService = CreateMockEmbeddingService(expectedEmbeddingSize);
        var expectedEmbedding = GenerateTestEmbedding(expectedEmbeddingSize);

        mockService.Setup(s => s.GetEmbeddingAsync(sentence, It.IsAny<CancellationToken>()))
               .ReturnsAsync(expectedEmbedding);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await mockService.Object.GetEmbeddingAsync(sentence);
        stopwatch.Stop();

        // Assert
        Debug.WriteLine($"Execution time: {stopwatch.ElapsedMilliseconds}ms");
        Debug.WriteLine($"Result embedding size: {result.Length}");
        Debug.WriteLine($"First few values: [{string.Join(", ", result.Take(5).Select(x => x.ToString("F3")))}...]");

        Assert.NotNull(result);
        Assert.Equal(expectedEmbeddingSize, result.Length);
        Assert.All(result, value => Assert.True(value >= -1.0f && value <= 1.0f, "Embedding values should be normalized"));

        mockService.Verify(s => s.GetEmbeddingAsync(sentence, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [MemberData(nameof(InvalidInputTestCases))]
    public async Task GetEmbeddingAsync_InvalidInputs_ThrowsExpectedException(
        string? sentence,
        Type expectedExceptionType,
        string description)
    {
        // Arrange
        Debug.WriteLine($"Testing GetEmbeddingAsync error handling: {description}");
        Debug.WriteLine($"Input: '{sentence}'");
        Debug.WriteLine($"Expected exception: {expectedExceptionType.Name}");

        var mockService = CreateMockEmbeddingService(1536);
        mockService.Setup(s => s.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new ArgumentException("Invalid input"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync(expectedExceptionType,
            () => mockService.Object.GetEmbeddingAsync(sentence!));

        Debug.WriteLine($"Exception message: {exception.Message}");
        Assert.NotNull(exception);
    }

    [Fact]
    public async Task GetEmbeddingAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        // Arrange
        Debug.WriteLine("Testing GetEmbeddingAsync cancellation behavior");

        var mockService = CreateMockEmbeddingService(1536);
        var cancellationTokenSource = new CancellationTokenSource();

        mockService.Setup(s => s.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new OperationCanceledException());

        cancellationTokenSource.Cancel();

        // Act & Assert
        Debug.WriteLine("Requesting cancellation and calling GetEmbeddingAsync");
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => mockService.Object.GetEmbeddingAsync("test", cancellationTokenSource.Token));

        Debug.WriteLine("Cancellation was properly handled");
    }

    [Fact]
    public async Task GetEmbeddingAsync_WithNullInput_ThrowsArgumentNullException()
    {
        // Arrange
        var mockService = CreateMockEmbeddingService(1536);
        mockService.Setup(s => s.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new ArgumentNullException("sentence"));

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => mockService.Object.GetEmbeddingAsync(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task GetEmbeddingAsync_WithInvalidInput_ThrowsArgumentException(string? input)
    {
        // Arrange
        var mockService = CreateMockEmbeddingService(1536);

        if (input == null)
        {
            mockService.Setup(s => s.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new ArgumentNullException("sentence"));
        }
        else
        {
            mockService.Setup(s => s.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                   .ThrowsAsync(new ArgumentException("Invalid input"));
        }

        // Act & Assert
        if (input == null)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(() => mockService.Object.GetEmbeddingAsync(input!));
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => mockService.Object.GetEmbeddingAsync(input));
        }
    }

    #endregion

    #region GenerateEmbeddingsAsync Tests

    [Theory]
    [MemberData(nameof(GenerateEmbeddingsTestCases))]
    [Trait("Category", "Performance")]
    public async Task GenerateEmbeddingsAsync_ValidInputs_ReturnsExpectedResponse(
        string[] inputs,
        string model,
        string description)
    {
        // Arrange
        Debug.WriteLine($"Testing GenerateEmbeddingsAsync with: {description}");
        Debug.WriteLine($"Inputs: [{string.Join(", ", inputs.Select(i => $"'{i}'"))}]");
        Debug.WriteLine($"Model: {model}");
        Debug.WriteLine($"Input count: {inputs.Length}");

        var mockService = CreateMockEmbeddingService(1536);
        var request = new EmbeddingRequest
        {
            Inputs = inputs,
            Model = model
        };

        var expectedResponse = CreateTestEmbeddingResponse(inputs, model);
        mockService.Setup(s => s.GenerateEmbeddingsAsync(It.IsAny<EmbeddingRequest>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(expectedResponse);

        // Act
        var stopwatch = Stopwatch.StartNew();
        var result = await mockService.Object.GenerateEmbeddingsAsync(request);
        stopwatch.Stop();

        // Assert
        Debug.WriteLine($"Execution time: {stopwatch.ElapsedMilliseconds}ms");
        Debug.WriteLine($"Response embedding count: {result.Embeddings.Count}");
        Debug.WriteLine($"Response model: {result.Model}");

        Assert.NotNull(result);
        Assert.Equal(inputs.Length, result.Embeddings.Count);
        Assert.Equal(model, result.Model);

        for (int i = 0; i < inputs.Length; i++)
        {
            var embedding = result.Embeddings.ElementAt(i);
            Debug.WriteLine($"Embedding {i}: Index={embedding.Index}, VectorLength={embedding.Vector.Length}");
            Assert.Equal(i, embedding.Index);
            Assert.Equal(1536, embedding.Vector.Length);
            Assert.Equal(inputs[i], embedding.Text);
        }
    }

    #endregion

    #region EmbeddingSize Property Tests

    [Fact]
    public void EmbeddingSize_Property_ReturnsExpectedValue()
    {
        // Arrange
        Debug.WriteLine("Testing EmbeddingSize property");
        const int expectedSize = 1536;

        var mockService = CreateMockEmbeddingService(expectedSize);

        // Act
        var result = mockService.Object.EmbeddingSize;

        // Assert
        Debug.WriteLine($"EmbeddingSize returned: {result}");
        Assert.Equal(expectedSize, result);

        mockService.VerifyGet(s => s.EmbeddingSize, Times.Once);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1536)]
    [InlineData(3072)]
    public void EmbeddingSize_DifferentSizes_ReturnsCorrectValue(int expectedSize)
    {
        // Arrange
        Debug.WriteLine($"Testing EmbeddingSize property with size: {expectedSize}");

        var mockService = CreateMockEmbeddingService(expectedSize);

        // Act
        var result = mockService.Object.EmbeddingSize;

        // Assert
        Debug.WriteLine($"EmbeddingSize returned: {result}");
        Assert.Equal(expectedSize, result);
        Assert.True(result > 0, "Embedding size should be positive");
    }

    #endregion

    #region Disposal Tests

    [Fact]
    public void Dispose_CalledOnce_DoesNotThrow()
    {
        // Arrange
        Debug.WriteLine("Testing Dispose method - single call");

        var mockService = CreateMockEmbeddingService(1536);

        // Act & Assert
        Debug.WriteLine("Calling Dispose()");
        mockService.Object.Dispose();

        Debug.WriteLine("Dispose completed successfully");
        mockService.Verify(s => s.Dispose(), Times.Once);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        Debug.WriteLine("Testing Dispose method - multiple calls");

        var mockService = CreateMockEmbeddingService(1536);

        // Act & Assert
        Debug.WriteLine("Calling Dispose() multiple times");
        mockService.Object.Dispose();
        mockService.Object.Dispose();
        mockService.Object.Dispose();

        Debug.WriteLine("Multiple Dispose calls completed successfully");
        mockService.Verify(s => s.Dispose(), Times.Exactly(3));
    }

    [Fact]
    [Trait("Category", "Resiliency")]
    public async Task GetEmbeddingAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        Debug.WriteLine("Testing GetEmbeddingAsync after disposal");

        var mockService = CreateMockEmbeddingService(1536);
        mockService.Setup(s => s.GetEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new ObjectDisposedException("EmbeddingService"));

        mockService.Object.Dispose();

        // Act & Assert
        Debug.WriteLine("Attempting to call GetEmbeddingAsync after disposal");
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => mockService.Object.GetEmbeddingAsync("test"));

        Debug.WriteLine("ObjectDisposedException was properly thrown");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a mock embedding service for testing
    /// </summary>
    private Mock<IEmbeddingService> CreateMockEmbeddingService(int embeddingSize)
    {
        var mock = new Mock<IEmbeddingService>();
        mock.SetupGet(s => s.EmbeddingSize).Returns(embeddingSize);

        mock.Setup(s => s.GetAvailableModelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { "text-embedding-3-small", "text-embedding-3-large" });

        return mock;
    }

    /// <summary>
    /// Generates a test embedding vector
    /// </summary>
    private static float[] GenerateTestEmbedding(int size)
    {
        var random = new Random(42); // Fixed seed for reproducible tests
        var embedding = new float[size];

        for (int i = 0; i < size; i++)
        {
            embedding[i] = (float)(random.NextDouble() * 2.0 - 1.0); // Values between -1 and 1
        }

        return embedding;
    }

    /// <summary>
    /// Creates a test embedding response
    /// </summary>
    private static EmbeddingResponse CreateTestEmbeddingResponse(string[] inputs, string model)
    {
        var embeddings = inputs.Select((input, index) => new EmbeddingItem
        {
            Vector = GenerateTestEmbedding(1536),
            Index = index,
            Text = input
        }).ToArray();

        return new EmbeddingResponse
        {
            Embeddings = embeddings,
            Model = model,
            Usage = new EmbeddingUsage
            {
                PromptTokens = inputs.Sum(i => i.Length / 4), // Rough token estimate
                TotalTokens = inputs.Sum(i => i.Length / 4)
            }
        };
    }

    #endregion
}