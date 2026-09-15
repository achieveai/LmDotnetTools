using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Reads a session's current per-sandbox environment variables via the gateway's session-scoped
    /// direct API (gateway PR #183 / v0.1.11). A <c>null</c> <c>env</c> field (no variables set) is
    /// reported as an empty map, never <c>null</c>.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> GetEnvAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        const string operation = "reading sandbox env";
        using var response = await SendDirectAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/env",
                content: null,
                sessionId,
                ct
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await MapDirectErrorAsync(response, operation, sessionId, ct).ConfigureAwait(false);
        }

        return await ReadEnvResponseOrThrowAsync(response, operation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a flat set of env changes to a session: a non-null value sets/overrides the key, and a
    /// <c>null</c> value UNSETS it; a key not present in <paramref name="changes"/> is left untouched.
    /// Returns the resulting full environment map. Built as a raw <see cref="JsonObject"/> rather than
    /// through <see cref="SandboxJson.RestOptions"/> because that serializer's
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull"/> would drop
    /// exactly the <c>null</c> entries that carry the "unset this key" meaning.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> PatchEnvAsync(
        string sessionId,
        IReadOnlyDictionary<string, string?> changes,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(changes);

        var body = new JsonObject();
        foreach (var (key, value) in changes)
        {
            body[key] = value is null ? null : JsonValue.Create(value);
        }

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        const string operation = "patching sandbox env";
        using var response = await SendDirectAsync(
                HttpMethod.Patch,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/env",
                content,
                sessionId,
                ct
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await MapDirectErrorAsync(response, operation, sessionId, ct).ConfigureAwait(false);
        }

        return await ReadEnvResponseOrThrowAsync(response, operation, ct).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ReadEnvResponseOrThrowAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct
    )
    {
        SandboxEnvResponseDto? payload;
        try
        {
            payload = await response
                .Content.ReadFromJsonAsync<SandboxEnvResponseDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed response for {operation}.",
                (int)response.StatusCode,
                ex
            );
        }

        return payload?.Env is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(payload.Env, StringComparer.Ordinal);
    }
}
