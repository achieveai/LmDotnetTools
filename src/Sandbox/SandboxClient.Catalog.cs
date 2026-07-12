using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Previews the marketplace catalog — a read-only browse of plugins/skills/agents that requires
    /// no sandbox session. V1: this is a snapshot preview, not proactive discovery or crawling.
    /// </summary>
    /// <param name="marketplaces">
    /// Optional subset of marketplace aliases to preview. When omitted, the gateway applies its own
    /// default set.
    /// </param>
    /// <param name="ct">Cancellation token observed by the HTTP call.</param>
    public async Task<SandboxMarketplaceCatalog> PreviewMarketplacesAsync(
        IReadOnlyList<string>? marketplaces = null,
        CancellationToken ct = default
    )
    {
        var relativeUri = "api/v1/marketplaces/preview";
        if (marketplaces is { Count: > 0 })
        {
            relativeUri += $"?marketplaces={Uri.EscapeDataString(string.Join(',', marketplaces))}";
        }

        using var response = await SendRestAsync(HttpMethod.Get, relativeUri, body: null, sessionId: null, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapErrorResponse(response, "marketplace preview");
        }

        MarketplaceCatalogDto? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<MarketplaceCatalogDto>(SandboxJson.RestOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox gateway returned a malformed marketplace catalog.",
                (int)response.StatusCode,
                ex
            );
        }

        if (payload is null)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox gateway returned a success status but no marketplace catalog body.",
                (int)response.StatusCode
            );
        }

        // The wire DTOs (see SandboxWireDtos.cs) model required fields (alias/name/...) as
        // non-nullable `string`, but System.Text.Json deserialization does not enforce that at
        // runtime — a semantically-invalid 2xx body can still supply `null` for one, or a `null`
        // collection element. The SandboxMarketplace* model constructors validate required fields and
        // throw ArgumentException, and SelectNonNullOrThrow rejects null elements as
        // SandboxException(Protocol); both must surface as a SandboxException(Protocol) like every
        // other malformed-response case, never as a raw ArgumentException/NullReferenceException
        // escaping this SDK's exception contract.
        var statusCode = (int)response.StatusCode;
        const string operation = "marketplace preview";
        try
        {
            var selected = SelectNonNullOrThrow(payload.Selected, static alias => alias, operation, statusCode);
            var marketplaceEntries = SelectNonNullOrThrow(payload.Marketplaces, dto => ToEntry(dto, operation, statusCode), operation, statusCode);
            return new SandboxMarketplaceCatalog(selected, marketplaceEntries);
        }
        catch (ArgumentException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox gateway returned a marketplace catalog with a missing or invalid required field.",
                statusCode,
                ex
            );
        }
    }

    /// <summary>
    /// Lists items the gateway's background discovery sweep has found for <paramref name="sessionId"/>'s
    /// workspace, over the existing session-discovery REST endpoint. V1: this is a narrow read of
    /// already-discovered items — it does not add proactive discovery or crawling.
    /// </summary>
    public async Task<IReadOnlyList<SandboxDiscoveredItem>> ListDiscoveredAsync(string sessionId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var response = await SendRestAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/discovered",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw MapErrorResponse(response, $"listing discovered items for session '{sessionId}'");
        }

        DiscoveredItemsResponseDto? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<DiscoveredItemsResponseDto>(SandboxJson.RestOptions, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed discovered-items response for session '{sessionId}'.",
                (int)response.StatusCode,
                ex
            );
        }

        if (payload?.Items is null)
        {
            return [];
        }

        // Same rationale as PreviewMarketplacesAsync above: DiscoveredItemDto's required fields
        // (kind/name/path) are non-nullable `string` on the wire type but can still deserialize as
        // `null` from a semantically-invalid 2xx body, and the collection can carry a `null` element —
        // SandboxDiscoveredItem's constructor validation and SelectNonNullOrThrow's null-element
        // rejection must both map to SandboxException(Protocol), not leak a raw
        // ArgumentException/NullReferenceException.
        var statusCode = (int)response.StatusCode;
        var operation = $"listing discovered items for session '{sessionId}'";
        try
        {
            return SelectNonNullOrThrow(
                payload.Items,
                item => new SandboxDiscoveredItem(item.Kind, item.Name, item.Description, item.Path, item.Content, item.QualifiedName),
                operation,
                statusCode
            );
        }
        catch (ArgumentException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a discovered item with a missing or invalid required field for session '{sessionId}'.",
                statusCode,
                ex
            );
        }
    }

    private static SandboxMarketplaceEntry ToEntry(MarketplaceEntryDto dto, string operation, int statusCode) =>
        new(dto.Alias, dto.Error, SelectNonNullOrThrow(dto.Plugins, plugin => ToPlugin(plugin, operation, statusCode), operation, statusCode));

    private static SandboxMarketplacePlugin ToPlugin(MarketplacePluginDto dto, string operation, int statusCode) =>
        new(
            dto.Name,
            dto.Version,
            dto.Description,
            SelectNonNullOrThrow(dto.Skills, ToSkill, operation, statusCode),
            SelectNonNullOrThrow(dto.Agents, ToAgent, operation, statusCode)
        );

    private static SandboxMarketplaceSkill ToSkill(MarketplaceSkillDto dto) => new(dto.Name, dto.Description, dto.Plugin, dto.Marketplace, dto.Path);

    private static SandboxMarketplaceAgent ToAgent(MarketplaceAgentDto dto) => new(dto.Name, dto.Description, dto.Plugin, dto.Marketplace, dto.Path);
}
