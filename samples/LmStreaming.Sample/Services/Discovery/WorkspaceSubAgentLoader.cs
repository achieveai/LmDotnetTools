using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmSampleShared.Discovery;

namespace LmStreaming.Sample.Services.Discovery;

/// <summary>
/// Loads workspace-discovered sub-agent templates for a sandbox session: asks the gateway what
/// it has discovered, then for each <c>kind == "subagent"</c> item reads the markdown file through
/// the gateway's files API and maps it to a <see cref="SubAgentTemplate"/>.
/// </summary>
/// <remarks>
/// <para>
/// Per-file errors (missing file, parse failure, traversal attempt, I/O failure) are logged and
/// skipped; they never abort the whole batch. A complete gateway failure also returns an empty
/// dictionary after logging — discovery is a non-essential enrichment of the built-in catalog.
/// </para>
/// <para>
/// Collision policy: this loader returns ONLY the discovered templates. The caller (Program.cs)
/// is responsible for merging into its own template catalog under built-in-wins semantics.
/// </para>
/// </remarks>
public sealed class WorkspaceSubAgentLoader
{
    private const string SubAgentKind = "subagent";

    /// <summary>
    /// Per-spawn turn cap shared by every sample sub-agent template (built-in and discovered).
    /// Centralised so the call sites stay aligned and so a future bump is one edit, not several
    /// drifting literals. Set above <see cref="SubAgentTemplate"/>'s built-in default of 50: the
    /// sample's real workloads (PR-context gathering, review lenses) routinely need 30+ tool calls,
    /// and when a run hits the cap the loop just stops without a synthesizing final turn — so too
    /// tight a budget silently truncates the sub-agent with no text output at all.
    /// </summary>
    internal const int DefaultMaxTurnsPerRun = 75;

    private readonly SandboxSessionRegistry _registry;
    private readonly ILogger<WorkspaceSubAgentLoader> _logger;
    private readonly SubAgentModelResolver? _modelResolver;

    public WorkspaceSubAgentLoader(SandboxSessionRegistry registry, ILogger<WorkspaceSubAgentLoader> logger)
        : this(registry, logger, null) { }

