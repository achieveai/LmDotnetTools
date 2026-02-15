using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using LmStreaming.Sample.Models;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

[ApiController]
[Route("api/diagnostics")]
public class DiagnosticsController(ILogger<DiagnosticsController> logger) : ControllerBase
{
    /// <summary>
    ///     Returns the active provider configuration so you can verify
    ///     which LLM backend the service is connected to.
    /// </summary>
    [HttpGet("provider-info")]
    public IActionResult GetProviderInfo()
    {
        var providerMode = Environment.GetEnvironmentVariable("LM_PROVIDER_MODE") ?? "test";
        var info = new Dictionary<string, string?>
        {
            ["providerMode"] = providerMode,
        };

        switch (providerMode.ToLowerInvariant())
        {
            case "anthropic":
                info["baseUrl"] = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
                info["model"] = Environment.GetEnvironmentVariable("ANTHROPIC_MODEL")
                    ?? "claude-sonnet-4-20250514";
                info["apiKeyConfigured"] = (!string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))).ToString();
                break;
            case "openai":
                info["baseUrl"] = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
                info["model"] = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4o";
                info["apiKeyConfigured"] = (!string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable("OPENAI_API_KEY"))).ToString();
                break;
            default:
                info["baseUrl"] = "http://test-mode/v1";
                info["model"] = "test-model";
                info["apiKeyConfigured"] = "N/A";
                break;
        }

        logger.LogInformation(
            "Provider info requested - Mode: {ProviderMode}, BaseUrl: {BaseUrl}, Model: {Model}",
            info["providerMode"],
            info["baseUrl"],
            info["model"]);

        return Ok(info);
    }
    [HttpPost("logs")]
    public IActionResult IngestLogs([FromBody] ClientLogBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        foreach (var entry in batch.Entries)
        {
            var level = entry.Level?.ToLowerInvariant() switch
            {
                "error" => LogLevel.Error,
                "warn" or "warning" => LogLevel.Warning,
                "info" or "information" => LogLevel.Information,
                "debug" => LogLevel.Debug,
                "trace" => LogLevel.Trace,
                _ => LogLevel.Information
            };

            using (Serilog.Context.LogContext.PushProperty("ClientTimestamp", entry.Timestamp))
            using (Serilog.Context.LogContext.PushProperty("ClientFile", entry.File))
            using (Serilog.Context.LogContext.PushProperty("ClientLine", entry.Line))
            using (Serilog.Context.LogContext.PushProperty("ClientFunction", entry.Function))
            using (Serilog.Context.LogContext.PushProperty("ClientComponent", entry.Component))
            using (Serilog.Context.LogContext.PushProperty("Source", "Browser"))
            {
                if (entry.Data is JsonElement jsonElement && jsonElement.ValueKind != JsonValueKind.Undefined && jsonElement.ValueKind != JsonValueKind.Null)
                {
                    try
                    {
                        var rawText = jsonElement.GetRawText();
                        using (Serilog.Context.LogContext.PushProperty("ClientData", rawText))
                        {
                            logger.Log(level, "[Client] {Message}", entry.Message);
                        }
                    }
                    catch
                    {
                        logger.Log(level, "[Client] {Message}", entry.Message);
                    }
                }
                else if (entry.Data != null)
                {
                    using (Serilog.Context.LogContext.PushProperty("ClientData", entry.Data, destructureObjects: true))
                    {
                        logger.Log(level, "[Client] {Message}", entry.Message);
                    }
                }
                else
                {
                    logger.Log(level, "[Client] {Message}", entry.Message);
                }
            }
        }

        return Ok(new { received = batch.Entries.Length });
    }

    [HttpGet("serialization-samples")]
    public IActionResult GetSerializationSamples()
    {
        logger.LogDebug("Serialization-samples endpoint called");

        var jsonOptions = JsonSerializerOptionsFactory.CreateForProduction();

        IMessage[] messages =
        [
            new TextMessage { Role = Role.User, Text = "Hello!" },
            new TextUpdateMessage { Role = Role.Assistant, Text = "Hi there", IsUpdate = true },
            new ToolsCallMessage
            {
                Role = Role.Assistant,
                ToolCalls = [new ToolCall { FunctionName = "get_weather", ToolCallId = "call_123", FunctionArgs = /*lang=json,strict*/ "{\"location\": \"NYC\"}" }]
            }
        ];

        var result = messages.Select(m => new
        {
            Type = m.GetType().Name,
            Json = JsonSerializer.Serialize(m, jsonOptions)
        }).ToList();

        logger.LogInformation(
            "Returning {MessageCount} message types: {Types}",
            result.Count,
            string.Join(", ", result.Select(r => r.Type)));

        return Ok(result);
    }
}
