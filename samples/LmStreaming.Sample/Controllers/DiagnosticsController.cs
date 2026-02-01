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
