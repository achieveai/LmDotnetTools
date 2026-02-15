using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace LmEmbeddings.Tests.TestUtilities;

/// <summary>
///     Tests for EmbeddingTestDataGenerator shared utility
/// </summary>
public class EmbeddingTestDataGeneratorTests
{
    [Theory]
    [MemberData(nameof(EmbeddingResponseTestCases))]
    public void CreateValidEmbeddingResponse_WithParameters_ReturnsValidJson(
        int embeddingCount,
        int embeddingSize,
        string model,
        string description
    )
    {
        Debug.WriteLine($"Testing CreateValidEmbeddingResponse: {description}");
        Debug.WriteLine($"Parameters: count={embeddingCount}, size={embeddingSize}, model={model}");

        // Act
        var json = EmbeddingTestDataGenerator.CreateValidEmbeddingResponse(embeddingCount, embeddingSize, model);

        // Assert
        Assert.NotNull(json);
        Assert.NotEmpty(json);

        // Verify it's valid JSON
        var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("Embeddings", out var embeddingsElement));
        Assert.True(root.TryGetProperty("Model", out var modelElement));
        Assert.True(root.TryGetProperty("Usage", out var usageElement));

        Assert.Equal(JsonValueKind.Array, embeddingsElement.ValueKind);
        Assert.Equal(embeddingCount, embeddingsElement.GetArrayLength());
        Assert.Equal(model, modelElement.GetString());

        // Check first embedding structure
        if (embeddingCount > 0)
        {
            var firstEmbedding = embeddingsElement[0];
            Assert.True(firstEmbedding.TryGetProperty("Vector", out var vectorElement));
            Assert.True(firstEmbedding.TryGetProperty("Index", out var indexElement));
            Assert.True(firstEmbedding.TryGetProperty("Text", out var textElement));

            Assert.Equal(JsonValueKind.Array, vectorElement.ValueKind);
            Assert.Equal(embeddingSize, vectorElement.GetArrayLength());
            Assert.Equal(0, indexElement.GetInt32());
        }

