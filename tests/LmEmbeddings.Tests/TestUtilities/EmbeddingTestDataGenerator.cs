using System.Text.Json;

namespace LmEmbeddings.Tests.TestUtilities;

/// <summary>
/// Centralized test data generator for embedding-related tests
/// Provides consistent test data creation methods
/// </summary>
public static class EmbeddingTestDataGenerator
{
    private static readonly Random DefaultRandom = new(42); // Fixed seed for consistent tests

    /// <summary>
    /// Creates a valid embedding response JSON string for testing
    /// </summary>
    /// <param name="embeddingCount">Number of embeddings in the response</param>
    /// <param name="embeddingSize">Size of each embedding vector (default: 1536)</param>
    /// <param name="model">Model name to include in response (default: "test-model")</param>
    /// <returns>JSON string representing a valid embedding response</returns>
    public static string CreateValidEmbeddingResponse(
        int embeddingCount,
        int embeddingSize = 1536,
        string model = "test-model"
    )
    {
        var embeddings = new List<object>();
        for (var i = 0; i < embeddingCount; i++)
        {
            embeddings.Add(
                new
                {
                    Vector = GenerateTestEmbeddingArray(embeddingSize),
                    Index = i,
                    Text = $"input_text_{i}",
                }
            );
        }

        var response = new
        {
            Embeddings = embeddings,
            Model = model,
            Usage = new { PromptTokens = embeddingCount * 10, TotalTokens = embeddingCount * 10 },
        };

        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Creates a valid rerank response JSON string for testing
    /// </summary>
    /// <param name="documentCount">Number of documents in the response</param>
    /// <param name="model">Model name to include in response (default: "test-rerank-model")</param>
    /// <returns>JSON string representing a valid rerank response</returns>
    public static string CreateValidRerankResponse(int documentCount, string model = "test-rerank-model")
    {
        var results = new List<object>();
        for (var i = 0; i < documentCount; i++)
        {
            results.Add(
                new
                {
                    index = i,
                    relevance_score = Math.Round(DefaultRandom.NextDouble(), 4),
                    document = new { text = $"document_text_{i}" },
                }
            );
        }

        var response = new
        {
            results = results,
            model = model,
            usage = new { total_tokens = documentCount * 5 },
        };

        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Generates a test embedding array with deterministic values
    /// </summary>
    /// <param name="size">Size of the embedding array</param>
    /// <param name="seed">Random seed for reproducible results (default: 42)</param>
    /// <returns>Float array representing an embedding vector</returns>
    public static float[] GenerateTestEmbeddingArray(int size, int seed = 42)
    {
        var random = new Random(seed);
        var embedding = new float[size];
        for (var i = 0; i < size; i++)
        {
            embedding[i] = (float)(random.NextDouble() * 2.0 - 1.0); // Range [-1, 1]
        }
        return embedding;
    }

    /// <summary>
    /// Generates multiple test embedding arrays
    /// </summary>
    /// <param name="count">Number of embedding arrays to generate</param>
    /// <param name="size">Size of each embedding array</param>
    /// <param name="baseSeed">Base seed for random generation</param>
    /// <returns>List of embedding arrays</returns>
    public static List<float[]> GenerateTestEmbeddingArrays(int count, int size, int baseSeed = 42)
    {
        var embeddings = new List<float[]>();
        for (var i = 0; i < count; i++)
        {
            embeddings.Add(GenerateTestEmbeddingArray(size, baseSeed + i));
        }
        return embeddings;
    }

    /// <summary>
    /// Creates test input texts for embedding requests
    /// </summary>
    /// <param name="count">Number of input texts to generate</param>
    /// <param name="prefix">Prefix for generated texts (default: "test_input")</param>
    /// <returns>Array of test input texts</returns>
    public static string[] CreateTestInputTexts(int count, string prefix = "test_input")
    {
        var inputs = new string[count];
        for (var i = 0; i < count; i++)
        {
            inputs[i] = $"{prefix}_{i}";
        }
        return inputs;
    }

    /// <summary>
    /// Creates test document texts for reranking requests
    /// </summary>
    /// <param name="count">Number of document texts to generate</param>
    /// <param name="prefix">Prefix for generated texts (default: "test_document")</param>
    /// <returns>Array of test document texts</returns>
    public static string[] CreateTestDocumentTexts(int count, string prefix = "test_document")
    {
        var documents = new string[count];
        for (var i = 0; i < count; i++)
        {
            documents[i] = $"{prefix}_{i} - This is a test document with some content for testing purposes.";
        }
        return documents;
    }

    /// <summary>
    /// Creates an error response JSON string for testing error scenarios
    /// </summary>
    /// <param name="errorCode">Error code</param>
    /// <param name="errorMessage">Error message</param>
    /// <param name="errorType">Error type (default: "invalid_request_error")</param>
    /// <returns>JSON string representing an error response</returns>
    public static string CreateErrorResponse(
        string errorCode,
        string errorMessage,
        string errorType = "invalid_request_error"
    )
    {
        var response = new
        {
            error = new
            {
                message = errorMessage,
                type = errorType,
                code = errorCode,
            },
        };

        return JsonSerializer.Serialize(response);
    }

    /// <summary>
    /// Creates a rate limit error response for testing
    /// </summary>
    /// <returns>JSON string representing a rate limit error</returns>
    public static string CreateRateLimitErrorResponse()
    {
        return CreateErrorResponse(
            "rate_limit_exceeded",
            "Rate limit exceeded. Please try again later.",
            "rate_limit_error"
        );
    }

    /// <summary>
    /// Creates an authentication error response for testing
    /// </summary>
    /// <returns>JSON string representing an authentication error</returns>
    public static string CreateAuthErrorResponse()
    {
        return CreateErrorResponse("invalid_api_key", "Invalid API key provided.", "authentication_error");
    }

    /// <summary>
    /// Creates test data for parameterized tests with various embedding scenarios
    /// </summary>
    /// <returns>Test data for use with [MemberData] attributes</returns>
    public static IEnumerable<object[]> GetEmbeddingTestCases()
    {
        return new List<object[]>
        {
            new object[] { 1, 1536, "Single embedding with standard size" },
            new object[] { 3, 1536, "Multiple embeddings with standard size" },
            new object[] { 1, 512, "Single embedding with small size" },
            new object[] { 5, 768, "Multiple embeddings with custom size" },
            new object[] { 10, 1536, "Large batch with standard size" },
        };
    }

    /// <summary>
    /// Creates test data for error scenarios
    /// </summary>
    /// <returns>Test data for error testing</returns>
    public static IEnumerable<object[]> GetErrorTestCases()
    {
        return new List<object[]>
        {
            new object[] { "rate_limit_exceeded", "Rate limit exceeded", "Rate limit error scenario" },
            new object[] { "invalid_api_key", "Invalid API key", "Authentication error scenario" },
            new object[] { "model_not_found", "Model not found", "Model error scenario" },
            new object[] { "invalid_input", "Invalid input format", "Input validation error scenario" },
        };
    }
}
