using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using LmStreaming.Sample.Models;
using Microsoft.AspNetCore.Mvc;

namespace LmStreaming.Sample.Controllers;

[ApiController]
[Route("api/diagnostics")]
public class DiagnosticsController(ILogger<DiagnosticsController> logger) : ControllerBase
{
    private static readonly Regex SemVerRegex = new(@"\b(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)\b", RegexOptions.Compiled);

    /// <summary>
    ///     Returns the active provider configuration so you can verify
    ///     which LLM backend the service is connected to.
    /// </summary>
    [HttpGet("provider-info")]
    public async Task<IActionResult> GetProviderInfo()
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
            case "codex":
                var requestAborted = HttpContext?.RequestAborted ?? CancellationToken.None;
                info["baseUrl"] = Environment.GetEnvironmentVariable("CODEX_BASE_URL")
                    ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
                    ?? "https://api.openai.com/v1";
                info["model"] = Environment.GetEnvironmentVariable("CODEX_MODEL") ?? "gpt-5.3-codex";
                info["apiKeyConfigured"] = (!string.IsNullOrEmpty(
                    Environment.GetEnvironmentVariable("CODEX_API_KEY"))).ToString();
                info["authMode"] = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_API_KEY"))
                    ? "api_key"
                    : "chatgpt_login";
                var effectiveMcpPort = GetEffectiveCodexMcpPort();
                info["mcpPortEffective"] = effectiveMcpPort.ToString();
                info["mcpEndpointUrl"] = $"http://localhost:{effectiveMcpPort}/mcp";
                info["rpcTraceEnabled"] = GetRpcTraceEnabled().ToString();

                var codexCliPath = Environment.GetEnvironmentVariable("CODEX_CLI_PATH") ?? "codex";
                var startupTimeoutMs = int.TryParse(
                    Environment.GetEnvironmentVariable("CODEX_APP_SERVER_STARTUP_TIMEOUT_MS"),
                    out var parsedStartupTimeoutMs)
                    ? parsedStartupTimeoutMs
                    : 30000;

                info["codexCliPath"] = codexCliPath;

                var (codexCliDetected, codexCliVersion, codexCliError) = await ProbeCodexCliAsync(
                    codexCliPath,
                    startupTimeoutMs,
                    requestAborted);
                info["codexCliDetected"] = codexCliDetected.ToString();
                info["codexCliVersion"] = codexCliVersion;

                var appServerHandshakeOk = false;
                var appServerLastError = codexCliError;
                if (codexCliDetected)
                {
                    var (handshakeOk, handshakeError) = await ProbeAppServerHandshakeAsync(
                        codexCliPath,
                        startupTimeoutMs,
                        requestAborted);
                    appServerHandshakeOk = handshakeOk;
                    appServerLastError = handshakeError;
                }

                info["appServerHandshakeOk"] = appServerHandshakeOk.ToString();
                info["appServerLastError"] = appServerLastError;
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

    private static int GetEffectiveCodexMcpPort()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("CODEX_MCP_PORT_EFFECTIVE"), out var effectivePort)
            && effectivePort > 0
            && effectivePort <= 65535
            ? effectivePort
            : GetConfiguredCodexMcpPort();
    }

    private static int GetConfiguredCodexMcpPort()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("CODEX_MCP_PORT"), out var port)
            && port > 0
            && port <= 65535
            ? port
            : 39200;
    }

    private static bool GetRpcTraceEnabled()
    {
        return bool.TryParse(Environment.GetEnvironmentVariable("CODEX_RPC_TRACE_ENABLED"), out var enabled)
               && enabled;
    }

    private static async Task<(bool Detected, string? Version, string? Error)> ProbeCodexCliAsync(
        string codexCliPath,
        int timeoutMs,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = codexCliPath,
            Arguments = "--version",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process == null)
            {
                return (false, null, $"Failed to start Codex CLI '{codexCliPath}'.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 1000)));

            var stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            var output = string.Join(Environment.NewLine, [stdOut, stdErr]).Trim();

            if (process.ExitCode != 0)
            {
                return (false, null, $"Codex CLI '--version' exited with code {process.ExitCode}: {Truncate(output)}");
            }

            var match = SemVerRegex.Match(output);
            if (!match.Success)
            {
                return (false, null, $"Could not parse Codex CLI version from output: {Truncate(output)}");
            }

            var version = $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}";
            return (true, version, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, null, "Codex CLI probe cancelled.");
        }
        catch (OperationCanceledException)
        {
            return (false, null, "Codex CLI probe timed out.");
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static async Task<(bool Ok, string? Error)> ProbeAppServerHandshakeAsync(
        string codexCliPath,
        int timeoutMs,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = codexCliPath,
            Arguments = "app-server --listen stdio://",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process == null)
            {
                return (false, $"Failed to start Codex App Server using '{codexCliPath}'.");
            }

            using var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            var initializeRequest = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    clientInfo = new
                    {
                        name = "lm-dotnet-tools-diagnostics",
                        version = "0.1.0",
                    },
                    capabilities = new
                    {
                        experimentalApi = true,
                    },
                },
            });
            await writer.WriteLineAsync(initializeRequest);
            await writer.WriteLineAsync("""{"jsonrpc":"2.0","method":"initialized"}""");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(timeoutMs, 1_000)));

            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(timeoutCts.Token);
                if (line == null)
                {
                    var stderr = await process.StandardError.ReadToEndAsync(timeoutCts.Token);
                    return (false, $"App Server stdout closed before initialize response. stderr: {Truncate(stderr)}");
                }

                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (!root.TryGetProperty("id", out var idProp)
                    || idProp.ValueKind != JsonValueKind.Number
                    || idProp.GetInt32() != 1)
                {
                    continue;
                }

                if (root.TryGetProperty("result", out _))
                {
                    return (true, null);
                }

                if (root.TryGetProperty("error", out var errorProp)
                    && errorProp.ValueKind == JsonValueKind.Object
                    && errorProp.TryGetProperty("message", out var messageProp)
                    && messageProp.ValueKind == JsonValueKind.String)
                {
                    return (false, messageProp.GetString());
                }

                return (false, $"Unexpected initialize response: {Truncate(line)}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (false, "App Server handshake cancelled.");
        }
        catch (OperationCanceledException)
        {
            return (false, "App Server handshake timed out.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
        finally
        {
            if (process != null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore kill failures in diagnostics probing.
                }

                process.Dispose();
            }
        }
    }

    private static string Truncate(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= 500 ? value : value[..500];
    }
}