        Debug.WriteLine($"✓ Generated valid JSON with {embeddingCount} embeddings of size {embeddingSize}");
    }

    [Theory]
    [MemberData(nameof(RerankResponseTestCases))]
    public void CreateValidRerankResponse_WithParameters_ReturnsValidJson(
        int documentCount,
        string model,
        string description
    )
    {
        Debug.WriteLine($"Testing CreateValidRerankResponse: {description}");

        // Act
        var json = EmbeddingTestDataGenerator.CreateValidRerankResponse(documentCount, model);

        // Assert
        Assert.NotNull(json);
        Assert.NotEmpty(json);

        var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("results", out var resultsElement));
        Assert.True(root.TryGetProperty("model", out var modelElement));
        Assert.Equal(documentCount, resultsElement.GetArrayLength());
        Assert.Equal(model, modelElement.GetString());

        Debug.WriteLine($"✓ Generated valid rerank response with {documentCount} documents");
    }

    [Theory]
    [MemberData(nameof(EmbeddingArrayTestCases))]
    public void GenerateTestEmbeddingArray_WithParameters_ReturnsValidArray(int size, int seed, string description)
    {
        Debug.WriteLine($"Testing GenerateTestEmbeddingArray: {description}");

        // Act
        var embedding = EmbeddingTestDataGenerator.GenerateTestEmbeddingArray(size, seed);

        // Assert
        Assert.NotNull(embedding);
        Assert.Equal(size, embedding.Length);

        // Check values are in expected range [-1, 1]
        Assert.All(embedding, value => Assert.InRange(value, -1.0f, 1.0f));

        // Verify deterministic generation
        var embedding2 = EmbeddingTestDataGenerator.GenerateTestEmbeddingArray(size, seed);
        Assert.Equal(embedding, embedding2);

        Debug.WriteLine($"✓ Generated deterministic embedding array of size {size}");
    }

    [Theory]
    [MemberData(nameof(MultipleEmbeddingArrayTestCases))]
    public void GenerateTestEmbeddingArrays_WithParameters_ReturnsValidArrays(
        int count,
        int size,
        int baseSeed,
        string description
    )
    {
        Debug.WriteLine($"Testing GenerateTestEmbeddingArrays: {description}");

        // Act
        var embeddings = EmbeddingTestDataGenerator.GenerateTestEmbeddingArrays(count, size, baseSeed);

        // Assert
        Assert.NotNull(embeddings);
        Assert.Equal(count, embeddings.Count);

        foreach (var embedding in embeddings)
        {
            Assert.Equal(size, embedding.Length);
            Assert.All(embedding, value => Assert.InRange(value, -1.0f, 1.0f));
        }

        // Verify arrays are different (due to different seeds)
        if (count > 1)
        {
            Assert.NotEqual(embeddings[0], embeddings[1]);
        }

        Debug.WriteLine($"✓ Generated {count} different embedding arrays of size {size}");
    }

    [Theory]
    [MemberData(nameof(InputTextTestCases))]
    public void CreateTestInputTexts_WithParameters_ReturnsValidTexts(int count, string prefix, string description)
    {
        Debug.WriteLine($"Testing CreateTestInputTexts: {description}");

        // Act
        var texts = EmbeddingTestDataGenerator.CreateTestInputTexts(count, prefix);

        // Assert
        Assert.NotNull(texts);
        Assert.Equal(count, texts.Length);

        for (var i = 0; i < count; i++)
        {
            Assert.Equal($"{prefix}_{i}", texts[i]);
        }

        Debug.WriteLine($"✓ Generated {count} input texts with prefix '{prefix}'");
    }

    [Theory]
    [MemberData(nameof(DocumentTextTestCases))]
    public void CreateTestDocumentTexts_WithParameters_ReturnsValidTexts(int count, string prefix, string description)
    {
        Debug.WriteLine($"Testing CreateTestDocumentTexts: {description}");

        // Act
        var documents = EmbeddingTestDataGenerator.CreateTestDocumentTexts(count, prefix);

        // Assert
        Assert.NotNull(documents);
        Assert.Equal(count, documents.Length);

        foreach (var document in documents)
        {
            Assert.StartsWith(prefix, document);
            Assert.Contains("This is a test document", document);
        }

        Debug.WriteLine($"✓ Generated {count} document texts with prefix '{prefix}'");
    }

    [Theory]
    [MemberData(nameof(ErrorResponseTestCases))]
    public void CreateErrorResponse_WithParameters_ReturnsValidJson(
        string errorCode,
        string errorMessage,
        string errorType,
        string description
    )
    {
        Debug.WriteLine($"Testing CreateErrorResponse: {description}");

        // Act
        var json = EmbeddingTestDataGenerator.CreateErrorResponse(errorCode, errorMessage, errorType);

        // Assert
        Assert.NotNull(json);
        Assert.NotEmpty(json);

        var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("error", out var errorElement));
        Assert.True(errorElement.TryGetProperty("code", out var codeElement));
        Assert.True(errorElement.TryGetProperty("message", out var messageElement));
        Assert.True(errorElement.TryGetProperty("type", out var typeElement));

        Assert.Equal(errorCode, codeElement.GetString());
        Assert.Equal(errorMessage, messageElement.GetString());
        Assert.Equal(errorType, typeElement.GetString());

        Debug.WriteLine($"✓ Generated error response: {errorCode} - {errorMessage}");
    }

    [Fact]
    public void CreateRateLimitErrorResponse_ReturnsValidErrorJson()
    {
        Debug.WriteLine("Testing CreateRateLimitErrorResponse");

        // Act
        var json = EmbeddingTestDataGenerator.CreateRateLimitErrorResponse();

        // Assert
        Assert.NotNull(json);
        Assert.Contains("rate_limit_exceeded", json);
        Assert.Contains("Rate limit exceeded", json);

        Debug.WriteLine("✓ Generated rate limit error response");
    }

    [Fact]
    public void CreateAuthErrorResponse_ReturnsValidErrorJson()
    {
        Debug.WriteLine("Testing CreateAuthErrorResponse");

        // Act
        var json = EmbeddingTestDataGenerator.CreateAuthErrorResponse();

        // Assert
        Assert.NotNull(json);
        Assert.Contains("invalid_api_key", json);
        Assert.Contains("Invalid API key", json);

        Debug.WriteLine("✓ Generated auth error response");
    }

    [Fact]
    public void GetEmbeddingTestCases_ReturnsValidTestData()
    {
        Debug.WriteLine("Testing GetEmbeddingTestCases");

        // Act
        var testCases = EmbeddingTestDataGenerator.GetEmbeddingTestCases();

        // Assert
        Assert.NotNull(testCases);
        Assert.NotEmpty(testCases);

        foreach (var testCase in testCases)
        {
            Assert.Equal(3, testCase.Length); // count, size, description
            _ = Assert.IsType<int>(testCase[0]); // count
            _ = Assert.IsType<int>(testCase[1]); // size
            _ = Assert.IsType<string>(testCase[2]); // description
        }

        Debug.WriteLine($"✓ Generated {testCases.Count()} embedding test cases");
    }

    [Fact]
    public void GetErrorTestCases_ReturnsValidTestData()
    {
        Debug.WriteLine("Testing GetErrorTestCases");

        // Act
        var testCases = EmbeddingTestDataGenerator.GetErrorTestCases();

        // Assert
        Assert.NotNull(testCases);
        Assert.NotEmpty(testCases);

        foreach (var testCase in testCases)
        {
            Assert.Equal(3, testCase.Length); // code, message, description
            _ = Assert.IsType<string>(testCase[0]); // code
            _ = Assert.IsType<string>(testCase[1]); // message
            _ = Assert.IsType<string>(testCase[2]); // description
        }

        Debug.WriteLine($"✓ Generated {testCases.Count()} error test cases");
    }

    #region Test Data

    public static IEnumerable<object[]> EmbeddingResponseTestCases =>
        [
            [1, 1536, "test-model", "Single embedding with standard size"],
            [3, 1536, "test-model-large", "Multiple embeddings with standard size"],
            [1, 512, "small-model", "Single embedding with small size"],
            [5, 768, "custom-model", "Multiple embeddings with custom size"],
            [10, 1024, "batch-model", "Large batch with medium size"],
        ];

    public static IEnumerable<object[]> RerankResponseTestCases =>
        [
            [1, "rerank-model", "Single document rerank"],
            [5, "rerank-v2", "Multiple document rerank"],
            [10, "custom-rerank", "Large document set rerank"],
        ];

    public static IEnumerable<object[]> EmbeddingArrayTestCases =>
        [
            [1536, 42, "Standard OpenAI embedding size"],
            [768, 123, "BERT-style embedding size"],
            [512, 456, "Smaller embedding size"],
            [1024, 789, "Medium embedding size"],
            [100, 999, "Tiny embedding for testing"],
        ];

    public static IEnumerable<object[]> MultipleEmbeddingArrayTestCases =>
        [
            [3, 1536, 42, "Three standard embeddings"],
            [5, 768, 123, "Five medium embeddings"],
            [10, 512, 456, "Ten small embeddings"],
            [1, 1024, 789, "Single medium embedding"],
        ];

    public static IEnumerable<object[]> InputTextTestCases =>
        [
            [1, "test_input", "Single test input"],
            [5, "sample", "Multiple sample inputs"],
            [10, "data", "Batch of data inputs"],
            [3, "embedding_text", "Custom prefix inputs"],
        ];

    public static IEnumerable<object[]> DocumentTextTestCases =>
        [
            [1, "test_document", "Single test document"],
            [5, "sample_doc", "Multiple sample documents"],
            [10, "content", "Batch of content documents"],
            [3, "rerank_item", "Custom prefix documents"],
        ];

    public static IEnumerable<object[]> ErrorResponseTestCases =>
        [
            ["invalid_request", "The request is invalid", "client_error", "Client error response"],
            ["server_error", "Internal server error", "server_error", "Server error response"],
            ["rate_limit", "Too many requests", "rate_limit_error", "Rate limit error response"],
            ["auth_failed", "Authentication failed", "auth_error", "Authentication error response"],
        ];

    #endregion
}
