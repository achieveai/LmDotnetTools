using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.AgUi.Sample.Tools;

/// <summary>
/// Tool for getting current time and date information
/// Demonstrates simple synchronous tool execution
/// </summary>
public class TimeTool : IFunctionProvider
{
    private readonly ILogger<TimeTool> _logger;

    public TimeTool(ILogger<TimeTool> logger)
    {
        _logger = logger;
        _logger.LogDebug("TimeTool initialized");
    }

    public string ProviderName => "TimeProvider";
    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        var contract = new FunctionContract
        {
            Name = "get_current_time",
            Description = "Get the current date and time, optionally for a specific timezone",
            Parameters = new[]
            {
                new FunctionParameterContract
                {
                    Name = "timezone",
                    ParameterType = new JsonSchemaObject { Type = "string" },
                    Description = "Timezone identifier (e.g., 'UTC', 'America/New_York'). Default is UTC.",
                    IsRequired = false
                },
                new FunctionParameterContract
                {
                    Name = "format",
                    ParameterType = new JsonSchemaObject
                    {
                        Type = "string",
                        Enum = new[] { "iso", "friendly", "unix" }
                    },
                    Description = "Output format (iso, friendly, or unix timestamp)",
                    IsRequired = false
                }
            }.ToList()
        };

        yield return new FunctionDescriptor
        {
            Contract = contract,
            Handler = ExecuteAsync,
            ProviderName = ProviderName
        };
    }

    /// <summary>
    /// Executes time lookup
    /// </summary>
    private async Task<string> ExecuteAsync(string arguments)
    {
        _logger.LogInformation("TimeTool called with arguments: {Args}", arguments);

        try
        {
            var args = JsonSerializer.Deserialize<TimeArgs>(arguments);
            var timezone = args?.Timezone ?? "UTC";
            var format = args?.Format ?? "iso";

            _logger.LogDebug("Getting current time for timezone: {Timezone}, format: {Format}", timezone, format);

            // Simulate minimal delay
            await Task.Delay(10);

            TimeZoneInfo timeZone;
            try
            {
                timeZone = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            }
            catch
            {
                _logger.LogWarning("Invalid timezone '{Timezone}', defaulting to UTC", timezone);
                timeZone = TimeZoneInfo.Utc;
                timezone = "UTC";
            }

            var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, timeZone);

            var formattedTime = format switch
            {
                "friendly" => now.ToString("dddd, MMMM dd, yyyy 'at' hh:mm:ss tt"),
                "unix" => new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                _ => now.ToString("o")
            };

            var result = new
            {
                timezone = timezone,
                format = format,
                timestamp = formattedTime,
                iso = now.ToString("o"),
                unix = new DateTimeOffset(now).ToUnixTimeSeconds(),
                dayOfWeek = now.DayOfWeek.ToString(),
                utcOffset = timeZone.GetUtcOffset(now).ToString()
            };

            var json = JsonSerializer.Serialize(result);
            _logger.LogInformation("TimeTool returning: {Result}", json);

            return json;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse arguments for TimeTool");
            return JsonSerializer.Serialize(new { error = "Invalid JSON format", details = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TimeTool execution failed");
            return JsonSerializer.Serialize(new { error = "Internal error", details = ex.Message });
        }
    }

    private record TimeArgs(string? Timezone, string? Format);
}
