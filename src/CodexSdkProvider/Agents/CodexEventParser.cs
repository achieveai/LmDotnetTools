using System.Text.Json;

namespace AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;

/// <summary>
/// Static helpers for parsing and validating Codex JSON event payloads.
/// </summary>
internal static class CodexEventParser
{
    public static string? ExtractThreadId(JsonElement? root)
    {
        if (!root.HasValue || root.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.Value.TryGetProperty("threadId", out var threadIdProp)
            && threadIdProp.ValueKind == JsonValueKind.String)
        {
            return threadIdProp.GetString();
        }

        return root.Value.TryGetProperty("thread_id", out var threadIdSnake)
            && threadIdSnake.ValueKind == JsonValueKind.String
            ? threadIdSnake.GetString()
            : root.Value.TryGetProperty("thread", out var threadProp)
            && threadProp.ValueKind == JsonValueKind.Object
            && threadProp.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : null;
    }

    public static string? ExtractTurnId(JsonElement? root)
    {
        if (!root.HasValue || root.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.Value.TryGetProperty("turnId", out var turnIdProp)
            && turnIdProp.ValueKind == JsonValueKind.String)
        {
            return turnIdProp.GetString();
        }

        return root.Value.TryGetProperty("turn_id", out var turnIdSnake)
            && turnIdSnake.ValueKind == JsonValueKind.String
            ? turnIdSnake.GetString()
            : root.Value.TryGetProperty("turn", out var turnProp)
            && turnProp.ValueKind == JsonValueKind.Object
            && turnProp.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
            : null;
    }

