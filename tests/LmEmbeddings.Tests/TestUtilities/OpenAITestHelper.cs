using AchieveAi.LmDotnetTools.LmEmbeddings.Models;

namespace LmEmbeddings.Tests.TestUtilities;

/// <summary>
///     Helper class for creating OpenAI-specific test configurations
/// </summary>
public static class OpenAITestHelper
{
    /// <summary>
    ///     Creates EmbeddingOptions configured for OpenAI with the standard OpenAI models
    /// </summary>
    public static EmbeddingOptions CreateOpenAIOptions(
        string apiKey = "test-api-key",
        string baseUrl = "https://api.openai.com",
        string defaultModel = "text-embedding-3-small"
    )
    {
        return new EmbeddingOptions
        {
            Provider = "openai",
            ApiKey = apiKey,
            BaseUrl = baseUrl,
            DefaultModel = defaultModel,
            AvailableModelsWithDimensions = GetOpenAIModels(),
            DefaultEncodingFormat = "float",
        };
    }

    /// <summary>
    ///     Gets the standard OpenAI embedding models with their dimensions
    /// </summary>
    public static Dictionary<string, EmbeddingModelConfig> GetOpenAIModels()
    {
        return new Dictionary<string, EmbeddingModelConfig>
        {
            {
                "text-embedding-3-small",
                new EmbeddingModelConfig
                {
                    Model = "text-embedding-3-small",
                    Dimensions = 1536,
                    IsMultiModal = false,
                }
            },
            {
                "text-embedding-3-large",
                new EmbeddingModelConfig
                {
                    Model = "text-embedding-3-large",
                    Dimensions = 3072,
                    IsMultiModal = false,
                }
            },
            {
                "text-embedding-ada-002",
                new EmbeddingModelConfig
                {
                    Model = "text-embedding-ada-002",
                    Dimensions = 1536,
                    IsMultiModal = false,
                }
            },
        };
    }
}
