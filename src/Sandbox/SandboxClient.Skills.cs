using System.Net.Http.Json;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;

namespace AchieveAi.LmDotnetTools.Sandbox;

public sealed partial class SandboxClient
{
    /// <summary>
    /// Reads one agent's skill catalog (gateway ADR 0041, <c>GET /api/v1/sandboxes/{id}/skills</c>): every skill
    /// the sandbox admits, grouped marketplace → plugin → skill, plus the discovered workspace skills, each with
    /// the agent's tier and load state.
    /// </summary>
    /// <param name="sessionId">The sandbox session.</param>
    /// <param name="agentId">The agent (ADR 0040); <c>null</c> or <c>""</c> selects the default agent.</param>
    /// <param name="tier">Keep only skills in this tier; <c>null</c> keeps all.</param>
    /// <param name="loaded">Keep only loaded (<c>true</c>) or not-loaded (<c>false</c>) skills; <c>null</c> keeps all.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// A filter drops plugins and marketplaces it leaves empty; <see cref="SandboxSkillCatalog.Counts"/> is always
    /// over the whole catalog. An unknown or foreign session is <see cref="SandboxErrorKind.NotFound"/>.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="agentId"/> is neither empty nor a valid agent id.</exception>
    public async Task<SandboxSkillCatalog> GetSkillCatalogAsync(
        string sessionId,
        string? agentId = null,
        SandboxSkillTier? tier = null,
        bool? loaded = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var agent = NormalizeSkillAgentId(agentId);

        var query = new List<string>();
        if (agent.Length > 0)
        {
            query.Add($"agent_id={Uri.EscapeDataString(agent)}");
        }

        if (tier is { } t)
        {
            query.Add($"tier={ToWireTier(t)}");
        }

        if (loaded is { } l)
        {
            query.Add(l ? "loaded=true" : "loaded=false");
        }

        var operation = $"reading the skill catalog of agent '{agent}' on session '{sessionId}'";
        using var response = await SendRestAsync(
                HttpMethod.Get,
                SkillsPath(sessionId) + (query.Count > 0 ? "?" + string.Join('&', query) : string.Empty),
                body: null,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        return await ReadSkillCatalogOrThrowAsync(response, agent, isPatch: false, operation, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves skills between tiers for one agent (gateway ADR 0041, <c>PATCH /api/v1/sandboxes/{id}/skills</c>) and
    /// returns the agent's whole catalog afterwards, with <see cref="SandboxSkillCatalog.ToolsListChanged"/> set.
    /// </summary>
    /// <param name="sessionId">The sandbox session.</param>
    /// <param name="agentId">The agent (ADR 0040); <c>null</c> or <c>""</c> selects the default agent.</param>
    /// <param name="set">
    /// Skill ref → tier. A ref is a name the <c>Skill</c> tool accepts (marketplace skills plugin-qualified) or a
    /// skill directory path (<see cref="SandboxSkill.Path"/>, always unique).
    /// </param>
    /// <param name="reset">Drop every override of the agent first (back to the sandbox defaults), then apply <paramref name="set"/>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <remarks>
    /// All-or-nothing, and loads are never touched. Works before the agent's first tool call, so a harness can seed
    /// a sub-agent's skills before building it. The gateway sends no MCP <c>list_changed</c> notification: re-run
    /// <c>tools/list</c> for the agent afterwards to see its new <c>Skill</c> description.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="agentId"/> is neither empty nor a valid agent id, or a ref in <paramref name="set"/> is blank.
    /// </exception>
    /// <exception cref="SandboxException">
    /// <see cref="SandboxErrorKind.SkillRejected"/> for <c>skill_not_found</c>, <c>skill_ambiguous</c> (with
    /// <see cref="SandboxException.SkillCandidates"/>) or <c>invalid_skills</c>; nothing was applied.
    /// </exception>
    public async Task<SandboxSkillCatalog> PatchSkillTiersAsync(
        string sessionId,
        string? agentId,
        IReadOnlyDictionary<string, SandboxSkillTier> set,
        bool reset = false,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(set);
        var agent = NormalizeSkillAgentId(agentId);
        if (set.Keys.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Skill refs must be non-blank names or paths.", nameof(set));
        }

        var body = new PatchSkillsRequestDto(
            set.ToDictionary(pair => pair.Key, pair => ToWireTier(pair.Value), StringComparer.Ordinal),
            reset
        );
        var operation = $"patching the skill tiers of agent '{agent}' on session '{sessionId}'";
        using var response = await SendRestAsync(
                HttpMethod.Patch,
                SkillsPath(sessionId) + (agent.Length > 0 ? $"?agent_id={Uri.EscapeDataString(agent)}" : string.Empty),
                body,
                sessionId: null,
                ct
            )
            .ConfigureAwait(false);
        return await ReadSkillCatalogOrThrowAsync(response, agent, isPatch: true, operation, ct).ConfigureAwait(false);
    }

    private static string SkillsPath(string sessionId) => $"api/v1/sandboxes/{Uri.EscapeDataString(sessionId)}/skills";

    /// <summary>
    /// The skills routes take the agent as a query value where absent and <c>""</c> both mean the default agent,
    /// so — unlike the <c>X-Sbx-Agent-ID</c> header — an empty id is accepted here and simply not sent.
    /// </summary>
    private static string NormalizeSkillAgentId(string? agentId)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return string.Empty;
        }

        SandboxAgentId.ThrowIfInvalid(agentId, nameof(agentId));
        return agentId;
    }

    private async Task<SandboxSkillCatalog> ReadSkillCatalogOrThrowAsync(
        HttpResponseMessage response,
        string agent,
        bool isPatch,
        string operation,
        CancellationToken ct
    )
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, operation, ct).ConfigureAwait(false);
        }

        var statusCode = (int)response.StatusCode;
        SkillCatalogDto? payload;
        try
        {
            payload = await response
                .Content.ReadFromJsonAsync<SkillCatalogDto>(SandboxJson.RestOptions, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned a malformed skill catalog for {operation}.",
                statusCode,
                ex
            );
        }

        var catalog = ToSkillCatalog(payload, operation, statusCode);

        // A catalog for another agent means the agent selector was lost on the way (for example a renamed query
        // parameter falling back to the default agent): answering with it would silently report the wrong tiers.
        if (catalog.AgentId != agent)
        {
            throw SkillCatalogProtocol(operation, statusCode, $"agent_id '{catalog.AgentId}', expected '{agent}'");
        }

        // PATCH always answers tools_list_changed: true. Reporting false would tell the caller its cached tool list
        // is still current when the agent's Skill description may have changed.
        if (isPatch && !catalog.ToolsListChanged)
        {
            throw SkillCatalogProtocol(operation, statusCode, "no tools_list_changed: true on a PATCH response");
        }

        return catalog;
    }

    private static SandboxSkillCatalog ToSkillCatalog(SkillCatalogDto? dto, string operation, int statusCode)
    {
        if (
            dto is null
            || string.IsNullOrWhiteSpace(dto.SessionId)
            || dto.AgentId is null
            || dto.Counts
                is not {
                    Included: { } included,
                    Available: { } available,
                    Unavailable: { } unavailable,
                    Loaded: { } loaded
                }
            || dto.Marketplaces is null
            || dto.Workspace?.Skills is null
        )
        {
            throw SkillCatalogProtocol(operation, statusCode, "a missing catalog field");
        }

        var marketplaces = SelectNonNullOrThrow(
            dto.Marketplaces,
            m =>
                string.IsNullOrWhiteSpace(m.Alias) || m.Plugins is null
                    ? throw SkillCatalogProtocol(operation, statusCode, "a marketplace without alias or plugins")
                    : new SandboxSkillMarketplace(
                        m.Alias,
                        SelectNonNullOrThrow(
                            m.Plugins,
                            p => ToSkillPlugin(p, operation, statusCode),
                            operation,
                            statusCode
                        )
                    ),
            operation,
            statusCode
        );

        return new SandboxSkillCatalog(
            dto.SessionId,
            dto.AgentId,
            ParseWireTier(dto.DefaultTier, operation, statusCode),
            new SandboxSkillCounts(included, available, unavailable, loaded),
            marketplaces,
            SelectNonNullOrThrow(dto.Workspace.Skills, s => ToSkill(s, operation, statusCode), operation, statusCode),
            dto.ToolsListChanged ?? false
        );
    }

    private static SandboxSkillPlugin ToSkillPlugin(SkillPluginDto dto, string operation, int statusCode) =>
        string.IsNullOrWhiteSpace(dto.Name) || dto.Description is null || dto.Skills is null
            ? throw SkillCatalogProtocol(operation, statusCode, "a plugin without name, description or skills")
            : new SandboxSkillPlugin(
                dto.Name,
                dto.Version,
                dto.Description,
                SelectNonNullOrThrow(dto.Skills, s => ToSkill(s, operation, statusCode), operation, statusCode)
            );

    private static SandboxSkill ToSkill(SkillRowDto dto, string operation, int statusCode)
    {
        if (
            string.IsNullOrWhiteSpace(dto.Name)
            || string.IsNullOrWhiteSpace(dto.Path)
            || dto.Description is null
            || dto.Loaded is not { } isLoaded
        )
        {
            throw SkillCatalogProtocol(operation, statusCode, "a skill without name, path, description or loaded");
        }

        var origin = dto.TierOrigin switch
        {
            "override" => SandboxSkillTierOrigin.Override,
            "included_list" => SandboxSkillTierOrigin.IncludedList,
            "default" => SandboxSkillTierOrigin.Default,
            _ => throw SkillCatalogProtocol(operation, statusCode, $"an unknown tier_origin '{dto.TierOrigin}'"),
        };

        return new SandboxSkill(
            dto.Name,
            dto.Path,
            dto.Description,
            ParseWireTier(dto.Tier, operation, statusCode),
            origin,
            isLoaded,
            dto.LoadedAt,
            dto.Notified,
            dto.Shadowed ?? false
        );
    }

    private static SandboxException SkillCatalogProtocol(string operation, int statusCode, string what) =>
        new(
            SandboxErrorKind.Protocol,
            $"Sandbox gateway returned a skill catalog with {what} for {operation}.",
            statusCode
        );

    private static string ToWireTier(SandboxSkillTier tier) =>
        tier switch
        {
            SandboxSkillTier.Included => "included",
            SandboxSkillTier.Available => "available",
            SandboxSkillTier.Unavailable => "unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown skill tier."),
        };

    private static SandboxSkillTier ParseWireTier(string? tier, string operation, int statusCode) =>
        tier switch
        {
            "included" => SandboxSkillTier.Included,
            "available" => SandboxSkillTier.Available,
            "unavailable" => SandboxSkillTier.Unavailable,
            _ => throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway returned an unknown skill tier '{tier}' for {operation}.",
                statusCode
            ),
        };

    private static SkillsPolicyDto ToSkillsPolicyDto(SandboxSkillsPolicy policy) =>
        new(
            policy.DefaultTier is { } tier ? ToWireTier(tier) : null,
            policy.Included.Count > 0 ? [.. policy.Included] : null,
            policy.UnlistedHint
        );

    /// <summary>
    /// Maps the create response's echoed skills policy. The gateway always resolves a default tier, and never to
    /// <c>unavailable</c>, so either is a protocol defect rather than a value to pass on.
    /// </summary>
    private static SandboxSkillsPolicy ToSkillsPolicy(SkillsPolicyDto dto, string operation, int statusCode)
    {
        var tier = ParseWireTier(dto.DefaultTier, operation, statusCode);
        if (tier == SandboxSkillTier.Unavailable || dto.Included?.Any(string.IsNullOrWhiteSpace) == true)
        {
            throw new SandboxException(
                SandboxErrorKind.Protocol,
                $"Sandbox gateway echoed an invalid skills policy for {operation}.",
                statusCode
            );
        }

        return new SandboxSkillsPolicy(tier, dto.Included, dto.UnlistedHint);
    }
}
