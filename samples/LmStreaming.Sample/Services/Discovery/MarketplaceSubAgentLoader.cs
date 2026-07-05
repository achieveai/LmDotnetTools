using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services.Discovery;

/// <summary>
/// Bridges the gateway's marketplace catalog (<c>GET /api/v1/marketplaces/preview</c> — the same
/// source the UI's marketplace browser shows) into spawnable <see cref="SubAgentTemplate"/>s, so an
/// agent the user can SEE in the workspace's enabled marketplaces is also a <c>subagent_type</c> the
/// <c>Agent</c> tool can spawn.
/// </summary>
/// <remarks>
/// <para>
/// WHY THIS EXISTS: workspace file-discovery (<see cref="WorkspaceSubAgentLoader"/>,
/// <c>/api/v1/sandboxes/{id}/discovered</c> ⇒ <c>kind == "subagent"</c>) only surfaces agent
/// markdown files physically present in the workspace host directory. Marketplace plugin agents are
/// NOT copied into the workspace — selecting a marketplace only enables it server-side on the
/// gateway — so they never appear in that discovery list and were browsable-but-not-spawnable. This
/// loader closes that gap by mapping each <see cref="CatalogAgent"/> the UI lists into a template.
/// </para>
/// <para>
/// PRECEDENCE: marketplace templates only ever FILL GAPS. A built-in template and a real
/// workspace-discovered file (which carries the agent's full markdown body) both win over a catalog
/// entry of the same key — see <see cref="MergeFillGaps"/>. The catalog preview does not return the
/// agent's instruction body (it lives in the plugin directory on the gateway side, not under the
/// workspace host path), so a catalog-derived template carries a best-effort system prompt built
/// from the agent's name/plugin/description. That is enough to make it selectable and usefully
/// scoped; a workspace file with the real body always takes priority when one exists.
/// </para>
/// <para>
/// Best-effort by design: a gateway that is offline or returns an error surfaces as an empty
/// dictionary after logging, exactly like <see cref="WorkspaceSubAgentLoader"/>, so sub-agent
/// orchestration degrades to the built-in catalog instead of failing agent creation.
/// </para>
/// </remarks>
public sealed class MarketplaceSubAgentLoader
{
    private readonly IMarketplaceCatalogClient _catalogClient;
    private readonly ILogger<MarketplaceSubAgentLoader> _logger;

