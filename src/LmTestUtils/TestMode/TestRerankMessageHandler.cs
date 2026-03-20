using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

public class TestRerankMessageHandler : HttpMessageHandler
{
    private readonly ILogger<TestRerankMessageHandler> _logger;

    public TestRerankMessageHandler(ILogger<TestRerankMessageHandler>? logger = null)
    {
        _logger = logger ?? NullLogger<TestRerankMessageHandler>.Instance;
    }

    public string Model { get; set; } = "test-rerank-model";
    public TimeSpan? Delay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _logger.LogTrace("SendAsync called - Method: {Method}, URI: {Uri}", request.Method, request.RequestUri);

        if (Delay.HasValue && Delay.Value > TimeSpan.Zero)
        {
            _logger.LogTrace("Delaying response by {Delay}", Delay.Value);
            await Task.Delay(Delay.Value, cancellationToken);
        }

        var responseJson = CreateValidRerankResponse(Model);
        _logger.LogTrace("Returning rerank response for model {Model}", Model);
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json")
        };
        return response;
    }

    private static string CreateValidRerankResponse(string model)
    {
        var random = new Random(42);
        var response = new
        {
            results = new[]
            {
                new
                {
                    index = 0,
                    relevance_score = Math.Round(random.NextDouble(), 4),
                    document = new { text = "test_document" }
                }
            },
            model,
            usage = new { total_tokens = 5 }
        };

        return JsonSerializer.Serialize(response);
    }
}
