using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Transport;

/// <summary>
/// Writes a JSONL trace of JSON-RPC 2.0 messages exchanged with the Copilot ACP
/// server, redacting sensitive keys (apiKey/authorization/tokens) before flushing.
/// </summary>
internal sealed class CopilotRpcTraceWriter : IAsyncDisposable
{
    private static readonly HashSet<string> RedactedKeys = new(
    [
        "apiKey",
        "api_key",
        "authorization",
        "token",
        "access_token",
        "refresh_token",
        "github_token",
        "githubToken",
        "copilot_api_key",
        "copilotApiKey",
    ],
        StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly string _sessionId;
    private readonly ILogger? _logger;
    private StreamWriter? _writer;
    private int _disposed;

    public CopilotRpcTraceWriter(string filePath, string? sessionId, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _sessionId = string.IsNullOrWhiteSpace(sessionId) ? "unknown" : sessionId;
        _logger = logger;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        _writer = new StreamWriter(filePath, append: true, Encoding.UTF8);
    }

    public async Task WriteAsync(string direction, string line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var writer = _writer;
        if (writer == null)
        {
            return;
        }

        JsonElement payloadElement;
        string payloadJson;
        string? messageKind = null;
        string? method = null;
        string? rpcId = null;
        string? sessionId = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var redactedPayload = RedactElement(doc.RootElement);
            payloadJson = redactedPayload.GetRawText();
            payloadElement = redactedPayload;

            if (payloadElement.ValueKind == JsonValueKind.Object)
            {
                var hasMethod = payloadElement.TryGetProperty("method", out var methodProp)
                                && methodProp.ValueKind == JsonValueKind.String;
                var hasId = payloadElement.TryGetProperty("id", out var idProp);
                var hasResult = payloadElement.TryGetProperty("result", out _);
                var hasError = payloadElement.TryGetProperty("error", out _);

                messageKind = hasMethod && hasId ? "request"
                    : hasMethod ? "notification"
                    : hasId && (hasResult || hasError) ? "response"
                    : "unknown";

                if (hasMethod)
                {
                    method = methodProp.GetString();
                }

                if (hasId)
                {
                    rpcId = idProp.ValueKind switch
                    {
                        JsonValueKind.Number => idProp.ToString(),
                        JsonValueKind.String => idProp.GetString(),
                        _ => null,
                    };
                }

                if (payloadElement.TryGetProperty("params", out var paramsProp)
                    && paramsProp.ValueKind == JsonValueKind.Object)
                {
                    sessionId = TryGetString(paramsProp, "sessionId")
                        ?? TryGetString(paramsProp, "session_id");
                }
            }
        }
        catch (JsonException)
        {
            payloadJson = line;
            messageKind = "unknown";
        }

        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson)));
        var envelope = JsonSerializer.Serialize(new
        {
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
            copilot_session_id = _sessionId,
            direction,
            message_kind = messageKind ?? "unknown",
            rpc_id = rpcId,
            method,
            session_id = sessionId,
            payload_sha256 = payloadHash,
            payload = payloadJson,
        });

        await _writeLock.WaitAsync(ct);
        try
        {
            writer = _writer;
            if (writer == null)
            {
                return;
            }

            await writer.WriteLineAsync(envelope);
            await writer.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "{event_type} {event_status} {error_code}",
                "copilot.rpc_trace.write",
                "failed",
                "trace_write_failed");
        }
        finally
        {
            _ = _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _writeLock.WaitAsync();
        try
        {
            if (_writer != null)
            {
                await _writer.DisposeAsync();
                _writer = null;
            }
        }
        finally
        {
            _ = _writeLock.Release();
            _writeLock.Dispose();
        }
    }

    private static JsonElement RedactElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteRedacted(writer, element);
            writer.Flush();
        }

        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }

    private static void WriteRedacted(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (RedactedKeys.Contains(property.Name))
                    {
                        writer.WriteStringValue("***REDACTED***");
                    }
                    else
                    {
                        WriteRedacted(writer, property.Value);
                    }
                }

                writer.WriteEndObject();
                return;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedacted(writer, item);
                }

                writer.WriteEndArray();
                return;
            case JsonValueKind.Undefined:
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
            default:
                element.WriteTo(writer);
                return;
        }
    }

    private static string? TryGetString(JsonElement obj, string propertyName)
    {
        return obj.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }
}
