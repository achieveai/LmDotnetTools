using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Services.Discovery;

/// <summary>
/// Loads workspace-discovered sub-agent templates for a sandbox session: asks the gateway what
/// it has discovered, then for each <c>kind == "subagent"</c> item reads the markdown file from
/// the workspace host directory and maps it to a <see cref="SubAgentTemplate"/>.
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
    /// Centralised so the three call sites stay aligned and so a future bump is one edit, not
    /// three drifting literals. Lower than <see cref="SubAgentTemplate"/>'s built-in default of
    /// 50 because the sample's middleware path is single-provider and each turn is full-cost.
    /// </summary>
    internal const int DefaultMaxTurnsPerRun = 25;

    private readonly SandboxSessionRegistry _registry;
    private readonly ILogger<WorkspaceSubAgentLoader> _logger;

    public WorkspaceSubAgentLoader(
        SandboxSessionRegistry registry,
        ILogger<WorkspaceSubAgentLoader> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Discovers and loads sub-agent templates for <paramref name="session"/>. Returns an empty
    /// dictionary when nothing is discovered or when the gateway lookup fails.
    /// </summary>
    /// <param name="session">The sandbox session whose <c>HostPath</c> contains the files.</param>
    /// <param name="agentFactory">Provider-agent factory reused by every discovered template.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyDictionary<string, SubAgentTemplate>> LoadAsync(
        SandboxSession session,
        Func<IStreamingAgent> agentFactory,
        CancellationToken ct = default)
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
                session.SessionId);
            return new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);
        }

        if (items.Count == 0 || string.IsNullOrWhiteSpace(session.HostPath))
        {
            return new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, SubAgentTemplate>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            var loaded = await LoadOneAsync(session, item, agentFactory, ct).ConfigureAwait(false);
            if (loaded is null || string.IsNullOrWhiteSpace(loaded.Name))
            {
                continue;
            }

            if (!result.TryAdd(loaded.Name, loaded))
            {
                _logger.LogWarning(
                    "Discovered sub-agent {Name} collides with an earlier discovery; keeping the first occurrence",
                    loaded.Name);
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
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(agentFactory);

        if (!string.Equals(item.Kind, SubAgentKind, StringComparison.Ordinal))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(session.HostPath))
        {
            _logger.LogWarning(
                "Skipping discovered sub-agent {Name}: session {SessionId} has no HostPath",
                item.Name,
                session.SessionId);
            return null;
        }

        var basePath = NormalizeBasePath(session.HostPath);
        if (!TryResolveContainedPath(basePath, item.Path, out var fullPath))
        {
            _logger.LogWarning(
                "Skipping discovered sub-agent {Name}: path '{Path}' is outside workspace '{HostPath}'",
                item.Name,
                item.Path,
                session.HostPath);
            return null;
        }

        string markdown;
        try
        {
            markdown = await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping discovered sub-agent {Name}: failed to read '{Path}'",
                item.Name,
                fullPath);
            return null;
        }

        var stem = Path.GetFileNameWithoutExtension(fullPath);
        var parsed = SubAgentMarkdownParser.Parse(markdown, stem);
        if (parsed is null)
        {
            _logger.LogWarning(
                "Skipping discovered sub-agent {Name}: markdown at '{Path}' has no valid frontmatter or empty body",
                item.Name,
                fullPath);
            return null;
        }

        return MapToTemplate(parsed, agentFactory);
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
        Microsoft.Extensions.Logging.ILogger logger)
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
                    key);
            }
        }
    }

    /// <summary>
    /// Maps a parsed markdown sub-agent into a <see cref="SubAgentTemplate"/>. Internal-static so
    /// the unit tests can pin the mapping table without needing a registry instance.
    /// </summary>
    /// <remarks>
    /// Mapping rules:
    /// <list type="bullet">
    ///   <item><c>description</c> → both <see cref="SubAgentTemplate.Description"/> AND
    ///     <see cref="SubAgentTemplate.WhenToUse"/>, so the Agent-tool catalog isn't blank
    ///     for discovered templates (which don't carry a separate <c>when_to_use</c> field).</item>
    ///   <item><c>model</c> → <see cref="SubAgentTemplate.DefaultOptions"/> with only
    ///     <see cref="GenerateReplyOptions.ModelId"/> set; absent leaves
    ///     <see cref="SubAgentTemplate.DefaultOptions"/> null so the sub-agent inherits the
    ///     parent's runtime defaults (matching the built-in templates' shape).</item>
    ///   <item><c>tools</c> → <see cref="SubAgentTemplate.EnabledTools"/>. Absent (null) means
    ///     inherit every parent tool; an empty list means deny all tools (distinct case).</item>
    ///   <item><see cref="DefaultMaxTurnsPerRun"/> to match the existing production templates.</item>
    /// </list>
    /// </remarks>
    internal static SubAgentTemplate MapToTemplate(
        ParsedSubAgent parsed,
        Func<IStreamingAgent> agentFactory)
    {
        var defaults = !string.IsNullOrWhiteSpace(parsed.Model)
            ? new GenerateReplyOptions { ModelId = parsed.Model.Trim() }
            : null;

        return new SubAgentTemplate
        {
            Name = parsed.Name,
            Description = parsed.Description,
            WhenToUse = parsed.Description,
            SystemPrompt = parsed.SystemPrompt,
            AgentFactory = agentFactory,
            DefaultOptions = defaults,
            EnabledTools = parsed.Tools,
            MaxTurnsPerRun = DefaultMaxTurnsPerRun,
        };
    }

    /// <summary>
    /// Normalises <paramref name="hostPath"/> to a full path with a trailing directory separator
    /// so a <see cref="string.StartsWith(string, StringComparison)"/> containment check rejects
    /// sibling-prefix attacks (e.g. <c>C:\work-evil</c> against base <c>C:\work</c>).
    /// </summary>
    internal static string NormalizeBasePath(string hostPath)
    {
        var full = Path.GetFullPath(hostPath);
        return full.EndsWith(Path.DirectorySeparatorChar)
            ? full
            : full + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Resolves <paramref name="relativeOrAbsolute"/> against <paramref name="basePath"/> and
    /// asserts the result stays inside <paramref name="basePath"/>. Guards path-injection
    /// (<c>"../etc/passwd"</c>); does NOT prevent symlink-escape, which would need
    /// <see cref="File.ResolveLinkTarget(string, bool)"/>/junction resolution and is out of
    /// scope for this loader. Comparison is case-insensitive on Windows and case-sensitive
    /// elsewhere, matching the underlying filesystem's behaviour.
    /// </summary>
    internal static bool TryResolveContainedPath(string basePath, string relativeOrAbsolute, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
        {
            return false;
        }

        string combined;
        try
        {
            combined = Path.IsPathRooted(relativeOrAbsolute)
                ? Path.GetFullPath(relativeOrAbsolute)
                : Path.GetFullPath(Path.Combine(basePath, relativeOrAbsolute));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        // basePath always carries a trailing separator (NormalizeBasePath); combined therefore
        // must START WITH that separator-terminated prefix to be "inside" basePath. Equality with
        // the bare basePath (i.e. the workspace root itself) is rejected because a sub-agent
        // markdown file MUST be a file under that root, not the root directory.
        if (!combined.StartsWith(basePath, comparison))
        {
            return false;
        }

        fullPath = combined;
        return true;
    }
}
