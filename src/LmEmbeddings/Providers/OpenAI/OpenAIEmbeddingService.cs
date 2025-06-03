using AchieveAi.LmDotnetTools.LmEmbeddings.Core;
using AchieveAi.LmDotnetTools.LmEmbeddings.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Providers.OpenAI;

/// <summary>
/// OpenAI implementation of the embedding service
/// </summary>
public class OpenAIEmbeddingService : BaseEmbeddingService
{
    private readonly OpenAIEmbeddingOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public OpenAIEmbeddingService(
        ILogger<OpenAIEmbeddingService> logger,
        HttpClient httpClient,
        OpenAIEmbeddingOptions options) : base(logger, httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        // Configure HttpClient
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            HttpClient.DefaultRequestHeaders.Authorization = 
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
        
        if (!string.IsNullOrEmpty(_options.BaseUrl))
        {
            HttpClient.BaseAddress = new Uri(_options.BaseUrl);
        }
    }

    /// <inheritdoc />
    public override int EmbeddingSize => GetEmbeddingSizeForModel(_options.DefaultModel ?? "text-embedding-3-small");

    /// <summary>
    /// Gets the embedding size for a specific OpenAI model
    /// </summary>
    /// <param name="model">The model name</param>
    /// <returns>The embedding size for the model</returns>
    private static int GetEmbeddingSizeForModel(string model)
    {
        return model switch
        {
            "text-embedding-3-small" => 1536,
            "text-embedding-3-large" => 3072,
            "text-embedding-ada-002" => 1536,
            _ => 1536 // Default to text-embedding-3-small size
        };
    }

    public override async Task<EmbeddingResponse> GenerateEmbeddingsAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        return await ExecuteWithRetryAsync(async () =>
        {
            // Use the base class method to format the request based on API type
            var requestPayload = FormatRequestPayload(request);

            var json = JsonSerializer.Serialize(requestPayload, _jsonOptions);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            Logger.LogDebug("Sending embedding request to OpenAI for {InputCount} inputs using model {Model} with API type {ApiType}", 
                request.Inputs.Count, request.Model, request.ApiType);

            var response = await HttpClient.PostAsync("/v1/embeddings", content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var openAIResponse = JsonSerializer.Deserialize<OpenAIEmbeddingResponse>(responseJson, _jsonOptions);

            if (openAIResponse?.Data == null)
                throw new InvalidOperationException("Invalid response from OpenAI API");

            var embeddings = openAIResponse.Data.Select((item, index) => new EmbeddingItem
            {
                Vector = DecodeEmbedding(item.Embedding, request.EncodingFormat ?? "base64"),
                Index = item.Index,
                Text = request.Inputs.ElementAtOrDefault(item.Index)
            }).ToArray();

            return new EmbeddingResponse
            {
                Embeddings = embeddings,
                Model = openAIResponse.Model,
                Usage = openAIResponse.Usage != null ? new EmbeddingUsage
                {
                    PromptTokens = openAIResponse.Usage.PromptTokens,
                    TotalTokens = openAIResponse.Usage.TotalTokens
                } : null
            };
        }, cancellationToken: cancellationToken);
    }

    public override async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        // Return known OpenAI embedding models
        return await Task.FromResult(new[]
        {
            "text-embedding-3-small",
            "text-embedding-3-large", 
            "text-embedding-ada-002"
        }.AsReadOnly());
    }

    private static float[] DecodeEmbedding(object embedding, string encodingFormat)
    {
        return encodingFormat.ToLowerInvariant() switch
        {
            "base64" => DecodeBase64Embedding(embedding.ToString()!),
            "float" => ((JsonElement)embedding).EnumerateArray().Select(x => x.GetSingle()).ToArray(),
            _ => throw new NotSupportedException($"Encoding format '{encodingFormat}' is not supported")
        };
    }

    private static float[] DecodeBase64Embedding(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    // Internal models for OpenAI API responses
    private class OpenAIEmbeddingResponse
    {
        public string Object { get; set; } = "";
        public OpenAIEmbeddingData[] Data { get; set; } = Array.Empty<OpenAIEmbeddingData>();
        public string Model { get; set; } = "";
        public OpenAIUsage? Usage { get; set; }
    }

    private class OpenAIEmbeddingData
    {
        public string Object { get; set; } = "";
        public object Embedding { get; set; } = new object();
        public int Index { get; set; }
    }

    private class OpenAIUsage
    {
        public int PromptTokens { get; set; }
        public int TotalTokens { get; set; }
    }
} 