    internal WorkspaceSubAgentLoader(
        SandboxSessionRegistry registry,
        ILogger<WorkspaceSubAgentLoader> logger,
        SubAgentModelResolver? modelResolver
    )
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _logger = logger;
        _modelResolver = modelResolver;
    }

    /// <summary>
    /// Discovers and loads sub-agent templates for <paramref name="session"/>. Returns an empty
    /// dictionary when nothing is discovered or when the gateway lookup fails.
    /// </summary>
    /// <param name="session">The sandbox session whose workspace holds the files.</param>
    /// <param name="agentFactory">Provider-agent factory reused by every discovered template.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyDictionary<string, SubAgentTemplate>> LoadAsync(
        SandboxSession session,
        Func<IStreamingAgent> agentFactory,
        CancellationToken ct = default
    )
    {
        return await LoadWithCharacteristicsAsync(session, agentFactory, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Discovers templates and attaches the conversation's characteristics-aware factory.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, SubAgentTemplate>> LoadWithCharacteristicsAsync(
        SandboxSession session,
        Func<IStreamingAgent> agentFactory,
        Func<SubAgentCharacteristics, SubAgentProviderAgent>? characteristicsAgentFactory,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(agentFactory);

        IReadOnlyList<SandboxSessionRegistry.DiscoveredItem> items;
        try
        {
            items = await _registry.ListDiscoveredAsync(session.SessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to list discovered items for session {SessionId}; continuing without workspace sub-agents",
                session.SessionId
            );
            return new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);
        }

        if (items.Count == 0 || string.IsNullOrWhiteSpace(session.HostPath))
        {
            return new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var loaded = await LoadOneWithCharacteristicsAsync(
                    session,
                    item,
                    agentFactory,
                    characteristicsAgentFactory,
                    ct
                )
                .ConfigureAwait(false);
            if (loaded is null || string.IsNullOrWhiteSpace(loaded.Name))
            {
                continue;
            }

            var key = string.IsNullOrWhiteSpace(item.QualifiedName) ? loaded.Name : item.QualifiedName;
            if (!result.TryAdd(key, loaded))
            {
                _logger.LogWarning(
                    "Discovered sub-agent {Key} collides with an earlier discovery; keeping the first occurrence",
                    key
                );
            }
        }

        return result;
    }

    /// <summary>
    /// Loads a single discovered item into a <see cref="SubAgentTemplate"/>, or returns null when
    /// the item should be skipped (wrong kind, traversal attempt, missing/unreadable file,
    /// malformed markdown). Cancellation propagates; all other failures are logged and become a
    /// null result so the caller (batch loader OR the
    /// <see cref="LmStreaming.Sample.Controllers.ContextDiscoveryController"/> webhook handler)
    /// can treat them uniformly.
    /// </summary>
    /// <remarks>
    /// Mid-session activation flow (issue #77): the context-discovery webhook fires once per newly
    /// discovered item; for <c>kind == "subagent"</c> it calls this method and then registers the
    /// result with the session's <c>MutableSubAgentTemplateSource</c>. Sharing this path with
    /// <see cref="LoadAsync"/> keeps the boot-time scan and the live activation on a single
    /// codepath so security/traversal guards and frontmatter rules cannot drift apart.
    /// </remarks>
    public async Task<SubAgentTemplate?> LoadOneAsync(
        SandboxSession session,
        SandboxSessionRegistry.DiscoveredItem item,
        Func<IStreamingAgent> agentFactory,
        CancellationToken ct = default
    )
    {
        return await LoadOneWithCharacteristicsAsync(session, item, agentFactory, null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads one template and attaches the conversation's characteristics-aware factory.
    /// </summary>
    public async Task<SubAgentTemplate?> LoadOneWithCharacteristicsAsync(
        SandboxSession session,
        SandboxSessionRegistry.DiscoveredItem item,
        Func<IStreamingAgent> agentFactory,
        Func<SubAgentCharacteristics, SubAgentProviderAgent>? characteristicsAgentFactory,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(agentFactory);

        if (!string.Equals(item.Kind, SubAgentKind, StringComparison.Ordinal))
        {
            return null;
        }

        // Content-first: marketplace sub-agents arrive with their full markdown body inline (no workspace
        // file exists at a /marketplaces/... path). Parse it directly and skip the file read. The file-read
        // path below reads a workspace .claude/agents/*.md, which the gateway names by its path only.
        if (!string.IsNullOrWhiteSpace(item.Content))
        {
            var stemFromName = string.IsNullOrWhiteSpace(item.QualifiedName) ? item.Name : item.QualifiedName;
            var parsedInline = SubAgentMarkdownParser.Parse(item.Content, stemFromName);
            if (parsedInline is null)
            {
                _logger.LogWarning(
                    "Skipping discovered sub-agent {Name}: inline content had no valid frontmatter or empty body",
                    item.Name
                );
                return null;
            }

            return MapParsed(parsedInline, agentFactory, characteristicsAgentFactory) with
            {
                Marketplace = MarketplaceOf(item.Path),
            };
        }

        if (string.IsNullOrWhiteSpace(session.HostPath))
        {
            _logger.LogWarning(
                "Skipping discovered sub-agent {Name}: session {SessionId} has no HostPath",
                item.Name,
                session.SessionId
            );
            return null;
        }

        // The file is in the sandbox, not on this host: on a Docker-backed gateway HostPath is the container path
        // "/workspace". So read it through the gateway's files API, as the CLAUDE.md seed does. The SDK refuses a path
        // that leaves the workspace, and the gateway keeps symlinks inside it.
        string? markdown;
        try
        {
            markdown = await _registry.ReadWorkspaceFileAsync(session.SessionId, item.Path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping discovered sub-agent {Name}: path '{Path}' cannot be read in session {SessionId}",
                item.Name,
                item.Path,
                session.SessionId
            );
            return null;
        }

        if (markdown is null)
        {
            _logger.LogWarning(
                "Skipping discovered sub-agent {Name}: the gateway returned no file at '{Path}' in session {SessionId}",
                item.Name,
                item.Path,
                session.SessionId
            );
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(item.Path);
        var parsed = SubAgentMarkdownParser.Parse(markdown, stem);
        if (parsed is null)
        {
            _logger.LogWarning(
                "Skipping discovered sub-agent {Name}: markdown at '{Path}' has no valid frontmatter or empty body",
                item.Name,
                item.Path
            );
            return null;
        }

        return MapParsed(parsed, agentFactory, characteristicsAgentFactory);
    }

    private SubAgentTemplate MapParsed(
        ParsedSubAgent parsed,
        Func<IStreamingAgent> agentFactory,
        Func<SubAgentCharacteristics, SubAgentProviderAgent>? characteristicsAgentFactory
    )
    {
        foreach (var diagnostic in parsed.Diagnostics)
        {
            _logger.LogWarning(
                "Discovered sub-agent {Name} frontmatter diagnostic: {Diagnostic}",
                parsed.Name,
                diagnostic
            );
        }

        var selection = _modelResolver?.ResolveWithEffort(parsed.Model, parsed.ModelIntelligence);
        var effectiveModel = selection?.ModelId;
        var hasAuthorPinnedModel =
            !string.IsNullOrWhiteSpace(parsed.Model)
            && !string.Equals(parsed.Model.Trim(), "inherit", StringComparison.OrdinalIgnoreCase);
        var isTierResolved =
            !hasAuthorPinnedModel && parsed.ModelIntelligence is not null && effectiveModel is not null;
        var effectiveParsed = effectiveModel is null
            ? parsed
            : parsed with
            {
                Model = effectiveModel,
                IsModelTierResolved = isTierResolved,
            };
        return SubAgentTemplateMapper.Map(effectiveParsed, agentFactory, DefaultMaxTurnsPerRun) with
        {
            CharacteristicsAgentFactory = characteristicsAgentFactory,
            ModelIntelligence = parsed.ModelIntelligence,
            // ResolveWithEffort does not climb, so the tier that picked the model is the authored one. A
            // candidate's own "model:effort" wins over the tier's effort.
            TierEffort = isTierResolved ? selection?.Effort : null,
        };
    }

    /// <summary>
    /// Merges <paramref name="discovered"/> into <paramref name="builtIns"/> under built-in-wins
    /// semantics: a discovered template whose key collides with an existing built-in entry is
    /// skipped and logged. This pins a trust boundary — untrusted workspace markdown must NEVER
    /// shadow a trusted hardcoded template — so any future change to the merge direction needs
    /// the merge test to be updated explicitly.
    /// </summary>
    /// <param name="builtIns">The mutable built-in catalog. Receives discovered entries on no-collision.</param>
    /// <param name="discovered">Discovered templates returned by <see cref="LoadAsync"/>.</param>
    /// <param name="logger">Logger used to record collisions. Never null in production.</param>
    internal static void MergeBuiltInWins(
        IDictionary<string, SubAgentTemplate> builtIns,
        IReadOnlyDictionary<string, SubAgentTemplate> discovered,
        Microsoft.Extensions.Logging.ILogger logger
    )
    {
        ArgumentNullException.ThrowIfNull(builtIns);
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(logger);

        foreach (var (key, template) in discovered)
        {
            if (!builtIns.TryAdd(key, template))
            {
                logger.LogWarning(
                    "Discovered sub-agent {Name} collides with a built-in template; keeping the built-in",
                    key
                );
            }
        }
    }

    /// <summary>
    /// The marketplace alias in a gateway path <c>/marketplaces/{alias}/...</c>, or null for any other path
    /// (a workspace file). The alias is taken as it is, whatever characters it holds.
    /// </summary>
    internal static string? MarketplaceOf(string? path)
    {
        const string Prefix = "/marketplaces/";
        if (path is null || !path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var end = path.IndexOf('/', Prefix.Length);
        return end > Prefix.Length ? path[Prefix.Length..end] : null;
    }
}
