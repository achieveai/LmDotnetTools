using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <remarks>
/// Property-bag routes (gateway ADR 0042, <c>api/properties.rs</c>). Both are ownership-gated (a foreign or
/// unknown session is a uniform <see cref="SandboxErrorKind.NotFound"/>) and work on a dormant session. The
/// <c>400</c> rejections (<c>invalid_properties</c>) are <see cref="SandboxErrorKind.Protocol"/> and a change to a
/// partition key (<c>409 partition_property_immutable</c>) is <see cref="SandboxErrorKind.Conflict"/>; both carry the
/// offending keys on <see cref="SandboxException.InvalidKeys"/>.
/// </remarks>
public sealed partial class SandboxClient
{
    /// <summary>Reads a sandbox's property bag and the partition keys it was created under.</summary>
    public async Task<SandboxProperties> GetPropertiesAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        const string operation = "reading sandbox properties";
        using var response = await SendDirectAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/properties",
                content: null,
                sessionId,
                ct
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await MapDirectErrorAsync(response, operation, sessionId, ct).ConfigureAwait(false);
        }

        return await ReadPropertiesResponseOrThrowAsync(response, operation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies a flat set of property changes: a non-null value sets the key and a <c>null</c> value unsets it;
    /// keys not mentioned are untouched. Returns the resulting bag. A change that would alter or unset a
    /// partition key fails the whole request (<c>409 partition_property_immutable</c>); sending a partition key
    /// with its current value is allowed. Built as a raw <see cref="JsonObject"/> for the same reason as
    /// <see cref="PatchEnvAsync"/>: the REST serializer would drop the <c>null</c> entries that mean "unset".
    /// </summary>
    public async Task<SandboxProperties> UpdatePropertiesAsync(
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

        const string operation = "updating sandbox properties";
        using var response = await SendDirectAsync(
                HttpMethod.Patch,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/properties",
                content,
                sessionId,
                ct
            )
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw await MapDirectErrorAsync(response, operation, sessionId, ct).ConfigureAwait(false);
        }

        return await ReadPropertiesResponseOrThrowAsync(response, operation, ct).ConfigureAwait(false);
    }

    private static async Task<SandboxProperties> ReadPropertiesResponseOrThrowAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct
    )
    {
        var statusCode = (int)response.StatusCode;
        SandboxPropertiesResponseDto? payload;
        try
        {
            payload = await response
                .Content.ReadFromJsonAsync<SandboxPropertiesResponseDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            // InvalidOperationException: an unknown Content-Type charset, as in ReadEnvResponseOrThrowAsync.
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed response for {operation}.",
                statusCode,
                ex
            );
        }

        // The gateway always sends `properties` ({} when empty); a missing bag is a protocol defect, not "no
        // properties", because a caller would then believe it had cleared keys it never saw.
        if (payload?.Properties is null)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned no 'properties' for {operation}.",
                statusCode
            );
        }

        return new SandboxProperties(
            new Dictionary<string, string>(payload.Properties, StringComparer.Ordinal),
            SelectNonNullOrThrow(payload.Partition, static key => key, operation, statusCode)
        );
    }
}