    public static string? ExtractTurnStatus(JsonElement? root)
    {
        if (!root.HasValue || root.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return root.Value.TryGetProperty("status", out var statusProp)
            && statusProp.ValueKind == JsonValueKind.String
            ? statusProp.GetString()
            : root.Value.TryGetProperty("turn", out var turnProp)
            && turnProp.ValueKind == JsonValueKind.Object
            && turnProp.TryGetProperty("status", out var turnStatus)
            && turnStatus.ValueKind == JsonValueKind.String
            ? turnStatus.GetString()
            : null;
    }

    public static string? ExtractTurnErrorMessage(JsonElement? root)
    {
        if (!root.HasValue || root.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return root.Value.TryGetProperty("error", out var errorProp)
            && errorProp.ValueKind == JsonValueKind.Object
            && errorProp.TryGetProperty("message", out var messageProp)
            && messageProp.ValueKind == JsonValueKind.String
            ? messageProp.GetString()
            : root.Value.TryGetProperty("turn", out var turnProp)
            && turnProp.ValueKind == JsonValueKind.Object
            && turnProp.TryGetProperty("error", out var turnErrorProp)
            && turnErrorProp.ValueKind == JsonValueKind.Object
            && turnErrorProp.TryGetProperty("message", out var turnMessageProp)
            && turnMessageProp.ValueKind == JsonValueKind.String
            ? turnMessageProp.GetString()
            : null;
    }

    public static string? ExtractErrorMessage(JsonElement payload)
    {
        if (TryGetProperty(payload, "error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            if (error.ValueKind == JsonValueKind.Object
                && TryGetProperty(error, "message", out var message)
                && message.ValueKind == JsonValueKind.String)
            {
                return message.GetString();
            }
        }

        return TryGetProperty(payload, "message", out var fallbackMessage)
            && fallbackMessage.ValueKind == JsonValueKind.String
            ? fallbackMessage.GetString()
            : null;
    }

    public static string? GetPropertyString(JsonElement? root, string propertyName)
    {
        return !root.HasValue || root.Value.ValueKind != JsonValueKind.Object
            ? null
            : root.Value.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    public static JsonElement? GetPropertyElement(JsonElement? root, string propertyName)
    {
        return !root.HasValue || root.Value.ValueKind != JsonValueKind.Object
            ? null
            : root.Value.TryGetProperty(propertyName, out var property) ? property.Clone() : null;
    }

    public static bool TryGetProperty(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out value))
        {
            value = value.Clone();
            return true;
        }

        value = default;
        return false;
    }

    public static bool IsItemStartedMethod(string method)
    {
        return string.Equals(method, "item/started", StringComparison.Ordinal)
               || string.Equals(method, "item.started", StringComparison.Ordinal);
    }

    public static bool IsItemCompletedMethod(string method)
    {
        return string.Equals(method, "item/completed", StringComparison.Ordinal)
               || string.Equals(method, "item.completed", StringComparison.Ordinal);
    }

    public static bool IsWebSearchBeginMethod(string method)
    {
        return string.Equals(method, "codex/event/web_search_begin", StringComparison.Ordinal)
               || string.Equals(method, "codex.event.web_search_begin", StringComparison.Ordinal);
    }

    public static bool IsWebSearchEndMethod(string method)
    {
        return string.Equals(method, "codex/event/web_search_end", StringComparison.Ordinal)
               || string.Equals(method, "codex.event.web_search_end", StringComparison.Ordinal);
    }

    public static bool IsInProgress(string status)
    {
        return string.Equals(status, "in_progress", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "inProgress", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "inprogress", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTerminalTurnStatus(string status)
    {
        return !IsInProgress(status);
    }

    public static bool IsTurnFailureStatus(string? status)
    {
        return string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsTurnFailureNotification(string method)
    {
        return string.Equals(method, "turn/failed", StringComparison.Ordinal)
               || string.Equals(method, "turn.failed", StringComparison.Ordinal)
               || string.Equals(method, "turn/interrupted", StringComparison.Ordinal)
               || string.Equals(method, "turn.interrupted", StringComparison.Ordinal)
               || string.Equals(method, "turn/cancelled", StringComparison.Ordinal)
               || string.Equals(method, "turn.cancelled", StringComparison.Ordinal)
               || string.Equals(method, "turn/canceled", StringComparison.Ordinal)
               || string.Equals(method, "turn.canceled", StringComparison.Ordinal);
    }

    public static string NormalizeInternalToolStatus(string? status, bool hasError)
    {
        return string.IsNullOrWhiteSpace(status)
            ? hasError ? "error" : "success"
            : status switch
            {
                "completed" => hasError ? "error" : "success",
                "success" => "success",
                "failed" => "error",
                "error" => "error",
                "interrupted" => "cancelled",
                "cancelled" => "cancelled",
                "canceled" => "cancelled",
                "timed_out" => "timed_out",
                "timeout" => "timed_out",
                _ => hasError ? "error" : "success",
            };
    }

    public static string? NormalizeInternalToolName(string? itemType)
    {
        return itemType switch
        {
            "webSearch" => "web_search",
            "web_search" => "web_search",
            "commandExecution" => "command_execution",
            "command_execution" => "command_execution",
            "fileChange" => "file_change",
            "file_change" => "file_change",
            "todoList" => "todo_list",
            "todo_list" => "todo_list",
            _ => null,
        };
    }

    public static bool TryParseInternalToolItem(
        JsonElement? parameters,
        out JsonElement item,
        out string toolName,
        out string toolCallId)
    {
        item = default;
        toolName = string.Empty;
        toolCallId = string.Empty;

        if (!parameters.HasValue || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetProperty(parameters.Value, "item", out var itemElement)
            || itemElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryGetProperty(itemElement, "type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var normalizedTool = NormalizeInternalToolName(typeElement.GetString());
        if (normalizedTool == null)
        {
            return false;
        }

        var callId = GetPropertyString(itemElement, "id")
                     ?? GetPropertyString(itemElement, "call_id")
                     ?? GetPropertyString(itemElement, "callId");
        if (string.IsNullOrWhiteSpace(callId))
        {
            return false;
        }

        item = itemElement.Clone();
        toolName = normalizedTool;
        toolCallId = callId;
        return true;
    }

    public static void AddToolSpecificFields(
        Dictionary<string, object?> destination,
        string toolName,
        JsonElement payload,
        bool isResultPayload)
    {
        switch (toolName)
        {
            case "web_search":
                AddStringField(destination, payload, "query");
                AddStringField(destination, payload, "action");
                AddStringField(destination, payload, "target_url", "target_url", "targetUrl", "url");
                if (isResultPayload)
                {
                    AddStringField(destination, payload, "opened_url", "opened_url", "openedUrl", "url");
                    AddRawField(destination, payload, "matches", "matches", "resultMatches");
                    AddRawField(destination, payload, "snippets", "snippets", "results");
                }
                else
                {
                    AddRawField(destination, payload, "filters", "filters");
                }

                break;

            case "command_execution":
                AddStringField(destination, payload, "command");
                AddStringField(destination, payload, "cwd", "cwd", "workingDirectory");
                AddIntField(destination, payload, "timeout_ms", "timeout_ms", "timeoutMs");
                if (isResultPayload)
                {
                    AddIntField(destination, payload, "exit_code", "exit_code", "exitCode");
                    AddStringField(destination, payload, "stdout_excerpt", "stdout_excerpt", "stdout", "output");
                    AddStringField(destination, payload, "stderr_excerpt", "stderr_excerpt", "stderr");
                }

                break;

            case "file_change":
                AddStringField(destination, payload, "decision", "decision", "action");
                AddRawField(destination, payload, "changes", "changes", "patch", "files", "paths");
                break;

            case "todo_list":
                AddStringField(destination, payload, "operation", "operation", "action");
                AddRawField(destination, payload, "items", "items", "todos");
                break;
            default:
                break;
        }
    }

    public static void AddStringField(Dictionary<string, object?> destination, JsonElement payload, string targetName, params string[] sourceCandidates)
    {
        var names = sourceCandidates.Length == 0 ? [targetName] : sourceCandidates;
        foreach (var candidate in names)
        {
            if (TryGetProperty(payload, candidate, out var value) && value.ValueKind == JsonValueKind.String)
            {
                destination[targetName] = value.GetString();
                return;
            }
        }
    }

    public static void AddIntField(Dictionary<string, object?> destination, JsonElement payload, string targetName, params string[] sourceCandidates)
    {
        var names = sourceCandidates.Length == 0 ? [targetName] : sourceCandidates;
        foreach (var candidate in names)
        {
            if (!TryGetProperty(payload, candidate, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            {
                destination[targetName] = intValue;
                return;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out intValue))
            {
                destination[targetName] = intValue;
                return;
            }
        }
    }

    public static void AddRawField(Dictionary<string, object?> destination, JsonElement payload, string targetName, params string[] sourceCandidates)
    {
        var names = sourceCandidates.Length == 0 ? [targetName] : sourceCandidates;
        foreach (var candidate in names)
        {
            if (TryGetProperty(payload, candidate, out var value))
            {
                destination[targetName] = value;
                return;
            }
        }
    }

    public static JsonElement CreateEmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }

    public static string Truncate(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= 2_000 ? value : value[..2_000];
    }
}
