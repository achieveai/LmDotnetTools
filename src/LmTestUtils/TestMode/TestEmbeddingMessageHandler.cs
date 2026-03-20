using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

public class TestEmbeddingMessageHandler : HttpMessageHandler
{
    private readonly ILogger<TestEmbeddingMessageHandler> _logger;

    public TestEmbeddingMessageHandler(ILogger<TestEmbeddingMessageHandler>? logger = null)
    {
        _logger = logger ?? NullLogger<TestEmbeddingMessageHandler>.Instance;
    }

    public int EmbeddingSize { get; set; } = 1536;
    public string Model { get; set; } = "test-model";
    public TimeSpan? Delay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _logger.LogTrace("SendAsync called - Method: {Method}, URI: {Uri}", request.Method, request.RequestUri);

        if (Delay.HasValue && Delay.Value > TimeSpan.Zero)
        {
            _logger.LogTrace("Delaying response by {Delay}", Delay.Value);
            await Task.Delay(Delay.Value, cancellationToken);
        }

        var responseJson = CreateValidEmbeddingResponse(EmbeddingSize, Model);
        _logger.LogTrace("Returning embedding response with {EmbeddingSize} dimensions", EmbeddingSize);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };
        return response;
    }

    private static string CreateValidEmbeddingResponse(int embeddingSize, string model)
    {
        var random = new Random(42);
        var vector = new float[embeddingSize];
        for (var i = 0; i < embeddingSize; i++)
        {
            vector[i] = (float)((random.NextDouble() * 2.0) - 1.0);
        }

        var response = new
        {
            Embeddings = new[]
            {
                new { Vector = vector, Index = 0, Text = "test_input" }
            },
            Model = model,
            Usage = new { PromptTokens = 10, TotalTokens = 10 }
        };

        return JsonSerializer.Serialize(response);
    }
}
