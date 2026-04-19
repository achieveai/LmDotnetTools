using System.Text.Json;

namespace AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;

/// <summary>
/// Static helpers for parsing and validating Copilot ACP JSON event payloads.
/// The Copilot ACP protocol uses camelCase naming on the wire.
/// </summary>
internal static class CopilotEventParser
{
    public static string? ExtractSessionId(JsonElement? root)
    {
        if (!root.HasValue || root.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return root.Value.TryGetProperty("sessionId", out var sessionIdProp)
            && sessionIdProp.ValueKind == JsonValueKind.String
            ? sessionIdProp.GetString()
            : root.Value.TryGetProperty("session", out var sessionProp)
            && sessionProp.ValueKind == JsonValueKind.Object
            && sessionProp.TryGetProperty("id", out var idProp)
            && idProp.ValueKind == JsonValueKind.String
            ? idProp.GetString()
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

    public static string? ExtractSessionUpdateKind(JsonElement? parameters)
    {
        if (!parameters.HasValue || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (parameters.Value.TryGetProperty("update", out var updateProp)
            && updateProp.ValueKind == JsonValueKind.Object
            && updateProp.TryGetProperty("sessionUpdate", out var kindProp)
            && kindProp.ValueKind == JsonValueKind.String)
        {
            return kindProp.GetString();
        }

        return parameters.Value.TryGetProperty("sessionUpdate", out var directKindProp)
            && directKindProp.ValueKind == JsonValueKind.String
            ? directKindProp.GetString()
            : null;
    }

    public static JsonElement? ExtractSessionUpdateElement(JsonElement? parameters)
    {
        if (!parameters.HasValue || parameters.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return parameters.Value.TryGetProperty("update", out var updateProp)
            && updateProp.ValueKind == JsonValueKind.Object
            ? updateProp.Clone()
            : parameters.Value.Clone();
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
