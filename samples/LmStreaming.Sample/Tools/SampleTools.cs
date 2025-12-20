using System.ComponentModel;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace LmStreaming.Sample.Tools;

/// <summary>
/// Mock tools for the streaming sample application.
/// These tools demonstrate how MultiTurnAgentLoop handles tool calls.
/// </summary>
public static class SampleTools
{
    private static readonly Random Random = new();

    /// <summary>
    /// Get current weather for a location.
    /// </summary>
    [Function("get_weather", "Get current weather conditions for a specific location")]
    public static string GetWeather(
        [Description("City name, e.g., 'New York', 'London', 'Tokyo'")] string location)
    {
        // Mock weather data
        var conditions = new[] { "Sunny", "Cloudy", "Partly Cloudy", "Rainy", "Clear" };
        var result = new
        {
            location,
            temperature = Random.Next(45, 95),
            temperatureUnit = "F",
            condition = conditions[Random.Next(conditions.Length)],
            humidity = Random.Next(30, 80),
            windSpeed = Random.Next(5, 25),
            windUnit = "mph"
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Perform basic arithmetic calculations.
    /// </summary>
    [Function("calculate", "Perform basic arithmetic operations: add, subtract, multiply, divide")]
    public static string Calculate(
        [Description("First number")] double a,
        [Description("Operation: 'add', 'subtract', 'multiply', or 'divide'")] string operation,
        [Description("Second number")] double b)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var result = operation.ToLowerInvariant() switch
        {
            "add" or "+" => a + b,
            "subtract" or "-" => a - b,
            "multiply" or "*" => a * b,
            "divide" or "/" when b != 0 => a / b,
            "divide" or "/" => double.NaN,
            _ => throw new ArgumentException($"Unknown operation: {operation}. Use 'add', 'subtract', 'multiply', or 'divide'.")
        };

        var response = new
        {
            expression = $"{a} {operation} {b}",
            result,
            operation,
            a,
            b
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Search the web for information.
    /// </summary>
    [Function("web_search", "Search the web for information on a topic")]
    public static string WebSearch(
        [Description("Search query")] string query,
        [Description("Number of results to return (1-10, default: 3)")] int numResults = 3)
    {
        numResults = Math.Clamp(numResults, 1, 10);

        // Mock search results
        var results = Enumerable.Range(1, numResults).Select(i => new
        {
            title = $"Result {i}: {query}",
            url = $"https://example.com/search/{Uri.EscapeDataString(query)}/{i}",
            snippet = $"This is a mock search result {i} for the query '{query}'. " +
                     $"In a real implementation, this would contain actual search results from a search engine API.",
            publishedDate = DateTime.UtcNow.AddDays(-Random.Next(1, 365)).ToString("yyyy-MM-dd")
        }).ToList();

        var response = new
        {
            query,
            totalResults = numResults,
            results
        };

        return JsonSerializer.Serialize(response, new JsonSerializerOptions { WriteIndented = true });
    }
}
