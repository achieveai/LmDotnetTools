using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <remarks>
/// Skill-tier routes (gateway ADR 0041, <c>api/skills.rs</c>). Both are ownership-gated: an unknown or foreign
/// session is <see cref="SandboxErrorKind.NotFound"/>. The <c>400</c> rejections (<c>skill_not_found</c>,
/// <c>skill_ambiguous</c>, <c>invalid_skills</c>, <c>invalid_agent_id</c>) are <see cref="SandboxErrorKind.Protocol"/>
/// with <see cref="SandboxException.ErrorCode"/> naming the reason.
/// </remarks>
public sealed partial class SandboxClient
{
    /// <summary>
    /// Reads one agent's skill catalog: every skill the sandbox admits with the agent's tier and load state.
    /// </summary>
    /// <param name="sessionId">The sandbox session.</param>
    /// <param name="agentId">The agent (<see cref="SandboxAgentId"/>); <c>null</c> or empty for the default agent.</param>
    /// <param name="tier">Keep only skills in this tier, or <c>null</c> for all.</param>
    /// <param name="loaded">Keep only loaded (<c>true</c>) or not-loaded (<c>false</c>) skills, or <c>null</c> for all.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<SandboxSkillCatalog> GetSkillsAsync(
        string sessionId,
        string? agentId = null,
        SandboxSkillTier? tier = null,
        bool? loaded = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ThrowIfInvalidOptionalAgentId(agentId);

        var query = new List<string>();
        if (!string.IsNullOrEmpty(agentId))
        {
            query.Add("agent_id=" + Uri.EscapeDataString(agentId));
        }

        if (tier is { } t)
        {
            query.Add("tier=" + ToWireTier(t));
        }

        if (loaded is { } l)
        {
            query.Add(l ? "loaded=true" : "loaded=false");
        }

        var operation = $"reading skills of {DescribeAgent(agentId)} on session '{sessionId}'";
        using var response = await SendRestAsync(
                HttpMethod.Get,
                SkillsUri(sessionId, query),
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        return await ReadSkillCatalogOrThrowAsync(response, operation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves skills between tiers for one agent — the only way to change an agent's tiers after create. All or
    /// nothing; loads are never touched. Works before the agent's first tool call, so a harness can seed a
    /// sub-agent's tool-set before building it. MCP clients cache <c>tools/list</c> and the gateway sends no
    /// <c>list_changed</c> notification, so re-list tools afterwards (<see cref="SandboxSkillCatalog.ToolsListChanged"/>).
    /// </summary>
    /// <param name="sessionId">The sandbox session.</param>
    /// <param name="set">Ref → tier. A ref is a name the <c>Skill</c> tool accepts or a skill directory path.</param>
    /// <param name="reset"><c>true</c> drops every override of the agent first (back to the sandbox defaults), then applies <paramref name="set"/>.</param>
    /// <param name="agentId">The agent; <c>null</c> or empty for the default agent.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The agent's resulting catalog.</returns>
    public async Task<SandboxSkillCatalog> UpdateSkillTiersAsync(
        string sessionId,
        IReadOnlyDictionary<string, SandboxSkillTier> set,
        bool reset = false,
        string? agentId = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(set);
        ThrowIfInvalidOptionalAgentId(agentId);
        foreach (var reference in set.Keys)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(reference, nameof(set));
        }

        var body = new PatchSkillsRequestDto(
            set.ToDictionary(pair => pair.Key, pair => ToWireTier(pair.Value), StringComparer.Ordinal),
            reset
        );
        List<string> query = string.IsNullOrEmpty(agentId) ? [] : ["agent_id=" + Uri.EscapeDataString(agentId)];
        var operation = $"updating skill tiers of {DescribeAgent(agentId)} on session '{sessionId}'";
        using var response = await SendRestAsync(
                HttpMethod.Patch,
                SkillsUri(sessionId, query),
                body,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        return await ReadSkillCatalogOrThrowAsync(response, operation, ct).ConfigureAwait(false);
    }

    private static string SkillsUri(string sessionId, List<string> query) =>
        $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/skills"
        + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty);

    private static string DescribeAgent(string? agentId) =>
        string.IsNullOrEmpty(agentId) ? "the default agent" : $"agent '{agentId}'";

    private static void ThrowIfInvalidOptionalAgentId(string? agentId)
    {
        if (!string.IsNullOrEmpty(agentId))
        {
            SandboxAgentId.ThrowIfInvalid(agentId, nameof(agentId));
        }
    }

    private async Task<SandboxSkillCatalog> ReadSkillCatalogOrThrowAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct
    )
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        }

        var statusCode = (int)response.StatusCode;
        SkillCatalogDto? dto;
        try
        {
            dto = await response
                .Content.ReadFromJsonAsync<SkillCatalogDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed skill catalog for {operation}.",
                statusCode,
                ex
            );
        }

        SandboxException Malformed(string what) =>
            new(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a skill catalog {what} for {operation}.",
                statusCode
            );

        if (
            dto
                is not {
                    SessionId: { } sessionId,
                    AgentId: { } agentId,
                    Counts:
                    {
                        Included: { } included,
                        Available: { } available,
                        Unavailable: { } unavailable,
                        Loaded: { } loadedCount
                    },
                    Workspace: { } workspace,
                }
            || ParseWireTier(dto.DefaultTier) is not { } defaultTier
        )
        {
            throw Malformed("with a missing or invalid session_id, agent_id, default_tier, counts or workspace");
        }

        SandboxSkill ToSkill(SkillRowDto row)
        {
            if (
                row is not { Name: { } name, Path: { } path, Description: { } description, Loaded: { } isLoaded }
                || ParseWireTier(row.Tier) is not { } tier
                || ParseTierOrigin(row.TierOrigin) is not { } origin
            )
            {
                throw Malformed("with a skill missing its name, path, description, tier, tier_origin or loaded");
            }

            return new SandboxSkill(
                name,
                path,
                description,
                tier,
                origin,
                isLoaded,
                row.LoadedAt,
                row.Notified,
                row.Shadowed ?? false
            );
        }

        SandboxSkillPlugin ToPlugin(SkillPluginDto plugin) =>
            plugin is { Name: { } name, Description: { } description }
                ? new SandboxSkillPlugin(
                    name,
                    plugin.Version,
                    description,
                    SelectNonNullOrThrow(plugin.Skills, ToSkill, operation, statusCode)
                )
                : throw Malformed("with a plugin missing its name or description");

        SandboxSkillMarketplace ToMarketplace(SkillMarketplaceDto marketplace) =>
            marketplace.Alias is { } alias
                ? new SandboxSkillMarketplace(
                    alias,
                    SelectNonNullOrThrow(marketplace.Plugins, ToPlugin, operation, statusCode)
                )
                : throw Malformed("with a marketplace missing its alias");

        return new SandboxSkillCatalog(
            sessionId,
            agentId,
            defaultTier,
            new SandboxSkillCounts(included, available, unavailable, loadedCount),
            SelectNonNullOrThrow(dto.Marketplaces, ToMarketplace, operation, statusCode),
            SelectNonNullOrThrow(workspace.Skills, ToSkill, operation, statusCode),
            dto.ToolsListChanged ?? false
        );
    }

    private static SandboxSkillTierOrigin? ParseTierOrigin(string? origin) =>
        origin switch
        {
            "override" => SandboxSkillTierOrigin.Override,
            "included_list" => SandboxSkillTierOrigin.IncludedList,
            "default" => SandboxSkillTierOrigin.Default,
            _ => null,
        };
}
