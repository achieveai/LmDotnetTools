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
    /// default set plus every git marketplace this app registered (<see cref="RegisterAppMarketplaceAsync(string, string, string, CancellationToken)"/>).
    /// </param>
    /// <param name="ct">Cancellation token observed by the HTTP call.</param>
    public async Task<SandboxMarketplaceCatalog> PreviewMarketplacesAsync(
        IReadOnlyList<string>? marketplaces = null,
        CancellationToken ct = default
    )
    {
        // app_id makes the preview include this app's registered git marketplaces when the gateway runs with
        // AUTH_ENFORCE off; with it on, the authenticated app wins and the parameter is ignored.
        var relativeUri = $"api/v1/marketplaces/preview?{AppIdQuery()}";
        if (marketplaces is { Count: > 0 })
        {
            relativeUri += $"&marketplaces={Uri.EscapeDataString(string.Join(',', marketplaces))}";
        }

        using var response = await SendRestAsync(HttpMethod.Get, relativeUri, body: null, sessionId: null, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, "marketplace preview", ct).ConfigureAwait(false);
        }

        MarketplaceCatalogDto? payload;
        try
        {
            payload = await response
                .Content.ReadFromJsonAsync<MarketplaceCatalogDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
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
            var marketplaceEntries = SelectNonNullOrThrow(
                payload.Marketplaces,
                dto => ToEntry(dto, operation, statusCode),
                operation,
                statusCode
            );
            return new SandboxMarketplaceCatalog(selected, marketplaceEntries, payload.Capabilities?.PluginFiltering);
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
    /// <remarks>
    /// Follows the gateway's <c>next_cursor</c> across every page and returns every row the gateway could
    /// read, oldest first. A row the gateway sends but this client cannot parse fails the whole call with
    /// <see cref="SandboxErrorKind.Protocol"/>, as do a repeated cursor and more than 4096 pages.
    /// </remarks>
    public Task<IReadOnlyList<SandboxDiscoveredItem>> ListDiscoveredAsync(
        string sessionId,
        CancellationToken ct = default
    ) => ListDiscoveredAsync(sessionId, agentId: null, ct);

    /// <summary>
    /// Lists discovered items for <paramref name="sessionId"/>, narrowed to the rows one agent has been
    /// notified of (gateway ADR 0040, <c>?agent_id=</c>).
    /// </summary>
    /// <remarks>
    /// Follows the gateway's <c>next_cursor</c> across every page, sending the same <c>agent_id</c> on each,
    /// and returns every row the gateway could read, oldest first. A row the gateway sends but this client
    /// cannot parse fails the whole call with <see cref="SandboxErrorKind.Protocol"/>, as do a repeated
    /// cursor and more than 4096 pages.
    /// </remarks>
    /// <param name="sessionId">The sandbox session.</param>
    /// <param name="agentId">
    /// <c>null</c> lists every row (no filter); <c>""</c> lists the rows the default (primary) agent was
    /// notified of; any other value must be a valid <see cref="SandboxAgentId"/> and lists that agent's.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<SandboxDiscoveredItem>> ListDiscoveredAsync(
        string sessionId,
        string? agentId,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (agentId is { Length: > 0 })
        {
            SandboxAgentId.ThrowIfInvalid(agentId, nameof(agentId));
        }

        // A gateway that pages /discovered (gateway #285, commit 0ba1746; in no tagged release yet, newest
        // v0.1.22) always sends `next_cursor`, null at the end. With gateway master's skipped-row cap
        // (gateway #290, commit 1a21e82) a page can also be empty with a non-null cursor (it skipped unreadable
        // rows); at 0ba1746 an empty page always ends the walk. The loop follows the cursor either way rather
        // than stopping on an empty page. The same agent_id is sent on every page. An older gateway sends no
        // `next_cursor`, so this is one request, as before. Bounded the same two ways as the directory
        // listing: a repeated cursor and the page cap are Protocol errors. Cancellation is checked before each
        // page and after each read.
        var operation = $"listing discovered items for session '{sessionId}'";
        var agentQuery = agentId is null ? null : $"agent_id={Uri.EscapeDataString(agentId)}";
        var result = new List<SandboxDiscoveredItem>();
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var pageCount = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            if (++pageCount > MaxDiscoveredPages)
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway paginated {operation} past the {MaxDiscoveredPages}-page cap; refusing to loop unbounded."
                );
            }

            var (items, nextCursor) = await ReadDiscoveredPageAsync(sessionId, agentQuery, cursor, operation, ct)
                .ConfigureAwait(false);
            result.AddRange(items);

            cursor = string.IsNullOrEmpty(nextCursor) ? null : nextCursor;
            ct.ThrowIfCancellationRequested();
            if (cursor is not null && !seenCursors.Add(cursor))
            {
                throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    $"Sandbox gateway returned a repeated pagination cursor for {operation}; refusing to loop."
                );
            }
        } while (cursor is not null);

        return result;
    }

    /// <summary>
    /// Defensive upper bound on <c>/discovered</c> pages. The seen-cursor guard already catches a repeated
    /// cursor; this caps a gateway that emits fresh cursors forever. Generous — each gateway page is bounded
    /// by a 16 MiB byte budget, not a small row count.
    /// </summary>
    private const int MaxDiscoveredPages = 4096;

    /// <summary>Reads and maps one <c>/discovered</c> page; <c>after</c> is the previous page's cursor.</summary>
    private async Task<(List<SandboxDiscoveredItem> Items, string? NextCursor)> ReadDiscoveredPageAsync(
        string sessionId,
        string? agentQuery,
        string? after,
        string operation,
        CancellationToken ct
    )
    {
        var queryParts = new List<string>(2);
        if (agentQuery is not null)
        {
            queryParts.Add(agentQuery);
        }

        if (after is not null)
        {
            queryParts.Add($"after={Uri.EscapeDataString(after)}");
        }

        var query = queryParts.Count == 0 ? string.Empty : "?" + string.Join('&', queryParts);
        using var response = await SendRestAsync(
                HttpMethod.Get,
                $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/discovered{query}",
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        }

        DiscoveredItemsResponseDto? payload;
        try
        {
            payload = await response
                .Content.ReadFromJsonAsync<DiscoveredItemsResponseDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
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

        // A null body on a later page would otherwise end the walk silently with the rows read so far.
        // The first page keeps its existing behavior (an empty list), which predates paging.
        if (payload is null && after is not null)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an empty response for {operation} after cursor '{after}'.",
                (int)response.StatusCode
            );
        }

        if (payload?.Items is null)
        {
            return ([], payload?.NextCursor);
        }

        // Same rationale as PreviewMarketplacesAsync above: DiscoveredItemDto's required fields
        // (kind/path — "name" is genuinely optional per the gateway's DiscoveredFile contract, see
        // SandboxDiscoveredItem's remarks) are non-nullable `string` on the wire type but can still
        // deserialize as `null` from a semantically-invalid 2xx body, and the collection can carry a
        // `null` element — SandboxDiscoveredItem's constructor validation and SelectNonNullOrThrow's
        // null-element rejection must both map to SandboxException(Protocol), not leak a raw
        // ArgumentException/NullReferenceException.
        var statusCode = (int)response.StatusCode;
        try
        {
            var items = SelectNonNullOrThrow(
                payload.Items,
                item => new SandboxDiscoveredItem(
                    item.Kind,
                    item.Name,
                    item.Description,
                    item.Path,
                    item.Content,
                    item.QualifiedName
                )
                {
                    Frontmatter = item.Frontmatter?.Clone(),
                    Plugin = item.Plugin,
                    DiscoveredAt = item.DiscoveredAt,
                    Notified = item.Notified,
                },
                operation,
                statusCode
            );
            return (items, payload.NextCursor);
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
        new(
            dto.Alias,
            dto.Error,
            SelectNonNullOrThrow(dto.Plugins, plugin => ToPlugin(plugin, operation, statusCode), operation, statusCode)
        );

    private static SandboxMarketplacePlugin ToPlugin(MarketplacePluginDto dto, string operation, int statusCode) =>
        new(
            dto.Name,
            dto.Version,
            dto.Description,
            SelectNonNullOrThrow(dto.Skills, ToSkill, operation, statusCode),
            SelectNonNullOrThrow(dto.Agents, ToAgent, operation, statusCode),
            SelectNonNullOrThrow(dto.McpServers, ToMcpServer, operation, statusCode),
            dto.Selectable
        );

    private static SandboxMarketplaceSkill ToSkill(MarketplaceSkillDto dto) =>
        new(dto.Name, dto.Description, dto.Plugin, dto.Marketplace, dto.Path);

    private static SandboxMarketplaceAgent ToAgent(MarketplaceAgentDto dto) =>
        new(dto.Name, dto.Description, dto.Plugin, dto.Marketplace, dto.Path);

    private static SandboxMarketplaceMcpServer ToMcpServer(MarketplaceMcpServerDto dto) =>
        new(dto.Name, dto.Transport ?? dto.Type, dto.Url, dto.Auth?.Mode, dto.UnresolvedVariables)
        {
            RequiresOAuth = dto.RequiresOAuth,
        };
}
