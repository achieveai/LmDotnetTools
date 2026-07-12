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
        // runtime — a semantically-invalid 2xx body can still supply `null` for one. The
        // SandboxMarketplace* model constructors validate those fields and throw ArgumentException,
        // which must surface as a SandboxException(Protocol) like every other malformed-response
        // case, never as a raw ArgumentException escaping this SDK's exception contract.
        try
        {
            return new SandboxMarketplaceCatalog(payload.Selected, payload.Marketplaces is null ? null : [.. payload.Marketplaces.Select(ToEntry)]);
        }
        catch (ArgumentException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                "Sandbox gateway returned a marketplace catalog with a missing or invalid required field.",
                (int)response.StatusCode,
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
        // `null` from a semantically-invalid 2xx body — SandboxDiscoveredItem's constructor
        // validation must map to SandboxException(Protocol), not leak a raw ArgumentException.
        try
        {
            return
            [
                .. payload.Items.Select(item => new SandboxDiscoveredItem(
                    item.Kind,
                    item.Name,
                    item.Description,
                    item.Path,
                    item.Content,
                    item.QualifiedName
                )),
            ];
        }
        catch (ArgumentException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a discovered item with a missing or invalid required field for session '{sessionId}'.",
                (int)response.StatusCode,
                ex
            );
        }
    }

    private static SandboxMarketplaceEntry ToEntry(MarketplaceEntryDto dto) =>
        new(dto.Alias, dto.Error, dto.Plugins is null ? null : [.. dto.Plugins.Select(ToPlugin)]);

    private static SandboxMarketplacePlugin ToPlugin(MarketplacePluginDto dto) =>
        new(
            dto.Name,
            dto.Version,
            dto.Description,
            dto.Skills is null ? null : [.. dto.Skills.Select(ToSkill)],
            dto.Agents is null ? null : [.. dto.Agents.Select(ToAgent)]
        );

    private static SandboxMarketplaceSkill ToSkill(MarketplaceSkillDto dto) => new(dto.Name, dto.Description, dto.Plugin, dto.Marketplace, dto.Path);

    private static SandboxMarketplaceAgent ToAgent(MarketplaceAgentDto dto) => new(dto.Name, dto.Description, dto.Plugin, dto.Marketplace, dto.Path);
}