    public MarketplaceSubAgentLoader(
        IMarketplaceCatalogClient catalogClient,
        ILogger<MarketplaceSubAgentLoader> logger)
    {
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Fetches the marketplace catalog for <paramref name="marketplaces"/> (the workspace's enabled
    /// aliases; null/empty lets the gateway apply its default set — matching the UI browser) and maps
    /// every contributed agent to a template. Returns an empty dictionary when the gateway is
    /// unavailable or the catalog is empty.
    /// </summary>
    /// <param name="marketplaces">Enabled marketplace aliases for this workspace, or null for the gateway default.</param>
    /// <param name="agentFactory">Provider-agent factory reused by every mapped template.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyDictionary<string, SubAgentTemplate>> LoadAsync(
        IReadOnlyList<string>? marketplaces,
        Func<IStreamingAgent> agentFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentFactory);

        MarketplaceCatalog catalog;
        try
        {
            catalog = await _catalogClient.GetCatalogAsync(marketplaces, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (MarketplaceCatalogUnavailableException ex)
        {
            _logger.LogWarning(
                ex,
                "Marketplace catalog unavailable; continuing without marketplace sub-agents.");
            return new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // Defensive backstop matching WorkspaceSubAgentLoader: the catalog client should only
            // surface MarketplaceCatalogUnavailableException, but an unexpected failure (e.g. an
            // ObjectDisposedException during shutdown, or a non-cancellation HttpRequestException)
            // must NOT abort agent creation — enrichment is best-effort.
            _logger.LogWarning(
                ex,
                "Unexpected error fetching marketplace catalog; continuing without marketplace sub-agents.");
            return new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);
        }

        return MapCatalog(catalog, agentFactory, _logger);
    }

    /// <summary>
    /// Flattens every plugin agent in <paramref name="catalog"/> into templates keyed by the agent's
    /// name (the spawnable <c>subagent_type</c>). Marketplaces that failed to load (non-null
    /// <see cref="CatalogMarketplace.Error"/>) contribute nothing. Agents with a blank name are
    /// skipped; duplicate names keep the FIRST occurrence so the surface is stable. Internal-static
    /// so unit tests can pin the mapping without a gateway.
    /// </summary>
    internal static IReadOnlyDictionary<string, SubAgentTemplate> MapCatalog(
        MarketplaceCatalog catalog,
        Func<IStreamingAgent> agentFactory,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(agentFactory);

        var result = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);

        foreach (var marketplace in catalog.Marketplaces ?? [])
        {
            foreach (var plugin in marketplace.Plugins ?? [])
            {
                foreach (var agent in plugin.Agents ?? [])
                {
                    if (string.IsNullOrWhiteSpace(agent.Name))
                    {
                        continue;
                    }

                    var key = agent.Name.Trim();
                    if (!result.TryAdd(key, MapToTemplate(agent, agentFactory)))
                    {
                        logger?.LogInformation(
                            "Marketplace sub-agent {Name} appears in more than one plugin/marketplace; keeping the first occurrence.",
                            key);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Maps one <see cref="CatalogAgent"/> to a template. <c>description</c> seeds both
    /// <see cref="SubAgentTemplate.Description"/> and <see cref="SubAgentTemplate.WhenToUse"/> (the
    /// catalog has no separate when-to-use field), mirroring
    /// <see cref="AchieveAi.LmDotnetTools.LmSampleShared.Discovery.SubAgentTemplateMapper.Map"/>. The
    /// system prompt is best-effort because the preview never returns the agent's instruction body.
    /// </summary>
    internal static SubAgentTemplate MapToTemplate(
        CatalogAgent agent,
        Func<IStreamingAgent> agentFactory)
    {
        var name = agent.Name.Trim();
        var description = string.IsNullOrWhiteSpace(agent.Description) ? null : agent.Description.Trim();

        return new SubAgentTemplate
        {
            Name = name,
            Description = description,
            WhenToUse = description,
            SystemPrompt = BuildSystemPrompt(name, agent.Plugin, agent.Marketplace, description),
            AgentFactory = agentFactory,
            EnabledTools = null,
            MaxTurnsPerRun = WorkspaceSubAgentLoader.DefaultMaxTurnsPerRun,
        };
    }

    /// <summary>
    /// Builds the best-effort persona prompt for a catalog-derived agent. The plugin/marketplace
    /// provenance grounds the agent; the description (when present) is the only behavioural hint the
    /// preview exposes.
    /// </summary>
    private static string BuildSystemPrompt(string name, string? plugin, string? marketplace, string? description)
    {
        var origin = !string.IsNullOrWhiteSpace(plugin)
            ? !string.IsNullOrWhiteSpace(marketplace)
                ? $" contributed by the {plugin.Trim()} plugin (from the {marketplace.Trim()} marketplace)"
                : $" contributed by the {plugin.Trim()} plugin"
            : string.Empty;

        var role = string.IsNullOrWhiteSpace(description)
            ? string.Empty
            : $" {description}";

        return $"You are the \"{name}\" sub-agent{origin}.{role} "
            + "Complete the delegated task end to end using the tools available to you, then return a "
            + "concise, self-contained final answer — the parent only sees your final message.";
    }

    /// <summary>
    /// Adds <paramref name="catalog"/> templates into <paramref name="existing"/> only where the key
    /// is not already present: built-in templates AND real workspace-discovered files both keep their
    /// place (they are merged first), so a catalog stub can never shadow a richer template. Collisions
    /// are logged at Information — they are expected (e.g. a workspace file overriding its marketplace
    /// origin), not errors.
    /// </summary>
    internal static void MergeFillGaps(
        IDictionary<string, SubAgentTemplate> existing,
        IReadOnlyDictionary<string, SubAgentTemplate> catalog,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var (key, template) in catalog)
        {
            if (!existing.TryAdd(key, template))
            {
                logger.LogInformation(
                    "Marketplace sub-agent {Name} is already provided by a built-in or workspace file; keeping the existing template.",
                    key);
            }
        }
    }
}
