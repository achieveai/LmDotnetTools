using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Tools;

/// <summary>
/// Mock search tool for demonstrating search functionality
/// Returns mock search results based on query
/// </summary>
public class SearchTool : IFunctionProvider
{
    private readonly ILogger<SearchTool> _logger;
    private static readonly string[] MockTitles =
    [
        "Understanding AG-UI Protocol",
        "Building Real-time WebSocket Applications",
        "LmCore Agent Framework Guide",
        "Tool Calling in AI Agents",
        "Streaming Event Systems",
    ];

    public SearchTool(ILogger<SearchTool> logger)
    {
        _logger = logger;
        _logger.LogDebug("SearchTool initialized");
    }

    public string ProviderName => "SearchProvider";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "search",
            Description = "Search for information on a given query",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "query",
                    ParameterType = new JsonSchemaObject { Type = "string" },
                    Description = "The search query",
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "max_results",
                    ParameterType = new JsonSchemaObject { Type = "integer" },
                    Description = "Maximum number of results to return (default: 5)",
                    IsRequired = false,
                },
            ],
        };

        yield return new FunctionDescriptor
        {
            Contract = contract,
            Handler = ExecuteAsync,
            ProviderName = ProviderName,
        };
    }

    /// <summary>
    /// Executes the search with mock results
    /// </summary>
    private async Task<string> ExecuteAsync(string arguments)
    {
        _logger.LogInformation("SearchTool called with arguments: {Args}", arguments);

        try
        {
            var args = JsonSerializer.Deserialize<SearchArgs>(arguments);
            if (args == null || string.IsNullOrWhiteSpace(args.Query))
            {
                _logger.LogWarning("Invalid arguments provided to SearchTool");
                return JsonSerializer.Serialize(new { error = "Query parameter is required" });
            }

            var maxResults = Math.Min(args.MaxResults ?? 5, 10);
            _logger.LogDebug("Searching for query: {Query}, max results: {MaxResults}", args.Query, maxResults);

            // Simulate search delay
            await Task.Delay(Random.Shared.Next(200, 500));

            // Generate mock search results
            var results = Enumerable
                .Range(0, maxResults)
                .Select(i => new
                {
                    title = MockTitles[i % MockTitles.Length],
                    url = $"https://example.com/result-{i + 1}",
                    snippet = $"Mock search result for query '{args.Query}'. This is result #{i + 1} containing relevant information about {args.Query}.",
                    relevance = Random.Shared.NextDouble(),
                })
                .OrderByDescending(r => r.relevance)
                .ToList();

            var response = new
            {
                query = args.Query,
                totalResults = results.Count,
                results,
                timestamp = DateTime.UtcNow.ToString("o"),
            };

            var json = JsonSerializer.Serialize(response);
            _logger.LogInformation(
                "SearchTool returning {Count} results for query: {Query}",
                results.Count,
                args.Query
            );

            return json;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse arguments for SearchTool");
            return JsonSerializer.Serialize(new { error = "Invalid JSON format", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SearchTool execution failed");
            return JsonSerializer.Serialize(new { error = "Internal error", details = ex.Message });
        }
    }

    private record SearchArgs(string? Query, int? MaxResults);
}
