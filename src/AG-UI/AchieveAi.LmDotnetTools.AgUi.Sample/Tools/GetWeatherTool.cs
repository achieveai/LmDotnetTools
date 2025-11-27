using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Tools;

/// <summary>
///     Mock tool for getting weather information by city
///     Demonstrates simple function call with validation
/// </summary>
public class GetWeatherTool : IFunctionProvider
{
    private static readonly string[] Conditions = ["Sunny", "Cloudy", "Rainy", "Snowy", "Foggy", "Windy", "Stormy"];

    private static readonly string[] sourceArray = ["celsius", "fahrenheit"];
    private readonly ILogger<GetWeatherTool> _logger;

    public GetWeatherTool(ILogger<GetWeatherTool> logger)
    {
        _logger = logger;
        _logger.LogDebug("GetWeatherTool initialized");
    }

    public string ProviderName => "WeatherProvider";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "get_weather",
            Description = "Get current weather information for a specified city",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "city",
                    ParameterType = new JsonSchemaObject { Type = "string" },
                    Description = "The name of the city to get weather for",
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "units",
                    ParameterType = new JsonSchemaObject { Type = "string", Enum = sourceArray },
                    Description = "Temperature units (celsius or fahrenheit)",
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
    ///     Executes the weather lookup with mock data
    /// </summary>
    private async Task<string> ExecuteAsync(string arguments)
    {
        _logger.LogInformation("GetWeatherTool called with arguments: {Args}", arguments);

        try
        {
            var args = JsonSerializer.Deserialize<WeatherArgs>(arguments);
            if (args == null || string.IsNullOrWhiteSpace(args.City))
            {
                _logger.LogWarning("Invalid arguments provided to GetWeatherTool: {Args}", arguments);
                return JsonSerializer.Serialize(new { error = "City parameter is required" });
            }

            _logger.LogDebug("Fetching weather for city: {City}, units: {Units}", args.City, args.Units ?? "celsius");

            // Simulate API delay
            await Task.Delay(Random.Shared.Next(100, 300));

            // Generate mock weather data
            var temperature = args.Units == "fahrenheit" ? Random.Shared.Next(40, 90) : Random.Shared.Next(5, 32);

            var result = new
            {
                city = args.City,
                temperature,
                units = args.Units ?? "celsius",
                condition = Conditions[Random.Shared.Next(Conditions.Length)],
                humidity = Random.Shared.Next(30, 90),
                windSpeed = Random.Shared.Next(5, 30),
                timestamp = DateTime.UtcNow.ToString("o"),
            };

            var json = JsonSerializer.Serialize(result);
            _logger.LogInformation("GetWeatherTool returning: {Result}", json);

            return json;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse arguments for GetWeatherTool");
            return JsonSerializer.Serialize(new { error = "Invalid JSON format", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetWeatherTool execution failed");
            return JsonSerializer.Serialize(new { error = "Internal error", details = ex.Message });
        }
    }

    private record WeatherArgs(string? City, string? Units = "celsius");
}
