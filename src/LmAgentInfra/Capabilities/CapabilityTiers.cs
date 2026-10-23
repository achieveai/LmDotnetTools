using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Capabilities;

/// <summary>
/// A conversation's skill-tier configuration, from its mode. Entries are selectors (see
/// <see cref="CapabilityCatalog.ResolveSelectors"/>).
/// </summary>
/// <param name="Loaded">Skills the root agent starts with listed in its <c>Skill</c> tool.</param>
/// <param name="Pinned">Skills the root agent always has listed.</param>
/// <param name="SubAgentLoaded">Skills each sub-agent starts with listed.</param>
/// <param name="SubAgentPinned">Skills each sub-agent always has listed.</param>
/// <param name="SkillCap">
/// Most non-pinned skills a rotation keeps listed per agent, most recently used first. The seed is not capped.
/// </param>
/// <param name="AgentCap">
/// Null (default): sub-agent templates are not tiered, and <c>agent:</c> selectors are ignored with a WARN. Set: the
/// root's <c>Agent</c> tool lists only its loaded and pinned templates (<see cref="CapabilityTiers.ListedTemplates"/>),
/// <c>SearchAgent</c> and <c>SearchPlugin</c> find the rest, and a rotation keeps at most this many non-pinned
/// templates, most recently used first.
/// </param>
/// <param name="ToolCap">
/// Null (default): sandbox tools are not tiered, and <c>tool:</c> selectors are ignored with a WARN. Set: the root's
/// request lists only the sandbox tools its view lists (loaded and pinned), <c>SearchTool</c> finds the rest and
/// <c>ExecuteTool</c> runs them, and a rotation keeps at most this many non-pinned tools, most recently used first
/// (called directly or through <c>ExecuteTool</c>). A sub-agent given its own <c>ExecuteTool</c> does the same over its
/// own view.
/// </param>
public sealed record TierSettings(
    IReadOnlyList<string> Loaded,
    IReadOnlyList<string> Pinned,
    IReadOnlyList<string> SubAgentLoaded,
    IReadOnlyList<string> SubAgentPinned,
    int SkillCap = 10,
    int? AgentCap = null,
    int? ToolCap = null
);

/// <summary>
/// The gateway calls <see cref="CapabilityTiers"/> makes, bound to one sandbox session by the host. A
/// record of delegates so tests can stand in for the gateway.
/// </summary>
/// <param name="GetCatalog">The session's skill catalog for the DEFAULT agent (every admitted skill).</param>
/// <param name="PatchTiers">
/// Replaces one named agent's tiers: <c>reset: true</c> plus the given set (skill path to tier).
/// </param>
/// <param name="ListSkillContract">
/// Lists the sandbox's <c>Skill</c> tool again (MCP <c>tools/list</c>) as the agent on the current async flow
/// (no sub-agent actor = the root), converted exactly as the host's build converts it; null when the gateway
/// lists no <c>Skill</c>. A rotation calls it after a PATCH to pick up the new text.
/// </param>
public sealed record SkillTierGateway(
    Func<CancellationToken, Task<SandboxSkillCatalog>> GetCatalog,
    Func<string, IReadOnlyDictionary<string, SandboxSkillTier>, CancellationToken, Task> PatchTiers,
    Func<CancellationToken, Task<FunctionContract?>> ListSkillContract
);

/// <summary>
/// One conversation's loaded-vs-available skill tiers. The ROOT agent's <c>Skill</c> tool lists only the
/// skills in its view (loaded plus pinned); every other admitted skill stays loadable by name and is found
/// with <c>SearchSkill</c>. Each spawned sub-agent gets its own view the same way
/// (<see cref="ForInheritedToolAsync"/>), seeded from the mode's <c>subAgent</c> sets and rotated at its own
/// compactions; the root's and the sub-agents' views never touch each other.
/// </summary>
/// <remarks>
/// <para>
/// <b>One per conversation.</b> The host creates it at the conversation's first build and calls
/// <see cref="ConfigureAsync"/> on every build and mode switch. With no settings or no gateway it is
/// INERT, and an inert instance changes nothing: <see cref="RootGatewayAgentId"/> is null (the root sends
/// no agent id), <see cref="IsActive"/> is false (the host registers <c>Skill</c> as before and adds no
/// <c>SearchSkill</c>), and no gateway call is made.
/// </para>
/// <para>
/// <b>Why the root needs its own gateway agent id.</b> A sandbox session is shared by every conversation
/// on the workspace, and the default agent is shared with them. Tiers are set per named agent only, and
/// the root's name is its thread id (<see cref="SandboxSubAgentIdentity.ForRoot"/>).
/// </para>
/// <para>
/// <b>Apply.</b> Every configure GETs the catalog (current paths), resolves the root's view, then PATCHes
/// the root id with <c>reset</c> and EVERY path: listed ones <c>included</c>, all others
/// <c>available</c>. The PATCH never targets the default agent: the id is checked first.
/// </para>
/// <para>
/// <b>The <c>Skill</c> tool is the gateway's own text.</b> The host lists it AFTER configure, sent with
/// <see cref="RootGatewayAgentId"/>, so its text already lists only the root's view, and hands it to
/// <see cref="TryAttachRootSkill"/>; <see cref="RootProvider"/> then serves it. A rotation that changes the
/// view PATCHes, lists the tool again and swaps the contract the provider serves (the gateway has no MCP
/// list-changed notification), so the next request carries the new text and every other request carries
/// identical bytes.
/// </para>
/// <para>
/// <b>View.</b> Persisted in the root thread's metadata under <see cref="ViewPropertyKey"/>. Seeded from
/// the mode's <c>loaded</c> + <c>pinned</c> when absent; pinned is re-added at every configure.
/// </para>
/// <para>
/// <b>Rotation</b> (<see cref="OnCheckpointActivatedAsync"/>, the compaction hook). At each activation on the
/// agent's own thread the view becomes Pinned (every kind) plus what the agent used since the last rotation,
/// each kind capped by its most recent uses. Persisted first, then applied; an unchanged view sends nothing. Between
/// two rotations the view does not change, so neither do the tools a request lists.
/// </para>
/// <para>
/// <b>Sub-agent templates</b> tier only when <see cref="TierSettings.AgentCap"/> is set, and only for the root. The
/// templates come from the conversation's own source (<see cref="AttachAgents"/>), not the gateway, so a build
/// resolves <c>skill</c> selectors only and the commit seeds the <c>agent</c> kind once per view
/// (<c>seededKinds</c>). The root's <c>Agent</c> tool then lists its view (<see cref="ListedTemplates"/>); every
/// template still runs by its exact name. The index follows the source by reference, so a template registered later
/// is found by <c>SearchAgent</c> without changing the <c>Agent</c> text.
/// </para>
/// <para>
/// <b>Sandbox tools</b> tier only when <see cref="TierSettings.ToolCap"/> is set. The build hands
/// the tools its root sandbox connect registered to <see cref="Stage.AttachTools"/>; every one but <c>Skill</c> becomes
/// tier-managed. The root's provider is an <see cref="IDeferredToolProvider"/>: a request lists only the managed tools
/// the root's view lists, plus the fixed <c>SearchTool</c> and <c>ExecuteTool</c>, and every managed tool keeps its
/// handler. The agent loop turns an <c>ExecuteTool</c> call into the real call before approval, so approval, the
/// result and the lifecycle name the real tool. Host tools are never managed: they never leave the request.
/// </para>
/// <para>
/// A sub-agent that inherits <c>SearchTool</c> and <c>ExecuteTool</c> gets its own pair (<see cref="ForInheritedToolAsync"/>):
/// its managed tools are the ones its spawn inherited, its view's <c>tool</c> kind is seeded once from the sub-agent
/// lists, and its search and runner see only those tools through its own view; the root's view is never touched. A
/// template that names its tools without <c>ExecuteTool</c> keeps them plain (no companions): the author's choice.
/// </para>
/// </remarks>
public sealed class CapabilityTiers : IDisposable, IAsyncDisposable
{
    /// <summary>The thread-metadata key holding an agent's persisted view.</summary>
    public const string ViewPropertyKey = "capabilities.view";

    /// <summary>The tool name of the search tool.</summary>
    public const string SearchSkillToolName = "SearchSkill";

    /// <summary>The tool name of the sub-agent search tool; offered to the root only, with agents tiered.</summary>
    public const string SearchAgentToolName = "SearchAgent";

    /// <summary>The tool name of the plugin search tool; offered to the root only, with agents tiered.</summary>
    public const string SearchPluginToolName = "SearchPlugin";

    /// <summary>The tool name of the tool search; offered with tools tiered, a sub-agent's over its own tools.</summary>
    public const string SearchToolToolName = "SearchTool";

    /// <summary>
    /// The tool name of the tool runner; offered with tools tiered, a sub-agent's over its own tools. Only the agent loop's unwrap runs
    /// it; its own handler always fails closed.
    /// </summary>
    public const string ExecuteToolToolName = "ExecuteTool";

    /// <summary>
    /// The line the host appends to the system prompt of an agent that has <c>ExecuteTool</c>. A mode prompt may name
    /// tools the agent's tool list does not hold; a model then calls them directly, with no schema. Fixed text: a
    /// rotation never changes it, so it never breaks a cached prompt.
    /// </summary>
    public const string ExecuteToolPromptLine =
        "Only some tools are in your tool list. To use any other tool, even one these instructions name, first find it "
        + "with SearchTool, then run it with ExecuteTool.";

    /// <summary>The tool name of the gateway's skill tool.</summary>
    public const string SkillToolName = "Skill";

    /// <summary>
    /// The line the root's <c>Agent</c> tool shows after its listed templates, and at the end of its
    /// <c>subagent_type</c> text, while agents are tiered.
    /// </summary>
    public const string UnlistedAgentsHint =
        "More sub-agent types exist than are listed: find them with SearchAgent (or SearchPlugin), then run one "
        + "with Agent by its exact name.";

    /// <summary>
    /// A search hit scoring below this is marked <c>[weak]</c>. Derived from the slice-2 eval set (phase A): 1 of
    /// 60 off-topic hits scores above it, and 5 of 116 relevant hits below it. A small sample: revisit with more
    /// queries. Sub-agent and plugin hits use the same line, measured on the search eval's 63 sub-agents and 36 plugins
    /// (2026-10-23, default limit): the first hit of every labelled sub-agent query scored 9.81 to 56.29, and of 12 of
    /// the 13 labelled plugin queries 8.22 to 20.29; over 19 off-topic queries per kind no hit scored above 6.47
    /// (sub-agents) or 6.04 (plugins). A query made only of words most names share ("code", "review") scores even its
    /// answer low, so a first sub-agent or plugin hit is also not weak when a query word is one only its own name has
    /// (SC8: "code review correctness bugs specialist" finds code-reviewer:correctness-review first at 5.56, and "code
    /// review specialist reviewer" the code-reviewer plugin at 4.71). Over the 38 off-topic queries that clears one hit
    /// ("monitor my home security cameras": security-review, 6.47); it never clears a hit after the first.
    /// </summary>
    internal const double WeakScore = 7.0;

    /// <summary>
    /// A tool hit scoring below this is marked <c>[weak]</c>, unless a word of the query names the tool. The sandbox's
    /// ten tools make a small index, so their scores sit far below the skill scale of <see cref="WeakScore"/>. Measured
    /// on the gateway's tools (2026-10-22, default limit): over 16 queries with a known answer the right top hit scored
    /// 1.12 to 7.67, and the two under this line both name their tool (read a file: Read 1.41; stop a background shell:
    /// KillShell 1.12). Of 16 off-topic queries, 12 hit nothing and 2 hit at most 1.43. The other 2 hit Grep at 1.99,
    /// the same score as the right answer to "search text inside files": each matches Grep's one word "search" (or a
    /// typo-corrected "full"), so no score line can tell them apart, and they read as not weak.
    /// </summary>
    internal const double WeakToolScore = 1.9;

    private const string RootAgentKey = "root";
    private const string TierProviderName = "SkillTiers";
    private const int DefaultSearchLimit = 5;
    private const int MaxSearchLimit = 20;
    private const int MaxDescriptionLength = 200;
    private const int MaxNoticeSentenceLength = 150;
    private const int MaxPluginItemsPerKind = 8;
    private const string InvalidArgsCode = "invalid_args";
    private const string CallDirectlyCode = "call_directly";
    private const string ToolNotAvailableCode = "tool_not_available";
    private const string ExecuteToolUsage =
        "Call it as ExecuteTool({\"tool\": \"<exact name from SearchTool>\", \"arguments\": {<that tool's arguments>}}).";

    // A rotation runs inside the compacting loop's request path; it never holds that path longer than this.
    private static readonly TimeSpan RotationBudget = TimeSpan.FromSeconds(10);

    // Query words that name the kind itself ("an agent that ..."), so never one sub-agent or plugin (IsWeakByName).
    private static readonly HashSet<string> AgentKindWords = new(
        ["agent", "agents", "sub", "subagent", "subagents"],
        StringComparer.Ordinal
    );
    private static readonly HashSet<string> PluginKindWords = new(["plugin", "plugins"], StringComparer.Ordinal);

    // Text the model reads (search rows, refusals): quotes and apostrophes stay readable instead of \u0022.
    private static readonly JsonSerializerOptions ModelJsonOptions = new(JsonSchemaValidator.SchemaSerializationOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // F4: wherever no unwrap runs (a registry with no deferred provider, BuildMiddleware), ExecuteTool runs nothing.
    // Such a registry hides no tool, so the model is sent to the target by its own name (D-SC4-2).
    private static readonly ToolHandler ExecuteToolFailsClosed = (argsJson, _, _) =>
    {
        var tool = StringArgument(argsJson, "tool");
        var target = string.IsNullOrWhiteSpace(tool) ? "the tool" : tool;
        return Task.FromResult<ToolHandlerResult>(
            ToolHandlerResult.FromError(
                $"ExecuteTool cannot run tools here: every tool you can use is already in your tool list. Call {target} directly by its own name.",
                "not_supported"
            )
        );
    };

    // A sub-agent's own tiers are set up inside its spawn; the spawn never waits longer than this for them.
    private static readonly TimeSpan SpawnBudget = TimeSpan.FromSeconds(10);

    private readonly string _rootThreadId;
    private readonly IConversationStore _store;
    private readonly ILogger _logger;

    // Keyed by the agent's own thread id: the root, then each sub-agent at its first spawn.
    private readonly ConcurrentDictionary<string, AgentState> _agents = new(StringComparer.Ordinal);
    private readonly RootFunctionProvider _rootProvider;

    // The build a factory is running on this flow: while it is open, the root presents the STAGED agent id, so the
    // build's own sandbox listing is served as the mode being built, before anything is committed.
    private readonly AsyncLocal<BuildFlow?> _buildFlow = new();

    // Every swap of _catalog (with _indexedTemplates). A plain lock: the agent freshness check runs inside tool
    // handlers and under an agent's gate, and never awaits.
    private readonly object _catalogSwap = new();

    private volatile CapabilityCatalog? _catalog;
    private volatile SkillTierGateway? _gateway;
    private volatile TierSettings? _settings;

    // The live build's tier-managed sandbox tools by registered name; null = none (not tiered, inert, or no attach).
    // Swapped only at a commit, like the root view, so IsAdvertised answers the same between two rotations.
    private volatile IReadOnlyDictionary<string, FunctionContract>? _tools;
    private volatile string? _rootGatewayAgentId;
    private volatile bool _active;
    private int _skillCap = int.MaxValue;

    // The conversation's template source (AttachAgents). Indexed only while the live settings tier agents.
    private volatile MutableSubAgentTemplateSource? _agentSource;

    // The source snapshot the live catalog's agent items came from (null = none indexed); guarded by _catalogSwap.
    private IReadOnlyDictionary<string, SubAgentTemplate>? _indexedTemplates;

    // The root's last listing, handed out again while its names are unchanged (the Agent descriptor memo keys on it).
    private volatile TemplateListing? _listing;

    // Bumped by every configure: a sub-agent set up under an earlier build is set up again at its next spawn,
    // because the build may have moved the conversation to another sandbox session.
    private int _build;
    private bool _disposed;

    public CapabilityTiers(string rootThreadId, IConversationStore store, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootThreadId);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _rootThreadId = rootThreadId;
        _store = store;
        _logger = logger;
        _rootProvider = new RootFunctionProvider(this);
    }

    /// <summary>False while inert: the host wires tools exactly as it would without tiers.</summary>
    public bool IsActive => _active;

    /// <summary>
    /// The root agent's gateway agent id while active; null while inert (the root sends none). On a build's own
    /// flow (<see cref="Stage.EnterBuildFlow"/>) it is the staged id instead.
    /// </summary>
    public string? RootGatewayAgentId => _buildFlow.Value is { IsOpen: true } flow ? flow.RootId : _rootGatewayAgentId;

    /// <summary>
    /// The most one <see cref="StageAsync"/> waits for the root's gate and the gateway together. Past it the stage
    /// goes inert (logged as a failed apply), like any other gateway fault.
    /// </summary>
    internal TimeSpan ConfigureBudget { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The root agent's <c>Skill</c> (once attached) and <c>SearchSkill</c>. The host registers it only on a
    /// build that left the instance active; it also contributes nothing while inert, so a later configure that
    /// goes inert cannot leave a stale registration offering the tools.
    /// </summary>
    public IFunctionProvider RootProvider => _rootProvider;

    /// <summary>
    /// Takes the root's <c>Skill</c> tool from the host's build (listed after <see cref="ConfigureAsync"/>, as
    /// the root) so <see cref="RootProvider"/> serves it and a rotation can swap its text. Returns false, and
    /// takes nothing, for any other tool or while inert: the host then registers the tool as it always has.
    /// </summary>
    public bool TryAttachRootSkill(FunctionContract contract, ToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(handler);
        if (!_active || contract.Name != SkillToolName || !_agents.TryGetValue(_rootThreadId, out var root))
        {
            return false;
        }

        root.Skill = new SkillSlot(contract, handler);
        return true;
    }

    /// <summary>
    /// Records the conversation's sub-agent template source: the one its loop spawns from and discoveries register
    /// into. Call it before the build's <see cref="Stage.CommitAsync"/>, which seeds the root's templates from it.
    /// Only a mode with <see cref="TierSettings.AgentCap"/> uses it; until then it changes nothing.
    /// </summary>
    public void AttachAgents(MutableSubAgentTemplateSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _agentSource = source;
    }

    /// <summary>
    /// <c>SubAgentOptions.ListedTemplates</c>: the templates the <c>Agent</c> tool of the agent on
    /// <paramref name="agentThreadId"/> lists. For the root while agents are tiered and seeded, its view's templates
    /// with <see cref="UnlistedAgentsHint"/>; null (every template listed) for a sub-agent, while inert, and in a mode
    /// without <see cref="TierSettings.AgentCap"/>. The same instance comes back until the listed names change.
    /// </summary>
    public TemplateListing? ListedTemplates(string agentThreadId)
    {
        ArgumentNullException.ThrowIfNull(agentThreadId);
        if (
            !_active
            || _settings is not { AgentCap: not null }
            || _agentSource is null
            || !string.Equals(agentThreadId, _rootThreadId, StringComparison.Ordinal)
            || !_agents.TryGetValue(_rootThreadId, out var root)
            || root.View is not { } view
            || !view.SeededKinds.Contains(CapabilityCatalog.AgentKind, StringComparer.Ordinal)
        )
        {
            return null;
        }

        var names = view
            .Loaded.Where(k => k.StartsWith(CapabilityCatalog.AgentKeyPrefix, StringComparison.Ordinal))
            .Select(k => k[CapabilityCatalog.AgentKeyPrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);
        var last = _listing;
        if (last is not null && last.Names.SetEquals(names))
        {
            return last;
        }

        var listing = new TemplateListing(names, UnlistedAgentsHint);
        _listing = listing;
        return listing;
    }

    /// <summary>
    /// A skill the sandbox discovered mid-session becomes available without changing what the model is sent: GETs the
    /// catalog and adds the skills the live one lacks, so <c>SearchSkill</c> finds them at once and the next rotation's
    /// reset PATCH names their paths (available unless used; a path a reset leaves out goes back to the gateway
    /// default, included). Writes no view and sends no PATCH or listing: tools[] stays as it is until a compaction.
    /// Returns the skills added, in catalog order; none while inert, before the first commit, or when nothing is new.
    /// Never throws for a gateway failure or for <see cref="ConfigureBudget"/> (a WARN, none added); only the caller's
    /// cancellation propagates.
    /// </summary>
    public async Task<IReadOnlyList<CapabilityItem>> RefreshSkillsAfterDiscoveryAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_active || _gateway is not { } gateway)
        {
            return [];
        }

        var root = RootState();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ConfigureBudget);
        try
        {
            // The root's gate orders this with a build's stage and commit, which GET and adopt a catalog of their own.
            await root.Gate.WaitAsync(bounded.Token).ConfigureAwait(false);
            try
            {
                return AddDiscoveredSkills(await gateway.GetCatalog(bounded.Token).ConfigureAwait(false));
            }
            finally
            {
                _ = root.Gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Capabilities discovery refresh failed {RootThreadId} {Error}",
                _rootThreadId,
                ex is OperationCanceledException
                    ? $"refresh exceeded {ConfigureBudget.TotalSeconds:0.###} s"
                    : ex.Message
            );
            return [];
        }
    }

    /// <summary>
    /// The notice a conversation's agent gets for a skill <see cref="RefreshSkillsAfterDiscoveryAsync"/> added: the
    /// kind, the key, how to load it and the first sentence of its description.
    /// </summary>
    public static string SkillNotice(CapabilityItem skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return $"New skill available: {skill.Name} ({skill.Key}). Load it with Skill(skill=\"{skill.Name}\")."
            + FirstSentence(skill.Description);
    }

    /// <summary>
    /// The notice the root gets for a sub-agent template registered mid-session under <paramref name="templateKey"/>:
    /// the kind, the key, how to run it and the first sentence of its description.
    /// </summary>
    public static string AgentNotice(string templateKey, string? description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateKey);
        return $"New sub-agent available: {templateKey} ({CapabilityCatalog.AgentKeyPrefix}{templateKey}). "
            + $"Run it with Agent(subagent_type=\"{templateKey}\")."
            + FirstSentence(description);
    }

    /// <summary>
    /// Re-evaluates this conversation's tiers for a build and commits them at once: <see cref="StageAsync"/> then
    /// <see cref="Stage.CommitAsync"/>. <paramref name="settings"/> or <paramref name="gateway"/> null makes the
    /// instance inert. Never throws for a gateway failure: it logs <c>Capabilities apply failed</c> and leaves the
    /// root on whatever the gateway already has.
    /// </summary>
    public async Task ConfigureAsync(TierSettings? settings, SkillTierGateway? gateway, CancellationToken ct)
    {
        using var stage = await StageAsync(settings, gateway, ct).ConfigureAwait(false);
        await stage.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Prepares this conversation's tiers for a build without touching the live ones. The stage GETs the catalog,
    /// resolves the root's view and PATCHes the root's gateway agent, so the build lists a current <c>Skill</c>
    /// text; the root's view, <c>Skill</c>, agent id and settings change only at <see cref="Stage.CommitAsync"/>,
    /// which the host calls once the build is the conversation's. Disposing an uncommitted stage discards it, so a
    /// refused or failed build leaves the live loop exactly as it was. <paramref name="settings"/> or
    /// <paramref name="gateway"/> null stages an inert build. Never throws for a gateway failure or for
    /// <see cref="ConfigureBudget"/>: the stage goes inert with a WARN. Only the caller's cancellation propagates.
    /// </summary>
    public async Task<Stage> StageAsync(TierSettings? settings, SkillTierGateway? gateway, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var stage = new Stage(this);
        if (settings is null || gateway is null)
        {
            stage.InertReason = Stage.NotConfiguredReason;
            return stage;
        }

        var root = RootState();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(ConfigureBudget);
        try
        {
            await root.Gate.WaitAsync(bounded.Token).ConfigureAwait(false);
            try
            {
                await StageUnderGateAsync(stage, root, settings, gateway, bounded.Token).ConfigureAwait(false);
            }
            finally
            {
                _ = root.Gate.Release();
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Capabilities apply failed {ThreadId} {AgentKey} {Error}",
                _rootThreadId,
                RootAgentKey,
                $"configure exceeded {ConfigureBudget.TotalSeconds:0.###} s"
            );
            ReleaseCandidate(stage);
            stage.MakeInert(Stage.TimedOutReason);
        }
        catch
        {
            stage.Dispose();
            throw;
        }

        return stage;
    }

    private async Task StageUnderGateAsync(
        Stage stage,
        AgentState root,
        TierSettings settings,
        SkillTierGateway gateway,
        CancellationToken ct
    )
    {
        var rootId = SandboxSubAgentIdentity.ForRoot(_rootThreadId);
        if (rootId is null)
        {
            _logger.LogWarning(
                "Capabilities inert: thread id {RootThreadId} is not a valid gateway agent id",
                _rootThreadId
            );
            stage.InertReason = Stage.InvalidThreadIdReason;
            return;
        }

        WarnUntieredKinds(settings);
        CapabilityCatalog catalog;
        try
        {
            (catalog, stage.IndexedTemplates, stage.CatalogReused) = CandidateCatalog(
                await gateway.GetCatalog(ct).ConfigureAwait(false),
                settings
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Capabilities apply failed {ThreadId} {AgentKey} {Error}",
                _rootThreadId,
                RootAgentKey,
                ex.Message
            );
            stage.InertReason = Stage.CatalogUnavailableReason;
            return;
        }

        stage.Catalog = catalog;
        // Pinned is never persisted: it is resolved again from config at every build, for every kind a later step
        // resolves (agents at the commit), and a rotation always keeps it.
        var pinned = Resolve(RootAgentKey, catalog, settings.Pinned, CapabilityCatalog.SkillKind)
            .ToHashSet(StringComparer.Ordinal);
        if (await LoadViewAsync(root, pinned, settings.Loaded, catalog, ct).ConfigureAwait(false) is not { } loaded)
        {
            stage.InertReason = Stage.NewerSchemaReason;
            return;
        }

        stage.RootId = rootId;
        stage.Gateway = gateway;
        stage.Settings = settings;
        stage.Pinned = pinned;
        stage.View = loaded.View;
        stage.Seeded = loaded.Seeded;
        stage.LiveViewAtStage = root.View;
        // The host lists Skill after this returns, so a successful PATCH is all the build needs to be current.
        stage.Applied = await ApplyAsync(root, _rootThreadId, rootId, loaded.View, catalog, gateway, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Makes <paramref name="stage"/> the conversation's tiers; see <see cref="Stage.CommitAsync"/>.</summary>
    private async Task CommitAsync(Stage stage, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var root = RootState();
        await root.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!stage.TryMarkCommitted())
            {
                throw new InvalidOperationException(
                    "A capabilities stage commits once, and never after it is discarded."
                );
            }

            // A sub-agent set up under the previous build is set up again at its next spawn.
            _ = Interlocked.Increment(ref _build);
            // Every build lists and attaches its own Skill tool; one from an earlier build must not survive it.
            root.Skill = stage.Skill;
            if (stage.RootId is not { } rootId || stage.View is not { } view || stage.Settings is not { } settings)
            {
                ReleaseCandidate(stage);
                BecomeInert(stage.InertReason ?? Stage.NotConfiguredReason);
                return;
            }

            AdoptCatalog(stage);
            // A rotation that ran between stage and commit left the gateway on its own view: the next rotation
            // applies this one again.
            var raced = !ReferenceEquals(root.View, stage.LiveViewAtStage);
            if (raced && root.View is { } rotated)
            {
                // It also persisted its revision and window, which the stage's older view must not roll back: keep
                // them, and add this build's pins, so the next window starts after that rotation's checkpoint.
                view = rotated with
                {
                    Loaded = Union(rotated.Loaded, stage.Pinned),
                };
            }

            // Templates are the conversation's, not the gateway's: with no source attached the kind stays unseeded,
            // and the Agent tool lists every template (fails open).
            var pinned = stage.Pinned;
            var seededAgents = false;
            if (settings.AgentCap is not null && _agentSource is not null && FreshCatalog(settings) is { } agentCatalog)
            {
                (view, pinned, seededAgents) = AttachHostKind(
                    CapabilityCatalog.AgentKind,
                    root,
                    view,
                    pinned,
                    settings,
                    agentCatalog
                );
            }

            // Tools come from this build's own connect (Stage.AttachTools). None attached = the kind stays as it was and
            // hides nothing (fails open). Run again here: a raced rotation may have replaced the staged view.
            var seededTools = false;
            if (stage.Tools is not null && _catalog is { } toolCatalog)
            {
                (view, pinned, seededTools) = AttachHostKind(
                    CapabilityCatalog.ToolKind,
                    root,
                    view,
                    pinned,
                    settings,
                    toolCatalog
                );
            }

            if (!_active)
            {
                _logger.LogInformation(
                    "Capabilities active {RootThreadId}: the root agent now uses gateway agent id {GatewayAgentId}",
                    _rootThreadId,
                    rootId
                );
            }

            root.Pinned = pinned;
            root.View = view;
            root.GatewayAgentId = rootId;
            root.AppliedRevision = stage.Applied && !raced ? view.Revision : 0;
            _gateway = stage.Gateway;
            _settings = settings;
            _tools = stage.Tools;
            _skillCap = settings.SkillCap;
            _rootGatewayAgentId = rootId;
            _active = true;
            if (stage.Seeded || seededAgents || seededTools)
            {
                // The live loop already took this build, so nothing here may undo it: a failed write leaves the seed
                // in memory, and the next build seeds the same view again.
                try
                {
                    await PersistAsync(root, view, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Capabilities view of {AgentKey} not persisted", root.Key);
                }
            }
        }
        finally
        {
            _ = root.Gate.Release();
        }
    }

    private void DiscardStage(Stage stage)
    {
        ReleaseCandidate(stage);
        if (stage.IsActive)
        {
            _logger.LogInformation(
                "Capabilities build discarded {RootThreadId}: the live tiers are unchanged",
                _rootThreadId
            );
        }
    }

    private AgentState RootState() => _agents.GetOrAdd(_rootThreadId, _ => new AgentState(RootAgentKey, _rootThreadId));

    /// <summary>The work of <see cref="Stage.AttachTools"/>; see there.</summary>
    private void AttachTools(Stage stage, IReadOnlyList<FunctionContract> sandboxTools)
    {
        ArgumentNullException.ThrowIfNull(sandboxTools);
        if (
            !stage.IsPending
            || !stage.IsActive
            || stage.Settings is not { ToolCap: not null } settings
            || stage.Catalog is not { } staged
            || stage.View is not { } view
        )
        {
            return;
        }

        var tools = new Dictionary<string, FunctionContract>(StringComparer.Ordinal);
        foreach (var contract in sandboxTools.Where(c => c.Name != SkillToolName))
        {
            tools[contract.Name] = contract;
        }

        // Seeding against an empty listing would drop every tool the view lists, and persist that.
        if (tools.Count == 0)
        {
            _logger.LogInformation(
                "Capabilities tools not staged {RootThreadId}: the build registered no sandbox tools",
                _rootThreadId
            );
            return;
        }

        var catalog = StageToolCatalog(stage, staged, tools.Values);
        var (next, pinned, seeded) = AttachHostKind(
            CapabilityCatalog.ToolKind,
            RootState(),
            view,
            stage.Pinned,
            settings,
            catalog
        );
        stage.View = next;
        stage.Pinned = pinned;
        stage.Seeded |= seeded;
        stage.Tools = tools;
        _logger.LogInformation(
            "Capabilities tools staged {RootThreadId} {ManagedCount} {Listed}",
            _rootThreadId,
            tools.Count,
            next.Loaded.Where(k => KindOf(k) == CapabilityCatalog.ToolKind).ToArray()
        );
    }

    /// <summary>
    /// The stage's catalog with <paramref name="tools"/> as its tool items: the staged one when nothing changes, the
    /// live one when it already holds exactly these items (a steady state builds nothing new), else a new index only
    /// the commit adopts. A candidate the stage built and no longer uses is disposed.
    /// </summary>
    private CapabilityCatalog StageToolCatalog(
        Stage stage,
        CapabilityCatalog staged,
        IEnumerable<FunctionContract> tools
    )
    {
        CapabilityItem[] items =
        [
            .. staged.Items.Where(i => i.Kind != CapabilityCatalog.ToolKind),
            .. CapabilityCatalog.FromTools(tools),
        ];
        if (items.SequenceEqual(staged.Items))
        {
            return staged;
        }

        var live = _catalog;
        var reused = live is not null && items.SequenceEqual(live.Items);
        var next = reused ? live! : CapabilityCatalog.Build(items);
        ReleaseCandidate(stage);
        stage.Catalog = next;
        stage.CatalogReused = reused;
        return next;
    }

    /// <summary>
    /// <c>SubAgentOptions.InheritedToolOverride</c>: gives a spawning sub-agent its OWN <c>Skill</c> (the gateway's
    /// text for its own view, served with the parent's handler) and a <c>SearchSkill</c> that marks hits from its
    /// own view. The sub-agent's first such tool loads its view from its own thread (else seeds it from the
    /// mode's <c>subAgent</c> sets, revision 1, persisted), PATCHes its gateway id and lists <c>Skill</c> again as
    /// that sub-agent. While tools are tiered it also gives the sub-agent its own <c>SearchTool</c> and
    /// <c>ExecuteTool</c> over its own view: its request lists only the inherited managed tools that view lists
    /// (seeded once from the mode's <c>subAgent</c> <c>tool:</c> selectors), and its loop runs the rest through its
    /// <c>ExecuteTool</c>. Null for any other tool, while inert, and on any failure or after
    /// <see cref="SpawnBudget"/> (logged once): the sub-agent then inherits the parent's tool exactly as before,
    /// and its other tools fall back at once in the same build (an inherited <c>ExecuteTool</c> answers
    /// <c>not_supported</c>, and every inherited tool stays listed). A sub-agent left on the parent's tools is not
    /// rotated at its compactions until a later spawn installs its own. Only the caller's cancellation propagates.
    /// </summary>
    public async ValueTask<IFunctionProvider?> ForInheritedToolAsync(InheritedTool tool, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (_disposed)
        {
            return null;
        }

        // Depth 1: the sub-agent's thread hangs off the root thread, and its gateway id is the one its own calls
        // present (SandboxSubAgentIdentity.ForCurrentFlow under its actor scope).
        var threadId = SubAgentThreadIds.For(_rootThreadId, tool.AgentId);
        var toolsTiered = _active && _settings is { ToolCap: not null };
        var spawn = toolsTiered ? RecordSpawnTool(threadId, tool) : null;
        if (
            tool.Contract.Name is not (SkillToolName or SearchSkillToolName)
            && !(toolsTiered && tool.Contract.Name is SearchToolToolName or ExecuteToolToolName)
        )
        {
            return null;
        }

        // This spawn builds a new registry: until a tier tool is handed out below, it holds the parent's tools.
        if (_agents.TryGetValue(threadId, out var known))
        {
            known.OwnToolsInstalled = false;
        }

        if (!_active)
        {
            return null;
        }

        var agent = SubAgent(threadId, tool.AgentId);

        // A setup that already failed in this build is not tried again for the spawn's other tool: both tools fall
        // back together, and a hanging gateway costs a spawn one SpawnBudget, not one per tool.
        var build = Volatile.Read(ref _build);
        if (agent.FailedBuild == build)
        {
            _logger.LogDebug(
                "Capabilities setup of {ThreadId} {AgentKey} already failed in this build; {ToolName} is inherited",
                threadId,
                agent.Key,
                tool.Contract.Name
            );
            return null;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(SpawnBudget);
        try
        {
            await agent.Gate.WaitAsync(bounded.Token).ConfigureAwait(false);
            try
            {
                if (!await EnsureSubAgentAsync(agent, build, bounded.Token).ConfigureAwait(false))
                {
                    agent.FailedBuild = build;
                    return null;
                }

                if (tool.Contract.Name is SearchToolToolName or ExecuteToolToolName)
                {
                    await SeedSubAgentToolsAsync(agent, bounded.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _ = agent.Gate.Release();
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            agent.FailedBuild = build;
            _logger.LogWarning(
                ex,
                "Capabilities apply failed {ThreadId} {AgentKey} {Error}",
                threadId,
                agent.Key,
                $"sub-agent setup exceeded {SpawnBudget.TotalSeconds:0} s"
            );
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            agent.FailedBuild = build;
            _logger.LogWarning(
                ex,
                "Capabilities apply failed {ThreadId} {AgentKey} {Error}",
                threadId,
                agent.Key,
                ex.Message
            );
            return null;
        }

        agent.OwnToolsInstalled = true;
        if (tool.Contract.Name == ExecuteToolToolName)
        {
            spawn!.OwnExecuteTool = true;
            return new SubAgentExecuteToolProvider(agent, spawn);
        }

        return new SubAgentTierToolProvider(this, agent, tool.Contract.Name, tool.Handler, spawn);
    }

    private AgentState SubAgent(string threadId, string agentId) =>
        _agents.GetOrAdd(
            threadId,
            _ => new AgentState(agentId, threadId)
            {
                GatewayAgentId = SandboxSubAgentIdentity.ForSubAgent(_rootThreadId, agentId),
            }
        );

    /// <summary>
    /// Records one tool a sub-agent's spawn is offered, and returns that spawn's tools. A spawn offers each of its
    /// tools once, in one sequence, so a name offered again begins the next spawn. The tier-managed ones are the
    /// sub-agent's Included tools: its template's tool list has already limited them.
    /// </summary>
    private SpawnTools RecordSpawnTool(string threadId, InheritedTool tool)
    {
        var agent = SubAgent(threadId, tool.AgentId);
        var spawn = agent.Spawn;
        if (spawn is null || !spawn.Offered.Add(tool.Contract.Name))
        {
            spawn = new SpawnTools();
            _ = spawn.Offered.Add(tool.Contract.Name);
            agent.Spawn = spawn;
        }

        if (_tools is { } managed && managed.ContainsKey(tool.Contract.Name))
        {
            spawn.Managed[tool.Contract.Name] = tool.Contract;
        }

        return spawn;
    }

    /// <summary>
    /// Seeds a sub-agent's view with its tools once (the mode's <c>subAgent</c> <c>tool:</c> selectors, persisted with
    /// <c>seededKinds</c>), and adds its tool pins at every spawn. Runs under the sub-agent's gate, after
    /// <see cref="EnsureSubAgentAsync"/>.
    /// </summary>
    private async Task SeedSubAgentToolsAsync(AgentState agent, CancellationToken ct)
    {
        if (
            _settings is not { ToolCap: not null } settings
            || _catalog is not { } catalog
            || agent.View is not { } view
        )
        {
            return;
        }

        var pins = Resolve(agent.Key, catalog, settings.SubAgentPinned, CapabilityCatalog.ToolKind);
        agent.Pinned = agent.Pinned.Concat(pins).ToHashSet(StringComparer.Ordinal);
        var seeded = view.SeededKinds.Contains(CapabilityCatalog.ToolKind, StringComparer.Ordinal);
        var next = seeded
            ? view with
            {
                Loaded = Union(view.Loaded, pins),
            }
            : view with
            {
                Loaded = Union(
                    view.Loaded,
                    Resolve(agent.Key, catalog, settings.SubAgentLoaded, CapabilityCatalog.ToolKind).Concat(pins)
                ),
                SeededKinds = Union(view.SeededKinds, [CapabilityCatalog.ToolKind]),
            };
        if (next.Loaded.SequenceEqual(view.Loaded, StringComparer.Ordinal) && seeded)
        {
            return;
        }

        await PersistAsync(agent, next, ct).ConfigureAwait(false);
        agent.View = next;
        _logger.LogInformation(
            "Capabilities tools seeded {ThreadId} {AgentKey} {Listed}",
            agent.ThreadId,
            agent.Key,
            next.Loaded.Where(k => KindOf(k) == CapabilityCatalog.ToolKind).ToArray()
        );
    }

    /// <summary>
    /// Brings a sub-agent's view, gateway tiers and <c>Skill</c> text up to date for this build. False when it
    /// cannot (inert, a newer view schema, or an apply failure, which <see cref="ApplyAsync"/> logs). Runs under
    /// the sub-agent's gate.
    /// </summary>
    private async Task<bool> EnsureSubAgentAsync(AgentState agent, int build, CancellationToken ct)
    {
        if (agent.Skill is not null && agent.AppliedBuild == build && agent.AppliedRevision == agent.View?.Revision)
        {
            return true; // the same spawn's other tool, or another spawn of this sub-agent in this build
        }

        if (
            !_active
            || _gateway is not { } gateway
            || _catalog is not { } catalog
            || _settings is not { } settings
            || agent.GatewayAgentId is not { } gatewayAgentId
        )
        {
            return false;
        }

        agent.Pinned = Resolve(agent.Key, catalog, settings.SubAgentPinned, CapabilityCatalog.SkillKind)
            .ToHashSet(StringComparer.Ordinal);
        var view = await LoadOrSeedViewAsync(agent, settings.SubAgentLoaded, catalog, ct).ConfigureAwait(false);
        if (view is null)
        {
            return false;
        }

        agent.View = view;
        if (!await ApplyAsync(agent, agent.ThreadId, gatewayAgentId, view, catalog, gateway, ct).ConfigureAwait(false))
        {
            return false;
        }

        var contract = await ListSkillAsAsync(agent, gateway, ct).ConfigureAwait(false);
        agent.Skill = new SkillSlot(contract, Handler: null);
        agent.AppliedRevision = view.Revision;
        agent.AppliedBuild = build;
        _logger.LogInformation(
            "Capabilities relisted {ThreadId} {AgentKey} {Revision} {SkillDescriptionChars}",
            agent.ThreadId,
            agent.Key,
            view.Revision,
            contract.Description?.Length ?? 0
        );
        return true;
    }

    /// <summary>
    /// Lists the gateway's <c>Skill</c> tool again as <paramref name="agent"/>: the root on a flow with no sub-agent
    /// actor (the host's resolver reads the agent id from the flow), a sub-agent under its own actor scope,
    /// whatever flow its spawn or compaction runs on.
    /// </summary>
    private async Task<FunctionContract> ListSkillAsAsync(
        AgentState agent,
        SkillTierGateway gateway,
        CancellationToken ct
    )
    {
        FunctionContract? contract;
        if (agent.ThreadId == _rootThreadId)
        {
            if (AgentActorScope.Current is { } actor)
            {
                throw new InvalidOperationException(
                    $"The root's Skill tool cannot be listed from sub-agent flow '{actor.AgentId}'."
                );
            }

            contract = await gateway.ListSkillContract(ct).ConfigureAwait(false);
        }
        else
        {
            using (AgentActorScope.Begin(agent.Key))
            {
                contract = await gateway.ListSkillContract(ct).ConfigureAwait(false);
            }
        }

        if (contract is null)
        {
            throw new InvalidOperationException("The gateway listed no Skill tool after the PATCH.");
        }

        // The text this agent's Skill tool now serves: a sub-agent's requests are not in the host's dumps.
        _logger.LogDebug(
            "Capabilities Skill text {ThreadId} {AgentKey} {SkillDescription}",
            agent.ThreadId,
            agent.Key,
            contract.Description
        );
        return contract;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _active = false;
        _catalog?.Dispose();
        // The agents' gates are not disposed: a caller may still hold one (its Release would throw) or wait on one
        // (disposing a SemaphoreSlim orphans its queued waiters). They hold no handle until AvailableWaitHandle is read.
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void BecomeInert(string reason)
    {
        if (_active)
        {
            _logger.LogInformation(
                "Capabilities inert {RootThreadId} ({Reason}): the root agent goes back to the default gateway agent",
                _rootThreadId,
                reason
            );
        }

        _active = false;
        _gateway = null;
        _settings = null;
        _tools = null;
        _rootGatewayAgentId = null;
    }

    /// <summary>
    /// The live catalog when nothing it would index changed, else a new one that only a commit adopts, with the
    /// template snapshot its agent items came from, and whether it is the live instance. Skills only, unless
    /// <paramref name="settings"/> tier agents: then skills, plugins and the attached source's templates (none before
    /// <see cref="AttachAgents"/>).
    /// </summary>
    private (
        CapabilityCatalog Catalog,
        IReadOnlyDictionary<string, SubAgentTemplate>? Templates,
        bool Reused
    ) CandidateCatalog(SandboxSkillCatalog gatewayCatalog, TierSettings settings)
    {
        var templates = settings.AgentCap is null ? null : _agentSource?.Templates;
        IReadOnlyList<CapabilityItem> items = settings.AgentCap is null
            ? CapabilityCatalog.FromSkills(gatewayCatalog)
            :
            [
                .. CapabilityCatalog.FromSkills(gatewayCatalog),
                .. CapabilityCatalog.FromPlugins(gatewayCatalog),
                .. templates is null ? [] : CapabilityCatalog.FromTemplates(templates),
            ];
        var current = _catalog;
        return current is not null && current.Items.SequenceEqual(items)
            ? (current, templates, true)
            : (CapabilityCatalog.Build(items), templates, false);
    }

    /// <summary>Makes the stage's catalog the live one; see <see cref="Stage.CatalogReused"/>.</summary>
    private void AdoptCatalog(Stage stage)
    {
        lock (_catalogSwap)
        {
            var current = _catalog;
            // The stage reused the live index, and an agent refresh may have replaced it since (disposing it). The
            // replacement holds the same skills and plugins with newer templates, so it stays.
            if (ReferenceEquals(current, stage.Catalog) || stage.CatalogReused)
            {
                return;
            }

            _catalog = stage.Catalog;
            _indexedTemplates = stage.IndexedTemplates;
            // A search already holding the old instance gets ObjectDisposedException and retries on this one.
            current?.Dispose();
        }
    }

    /// <summary>
    /// The live catalog, its agent items first brought up to the attached source's current templates when
    /// <paramref name="settings"/> tier agents (F6: the source's snapshot reference is the freshness token, as for
    /// the <c>Agent</c> descriptor). Only the agent items change; skills and plugins stay as the last build indexed
    /// them. Null before the first commit.
    /// </summary>
    private CapabilityCatalog? FreshCatalog(TierSettings? settings)
    {
        var catalog = _catalog;
        if (catalog is null || settings is not { AgentCap: not null } || _agentSource is not { } source)
        {
            return catalog;
        }

        var templates = source.Templates;
        lock (_catalogSwap)
        {
            catalog = _catalog;
            if (catalog is null || ReferenceEquals(templates, _indexedTemplates))
            {
                return catalog;
            }

            CapabilityItem[] items =
            [
                .. catalog.Items.Where(i => i.Kind != CapabilityCatalog.AgentKind),
                .. CapabilityCatalog.FromTemplates(templates),
            ];
            _indexedTemplates = templates;
            if (items.SequenceEqual(catalog.Items))
            {
                return catalog;
            }

            var fresh = CapabilityCatalog.Build(items);
            _catalog = fresh;
            // A search already holding the old instance gets ObjectDisposedException and retries on this one.
            catalog.Dispose();
            _logger.LogInformation(
                "Capabilities agent index refreshed {RootThreadId} {TemplateCount}",
                _rootThreadId,
                templates.Count
            );
            return fresh;
        }
    }

    /// <summary>
    /// Swaps in a catalog holding the live items plus each skill of <paramref name="gatewayCatalog"/> the live one
    /// lacks, and returns those skills. Add-only: a skill the gateway no longer lists, or a changed description, waits
    /// for the next build, which indexes the whole catalog again. Nothing before the first commit.
    /// </summary>
    private IReadOnlyList<CapabilityItem> AddDiscoveredSkills(SandboxSkillCatalog gatewayCatalog)
    {
        lock (_catalogSwap)
        {
            if (_catalog is not { } current)
            {
                return [];
            }

            var known = current
                .Items.Where(i => i.Kind == CapabilityCatalog.SkillKind)
                .Select(i => i.Key)
                .ToHashSet(StringComparer.Ordinal);
            CapabilityItem[] added =
            [
                .. CapabilityCatalog.FromSkills(gatewayCatalog).Where(i => !known.Contains(i.Key)),
            ];
            if (added.Length == 0)
            {
                return [];
            }

            _catalog = CapabilityCatalog.Build([.. current.Items, .. added]);
            // A search already holding the old instance gets ObjectDisposedException and retries on this one.
            current.Dispose();
            _logger.LogInformation(
                "Capabilities skills discovered {RootThreadId} {Added}",
                _rootThreadId,
                added.Select(i => i.Key).ToArray()
            );
            return added;
        }
    }

    /// <summary>
    /// Seeds or validates the root view's keys of a host-sourced <paramref name="kind"/> (agents from the template
    /// source, tools from the build's connect), and returns the root's pins of every kind so far. A view whose
    /// <c>seededKinds</c> lacks the kind gains the mode's <c>loaded</c> and <c>pinned</c> items of it once
    /// (<c>Seeded</c>: the commit persists it). An already seeded one drops items <paramref name="catalog"/> no longer
    /// has, and gains the pins again.
    /// </summary>
    private (AgentView View, IReadOnlySet<string> Pinned, bool Seeded) AttachHostKind(
        string kind,
        AgentState root,
        AgentView view,
        IReadOnlySet<string> pinsSoFar,
        TierSettings settings,
        CapabilityCatalog catalog
    )
    {
        var kindPins = Resolve(root.Key, catalog, settings.Pinned, kind);
        var pinned = pinsSoFar.Concat(kindPins).ToHashSet(StringComparer.Ordinal);
        if (!view.SeededKinds.Contains(kind, StringComparer.Ordinal))
        {
            var seed = Resolve(root.Key, catalog, settings.Loaded, kind);
            return (
                view with
                {
                    Loaded = Union(view.Loaded, seed.Concat(kindPins)),
                    SeededKinds = Union(view.SeededKinds, [kind]),
                },
                pinned,
                true
            );
        }

        var known = view.Loaded.Where(k => KindOf(k) != kind || catalog.TryGet(k, out _));
        foreach (var dropped in view.Loaded.Except(known, StringComparer.Ordinal))
        {
            if (kind == CapabilityCatalog.AgentKind)
            {
                _logger.LogWarning(
                    "Capabilities view of {AgentKey} names {Key}, which this conversation's templates no longer offer; dropped",
                    root.Key,
                    dropped
                );
            }
            else
            {
                _logger.LogWarning(
                    "Capabilities view of {AgentKey} names {Key}, which this build's tools no longer include; dropped",
                    root.Key,
                    dropped
                );
            }
        }

        return (view with { Loaded = Union(known, kindPins) }, pinned, false);
    }

    /// <summary>
    /// F9: a selector of a kind that is not tiered is ignored, with a WARN: in the root lists, <c>agent:</c> without
    /// <see cref="TierSettings.AgentCap"/> and <c>tool:</c> without <see cref="TierSettings.ToolCap"/>; in the sub-agent
    /// lists, <c>agent:</c> always (sub-agents never search agents) and <c>tool:</c> without
    /// <see cref="TierSettings.ToolCap"/>.
    /// </summary>
    private void WarnUntieredKinds(TierSettings settings)
    {
        foreach (
            var selector in settings
                .Loaded.Concat(settings.Pinned)
                .Where(s => Untiered(s, settings.AgentCap is not null, settings.ToolCap is not null))
                .Concat(
                    settings
                        .SubAgentLoaded.Concat(settings.SubAgentPinned)
                        .Where(s => Untiered(s, false, settings.ToolCap is not null))
                )
        )
        {
            _logger.LogWarning("Capabilities selector kind not enabled {ThreadId} {Selector}", _rootThreadId, selector);
        }

        static bool Untiered(string? selector, bool agentsTiered, bool toolsTiered) =>
            (!agentsTiered && selector?.StartsWith(CapabilityCatalog.AgentKeyPrefix, StringComparison.Ordinal) == true)
            || (
                !toolsTiered && selector?.StartsWith(CapabilityCatalog.ToolKeyPrefix, StringComparison.Ordinal) == true
            );
    }

    /// <summary>Disposes a catalog the stage built and nothing adopted.</summary>
    private void ReleaseCandidate(Stage stage)
    {
        if (stage.Catalog is { } candidate && !ReferenceEquals(candidate, _catalog))
        {
            candidate.Dispose();
        }

        stage.Catalog = null;
    }

    /// <summary>A sub-agent's view, seeded and persisted when its thread has none; null for a newer schema.</summary>
    private async Task<AgentView?> LoadOrSeedViewAsync(
        AgentState agent,
        IReadOnlyList<string> loadedSelectors,
        CapabilityCatalog catalog,
        CancellationToken ct
    )
    {
        if (
            await LoadViewAsync(agent, agent.Pinned, loadedSelectors, catalog, ct).ConfigureAwait(false)
            is not { } loaded
        )
        {
            return null;
        }

        if (loaded.Seeded)
        {
            await PersistAsync(agent, loaded.View, ct).ConfigureAwait(false);
        }

        return loaded.View;
    }

    /// <summary>
    /// The agent's persisted view plus <paramref name="pinned"/>, or a revision-1 seed (<c>Seeded</c>, not persisted)
    /// when its thread has none; null when the persisted view has a newer schema.
    /// </summary>
    private async Task<(AgentView View, bool Seeded)?> LoadViewAsync(
        AgentState agent,
        IReadOnlySet<string> pinned,
        IReadOnlyList<string> loadedSelectors,
        CapabilityCatalog catalog,
        CancellationToken ct
    )
    {
        var metadata = await _store.LoadMetadataAsync(agent.ThreadId, ct).ConfigureAwait(false);
        var persisted = ReadView(metadata, out var newerSchema);
        if (newerSchema)
        {
            _logger.LogWarning(
                "Capabilities view of {AgentKey} has a newer schema; leaving it untouched and staying inert",
                agent.Key
            );
            return null;
        }

        if (persisted is not null)
        {
            // Only skills are checked here: the build knows them from the gateway. Other kinds come from the host
            // after the build (agents at the commit), so their keys wait for that kind's own check.
            var known = persisted
                .Loaded.Where(k => KindOf(k) != CapabilityCatalog.SkillKind || catalog.TryGet(k, out _))
                .ToList();
            foreach (var dropped in persisted.Loaded.Except(known, StringComparer.Ordinal))
            {
                _logger.LogWarning(
                    "Capabilities view of {AgentKey} names {Key}, which the sandbox no longer offers; dropped",
                    agent.Key,
                    dropped
                );
            }

            return (
                new AgentView(
                    Union(known, pinned),
                    persisted.Revision,
                    persisted.RotatedThroughSeq,
                    persisted.SeededKinds ?? AgentView.SkillsOnly
                ),
                false
            );
        }

        var loaded = Union(Resolve(agent.Key, catalog, loadedSelectors, CapabilityCatalog.SkillKind), pinned);
        return (new AgentView(loaded, 1, 0, AgentView.SkillsOnly), true);
    }

    private List<string> Resolve(
        string agentKey,
        CapabilityCatalog catalog,
        IReadOnlyList<string> selectors,
        string kind
    )
    {
        var issues = new List<SelectorIssue>();
        var keys = catalog.ResolveSelectors(selectors, issues, kind);
        foreach (var issue in issues)
        {
            _logger.LogWarning(
                "Capabilities selector unresolved {ThreadId} {AgentKey} {Selector} {Problem} {Candidates}",
                _rootThreadId,
                agentKey,
                issue.Selector,
                issue.Problem,
                issue.Candidates
            );
        }

        return [.. keys];
    }

    private async Task PersistAsync(AgentState agent, AgentView view, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(
            new PersistedView(
                PersistedView.CurrentSchemaVersion,
                view.Loaded,
                view.Revision,
                view.RotatedThroughSeq,
                // Omitted while only skills are seeded, so a skills-only view is written exactly as before.
                view.SeededKinds.SequenceEqual(AgentView.SkillsOnly, StringComparer.Ordinal)
                    ? null
                    : view.SeededKinds
            )
        );
        var isRoot = agent.ThreadId == _rootThreadId;
        try
        {
            await _store
                .UpdateMetadataAsync(
                    agent.ThreadId,
                    existing =>
                    {
                        // The root never mints a metadata row: a conversation without one is not ours to create.
                        // A sub-agent's thread is being created by its spawn, and its first row may come only
                        // after its first run, so its view starts the row (as AgentThreadOwnership does).
                        if (existing is null && isRoot)
                        {
                            throw new InvalidOperationException(
                                $"Conversation '{agent.ThreadId}' has no metadata row; the view stays in memory."
                            );
                        }

                        var row =
                            existing
                            ?? new ThreadMetadata
                            {
                                ThreadId = agent.ThreadId,
                                LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            };
                        var properties = (row.Properties ?? ImmutableDictionary<string, object>.Empty).SetItem(
                            ViewPropertyKey,
                            json
                        );
                        return row with { Properties = properties };
                    },
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Capabilities view of {AgentKey} not persisted", agent.Key);
        }
    }

    /// <summary>
    /// PATCHes <paramref name="view"/>; false (and a WARN) when anything on the way fails. The agent's
    /// <c>AppliedRevision</c> drops to 0 first: once a PATCH is attempted the gateway may hold either set, so until a
    /// caller records a success the next rotation PATCHes and lists again.
    /// </summary>
    private async Task<bool> ApplyAsync(
        AgentState agent,
        string threadId,
        string gatewayAgentId,
        AgentView view,
        CapabilityCatalog catalog,
        SkillTierGateway gateway,
        CancellationToken ct
    )
    {
        agent.AppliedRevision = 0;
        try
        {
            // An empty or invalid id would reach the DEFAULT agent, shared by every conversation.
            SandboxAgentId.ThrowIfInvalid(gatewayAgentId, nameof(gatewayAgentId));
            await gateway
                .PatchTiers(gatewayAgentId, SkillTierSet(catalog.Items, view.Loaded), ct)
                .ConfigureAwait(false);
            _logger.LogInformation(
                "Capabilities applied {AgentKey} {GatewayAgentId} {Revision} {Loaded}",
                agent.Key,
                gatewayAgentId,
                view.Revision,
                view.Loaded
            );
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Capabilities apply failed {ThreadId} {AgentKey} {Error}",
                threadId,
                agent.Key,
                ex.Message
            );
            return false;
        }
    }

    /// <summary>
    /// The PATCH body for a view: every SKILL in the catalog, <c>included</c> when the view lists it, otherwise
    /// <c>available</c>. Items of other kinds have no skill path and are never sent.
    /// </summary>
    internal static Dictionary<string, SandboxSkillTier> SkillTierSet(
        IEnumerable<CapabilityItem> items,
        IEnumerable<string> loaded
    )
    {
        var listed = loaded.ToHashSet(StringComparer.Ordinal);
        var set = new Dictionary<string, SandboxSkillTier>(StringComparer.Ordinal);
        foreach (var item in items.Where(i => i.Kind == CapabilityCatalog.SkillKind))
        {
            set[CapabilityCatalog.SkillPathOf(item.Key)] = listed.Contains(item.Key)
                ? SandboxSkillTier.Included
                : SandboxSkillTier.Available;
        }

        return set;
    }

    /// <summary>
    /// The compaction hook (<c>CompactionSetup.OnCheckpointActivated</c>): rotates the view of the agent whose
    /// thread just activated a checkpoint. Returns true only when that agent's tools changed (its <c>Skill</c>
    /// text was swapped, or its listed sandbox tools did, which a failed <c>Skill</c> apply does not undo), so the loop
    /// re-estimates its tool tokens; false when inert, when the view did not change, for a sub-agent serving the
    /// parent's tools (nothing rotates), or when anything else failed (logged, retried at the next activation or
    /// build). Only the caller's cancellation propagates.
    /// </summary>
    public async ValueTask<bool> OnCheckpointActivatedAsync(CheckpointActivation activation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(activation);
        // Route by the compacting thread (the root's, or a sub-agent's set up at its spawn); a sub-agent's
        // AgentId can read "root". Any other thread is not ours.
        if (_disposed || !_active || !_agents.TryGetValue(activation.ThreadId, out var agent))
        {
            return false;
        }

        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(RotationBudget);
        try
        {
            await agent.Gate.WaitAsync(bounded.Token).ConfigureAwait(false);
            try
            {
                return await RotateAsync(agent, activation, bounded.Token).ConfigureAwait(false);
            }
            finally
            {
                _ = agent.Gate.Release();
            }
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Capabilities apply failed {ThreadId} {AgentKey} {Error}",
                activation.ThreadId,
                agent.Key,
                $"rotation exceeded {RotationBudget.TotalSeconds:0} s"
            );
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Capabilities apply failed {ThreadId} {AgentKey} {Error}",
                activation.ThreadId,
                agent.Key,
                ex.Message
            );
            return false;
        }
    }

    private async Task<bool> RotateAsync(AgentState agent, CheckpointActivation activation, CancellationToken ct)
    {
        // Fresh: a template registered since the last search must still count as used.
        var settings = _settings;
        if (
            !_active
            || agent.View is not { } view
            || _gateway is not { } gateway
            || FreshCatalog(settings) is not { } catalog
            || agent.GatewayAgentId is not { } gatewayAgentId
        )
        {
            return false;
        }

        // A sub-agent serving the parent's Skill tool (its setup failed, or it spawned while inert) keeps its
        // view and gateway tiers as they are: its Skill calls went through the parent's tool, not its own.
        if (agent.ThreadId != _rootThreadId && !agent.OwnToolsInstalled)
        {
            _logger.LogInformation(
                "Capabilities rotation skipped {ThreadId} {AgentKey} {CheckpointSeq}: it serves the parent's tools",
                activation.ThreadId,
                agent.Key,
                activation.CheckpointSeq
            );
            return false;
        }

        // The window is ours, (RotatedThroughSeq, CheckpointSeq]. No row in it, no rotation.
        if (activation.LastMessageSeq <= view.RotatedThroughSeq)
        {
            _logger.LogInformation(
                "Capabilities rotation skipped {ThreadId} {AgentKey} {CheckpointSeq}: no rows after seq {RotatedThroughSeq}",
                activation.ThreadId,
                agent.Key,
                activation.CheckpointSeq,
                view.RotatedThroughSeq
            );
            return false;
        }

        var uses = new List<(string Key, long Seq)>();
        foreach (var (seq, call) in activation.ToolCalls)
        {
            if (seq > view.RotatedThroughSeq && UsedKey(agent, call, catalog, settings) is { } key)
            {
                uses.Add((key, seq));
            }
        }

        var agentCap = settings?.AgentCap ?? int.MaxValue;
        var toolCap = settings?.ToolCap ?? int.MaxValue;
        var loaded = Rotate(
            agent.Pinned,
            uses,
            kind =>
                kind switch
                {
                    CapabilityCatalog.SkillKind => _skillCap,
                    CapabilityCatalog.AgentKind => agentCap,
                    CapabilityCatalog.ToolKind => toolCap,
                    _ => int.MaxValue,
                }
        );
        var changed = !loaded.SequenceEqual(view.Loaded, StringComparer.Ordinal);
        // The request lists the view's tools (IsToolAdvertised), so a changed tool kind changes the tools at once.
        var toolsChanged = !loaded
            .Where(k => KindOf(k) == CapabilityCatalog.ToolKind)
            .SequenceEqual(view.Loaded.Where(k => KindOf(k) == CapabilityCatalog.ToolKind), StringComparer.Ordinal);
        var next = view with
        {
            Loaded = loaded,
            Revision = changed ? view.Revision + 1 : view.Revision,
            RotatedThroughSeq = activation.CheckpointSeq,
        };
        _logger.LogInformation(
            "Capabilities rotated {ThreadId} {AgentKey} {Revision} {Used} {Promoted} {Evicted}",
            activation.ThreadId,
            agent.Key,
            next.Revision,
            uses.Select(u => u.Key).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            loaded.Except(view.Loaded, StringComparer.Ordinal).ToArray(),
            view.Loaded.Except(loaded, StringComparer.Ordinal).ToArray()
        );

        // The harness view is the authority: persist first, then bring the gateway and the contract to it.
        await PersistAsync(agent, next, ct).ConfigureAwait(false);
        agent.View = next;
        if (!changed && agent.AppliedRevision == next.Revision)
        {
            return false; // cache: no PATCH, no re-list, identical tool bytes
        }

        if (
            !await ApplyAsync(agent, activation.ThreadId, gatewayAgentId, next, catalog, gateway, ct)
                .ConfigureAwait(false)
        )
        {
            return toolsChanged; // the old Skill contract stays; the next activation or build tries again
        }

        if (agent.Skill is not { } slot)
        {
            agent.AppliedRevision = next.Revision; // this build has no Skill tool to swap
            return toolsChanged;
        }

        var contract = await ListSkillAsAsync(agent, gateway, ct).ConfigureAwait(false);
        agent.Skill = slot with { Contract = contract };
        agent.AppliedRevision = next.Revision;
        _logger.LogInformation(
            "Capabilities relisted {ThreadId} {AgentKey} {Revision} {SkillDescriptionChars}",
            activation.ThreadId,
            agent.Key,
            next.Revision,
            contract.Description?.Length ?? 0
        );
        return true;
    }

    /// <summary>
    /// The next view: every pinned key (any kind, never capped) plus the used keys, where each kind keeps at most
    /// its cap of non-pinned keys, most recently used first (ties by key). Sorted, like every persisted view.
    /// </summary>
    internal static IReadOnlyList<string> Rotate(
        IReadOnlySet<string> pinned,
        IEnumerable<(string Key, long Seq)> uses,
        Func<string, int> capOfKind
    )
    {
        var kept = uses.Where(u => !pinned.Contains(u.Key))
            .GroupBy(u => u.Key, StringComparer.Ordinal)
            .Select(g => (Key: g.Key, LastSeq: g.Max(u => u.Seq)))
            .GroupBy(u => KindOf(u.Key), StringComparer.Ordinal)
            .SelectMany(kind =>
                kind.OrderByDescending(u => u.LastSeq)
                    .ThenBy(u => u.Key, StringComparer.Ordinal)
                    .Take(Math.Max(0, capOfKind(kind.Key)))
            )
            .Select(u => u.Key);
        return Union(pinned, kept);
    }

    /// <summary>A key's kind is its prefix: <c>skill:</c>, <c>agent:</c>, later <c>tool:</c>.</summary>
    private static string KindOf(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? key[..colon] : key;
    }

    // The one place a tool call becomes a use: a switch on the tool name, so each kind's step adds its case here.
    private string? UsedKey(AgentState agent, ToolCall call, CapabilityCatalog catalog, TierSettings? settings) =>
        call.FunctionName switch
        {
            SkillToolName => SkillKeyOf(agent, call.FunctionArgs, catalog),
            // Agents tier for the root only.
            SubAgentToolProvider.SpawnToolName
                when settings is { AgentCap: not null } && agent.ThreadId == _rootThreadId => AgentKeyOf(
                agent,
                call.FunctionArgs,
                catalog
            ),
            // The transcript keeps the model's call as sent: ExecuteTool naming the tool, even if it was refused.
            ExecuteToolToolName when settings is { ToolCap: not null } => ToolKeyOf(
                agent,
                StringArgument(call.FunctionArgs, "tool")
            ),
            _ when settings is { ToolCap: not null } => ToolKeyOf(agent, call.FunctionName),
            _ => null, // a search's hits and every other tool never count as a use
        };

    /// <summary>
    /// The key of a tool <paramref name="agent"/> manages: the root's tier-managed tools, or a sub-agent's inherited
    /// ones when its spawn gave it its own <c>ExecuteTool</c> (plain inherited tools never rotate). Null for any other
    /// name (unknown, not managed, or not inherited by this sub-agent): ignored.
    /// </summary>
    private string? ToolKeyOf(AgentState agent, string? name)
    {
        IReadOnlyDictionary<string, FunctionContract>? managed = null;
        if (agent.ThreadId == _rootThreadId)
        {
            managed = _tools;
        }
        else if (agent.Spawn is { OwnExecuteTool: true } spawn)
        {
            managed = spawn.Managed;
        }

        return name is not null && managed is not null && managed.ContainsKey(name)
            ? CapabilityCatalog.ToolKeyPrefix + name
            : null;
    }

    /// <summary>
    /// The catalog key of the template an <c>Agent</c> call named (<c>subagent_type</c>: an exact key, or the
    /// trailing segments of only one), or null when it names no single indexed template (ignored).
    /// </summary>
    private string? AgentKeyOf(AgentState agent, string? argsJson, CapabilityCatalog catalog)
    {
        var reference = StringArgument(argsJson, "subagent_type");
        if (!string.IsNullOrWhiteSpace(reference) && catalog.TryResolveAgent(reference, out var item))
        {
            return item.Key;
        }

        _logger.LogDebug(
            "Capabilities use ignored {AgentKey} {Reference}: it names no single sub-agent template",
            agent.Key,
            reference ?? argsJson
        );
        return null;
    }

    /// <summary>A string property of a tool call's JSON arguments; null when absent, not a string, or junk JSON.</summary>
    private static string? StringArgument(string? argsJson, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null; // junk arguments name nothing; the caller logs the raw text
        }
    }

    /// <summary>The catalog key a <c>Skill</c> call named, or null when it names no single skill (ignored).</summary>
    private string? SkillKeyOf(AgentState agent, string? argsJson, CapabilityCatalog catalog)
    {
        string? reference = null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            if (
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("skill", out var skill)
                && skill.ValueKind == JsonValueKind.String
            )
            {
                reference = skill.GetString();
            }
        }
        catch (JsonException)
        {
            // Junk arguments name no skill; logged below with the raw text.
        }

        if (!string.IsNullOrWhiteSpace(reference) && catalog.TryResolveSkill(reference, out var item))
        {
            return item.Key;
        }

        _logger.LogDebug(
            "Capabilities use ignored {AgentKey} {Reference}: it names no single skill",
            agent.Key,
            reference ?? argsJson
        );
        return null;
    }

    // Synchronous: the index is in memory. The ToolHandler shape still needs a Task.
    private Task<ToolHandlerResult> SearchSkillAsync(string viewThreadId, string argsJson) =>
        Task.FromResult<ToolHandlerResult>(
            Search(SearchSkillToolName, CapabilityCatalog.SkillKind, viewThreadId, argsJson)
        );

    private Task<ToolHandlerResult> SearchAgentAsync(string viewThreadId, string argsJson) =>
        Task.FromResult<ToolHandlerResult>(
            Search(SearchAgentToolName, CapabilityCatalog.AgentKind, viewThreadId, argsJson)
        );

    private Task<ToolHandlerResult> SearchPluginAsync(string viewThreadId, string argsJson) =>
        Task.FromResult<ToolHandlerResult>(
            Search(SearchPluginToolName, CapabilityCatalog.PluginKind, viewThreadId, argsJson)
        );

    private Task<ToolHandlerResult> SearchToolAsync(
        string viewThreadId,
        string argsJson,
        IReadOnlyDictionary<string, FunctionContract>? subAgentTools = null
    ) =>
        Task.FromResult<ToolHandlerResult>(
            Search(SearchToolToolName, CapabilityCatalog.ToolKind, viewThreadId, argsJson, subAgentTools)
        );

    /// <summary>
    /// One search of <paramref name="kind"/> (the tool <paramref name="toolName"/>), its hits marked loaded or available
    /// from the view of the agent on <paramref name="viewThreadId"/>, and <c>[weak]</c> below <see cref="WeakScore"/>
    /// (tools: below <see cref="WeakToolScore"/> unless the query names the tool). A sub-agent's tool search ranks only
    /// the tools it inherited (<paramref name="subAgentTools"/>): it ranks the kind's candidates, keeps the inherited
    /// hits, then takes the limit, so a root-only tool does not take an inherited tool's place. The catalog keeps at
    /// most its top 200 candidates per search, so past 200 matching tools an inherited one can still fall out.
    /// </summary>
    private ToolHandlerResult.Resolved Search(
        string toolName,
        string kind,
        string viewThreadId,
        string argsJson,
        IReadOnlyDictionary<string, FunctionContract>? subAgentTools = null
    )
    {
        string query;
        int limit;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            var root = document.RootElement;
            // JSON that is not an object ("text", [1,2], 42) names no query: the missing-query answer, never a throw.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return MissingQuery(toolName);
            }

            query =
                root.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
                    ? q.GetString()!
                    : string.Empty;
            limit =
                root.TryGetProperty("limit", out var l)
                && l.ValueKind == JsonValueKind.Number
                && l.TryGetInt32(out var n)
                    ? Math.Clamp(n, 1, MaxSearchLimit)
                    : DefaultSearchLimit;
        }
        catch (JsonException ex)
        {
            return ToolHandlerResult.FromError($"Tool arguments are not valid JSON: {ex.Message}", "invalid_args");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return MissingQuery(toolName);
        }

        // The caller's own actor when there is one: a sub-agent that inherited the root's SearchSkill logs as itself.
        var agentKey =
            AgentActorScope.Current?.AgentId
            ?? (_agents.TryGetValue(viewThreadId, out var owner) ? owner.Key : RootAgentKey);
        // Agents and plugins list templates: index the source's current ones first (F6).
        var catalog =
            (kind == CapabilityCatalog.SkillKind ? _catalog : FreshCatalog(_settings))
            ?? throw new InvalidOperationException($"{toolName} ran before this conversation loaded a skill catalog.");
        // A sub-agent's search ranks every item before it keeps its own: a cut first could leave none of them.
        var searchLimit = subAgentTools is null ? limit : int.MaxValue;
        IReadOnlyList<(CapabilityItem Item, double Score)> hits;
        try
        {
            hits = catalog.SearchScored(query, kind, searchLimit);
        }
        catch (ObjectDisposedException)
        {
            // A configure swapped the index mid-call: retry once on the replacement. The same instance
            // means this whole object was disposed, which is not retryable.
            var replacement = _catalog;
            if (replacement is null || ReferenceEquals(replacement, catalog))
            {
                throw;
            }

            catalog = replacement;
            hits = catalog.SearchScored(query, kind, searchLimit);
        }

        if (subAgentTools is not null)
        {
            hits = [.. hits.Where(h => subAgentTools.ContainsKey(h.Item.Name)).Take(limit)];
        }

        LogSearch(toolName, viewThreadId, agentKey, query, hits);
        var listed =
            _agents.TryGetValue(viewThreadId, out var agent) && agent.View is { } view
                ? view.Loaded.ToHashSet(StringComparer.Ordinal)
                : [];
        return ToolHandlerResult.FromText(
            kind switch
            {
                CapabilityCatalog.SkillKind => FormatSkillHits(query, hits, listed),
                CapabilityCatalog.AgentKind => FormatAgentHits(query, hits, listed, NamesOf(catalog, kind)),
                CapabilityCatalog.ToolKind => FormatToolHits(query, hits, listed, subAgentTools ?? _tools),
                _ => FormatPluginHits(query, hits, listed, catalog),
            }
        );
    }

    /// <summary>The query may carry user data: Information keeps its length only, and the text goes to Debug.</summary>
    private void LogSearch(
        string toolName,
        string viewThreadId,
        string agentKey,
        string query,
        IReadOnlyList<(CapabilityItem Item, double Score)> hits
    )
    {
        var keys = hits.Select(h => h.Item.Key).ToArray();
        switch (toolName)
        {
            case SearchSkillToolName:
                _logger.LogInformation(
                    "SearchSkill {ThreadId} {AgentKey} {QueryLength} {HitCount}",
                    viewThreadId,
                    agentKey,
                    query.Length,
                    hits.Count
                );
                _logger.LogDebug(
                    "SearchSkill query {ThreadId} {AgentKey} {Query} {HitKeys}",
                    viewThreadId,
                    agentKey,
                    query,
                    keys
                );
                break;
            case SearchAgentToolName:
                _logger.LogInformation(
                    "SearchAgent {ThreadId} {AgentKey} {QueryLength} {HitCount}",
                    viewThreadId,
                    agentKey,
                    query.Length,
                    hits.Count
                );
                _logger.LogDebug(
                    "SearchAgent query {ThreadId} {AgentKey} {Query} {HitKeys}",
                    viewThreadId,
                    agentKey,
                    query,
                    keys
                );
                break;
            case SearchToolToolName:
                _logger.LogInformation(
                    "SearchTool {ThreadId} {AgentKey} {QueryLength} {HitCount}",
                    viewThreadId,
                    agentKey,
                    query.Length,
                    hits.Count
                );
                _logger.LogDebug(
                    "SearchTool query {ThreadId} {AgentKey} {Query} {HitKeys}",
                    viewThreadId,
                    agentKey,
                    query,
                    keys
                );
                break;
            default:
                _logger.LogInformation(
                    "SearchPlugin {ThreadId} {AgentKey} {QueryLength} {HitCount}",
                    viewThreadId,
                    agentKey,
                    query.Length,
                    hits.Count
                );
                _logger.LogDebug(
                    "SearchPlugin query {ThreadId} {AgentKey} {Query} {HitKeys}",
                    viewThreadId,
                    agentKey,
                    query,
                    keys
                );
                break;
        }
    }

    private static ToolHandlerResult.Resolved MissingQuery(string toolName) =>
        ToolHandlerResult.FromError($"{toolName} needs a non-empty \"query\".", "invalid_args");

    // '\n', never AppendLine, in every formatter below: the model reads the same bytes whatever OS the host runs on.
    private static string FormatSkillHits(
        string query,
        IReadOnlyList<(CapabilityItem Item, double Score)> hits,
        IReadOnlySet<string> listed
    )
    {
        if (hits.Count == 0)
        {
            return $"No skills matched \"{query}\". Try different words for what the task is about "
                + "(a tool, file type or activity), or use a skill already listed in the Skill tool.";
        }

        var text = new StringBuilder();
        _ = text.Append(hits.Count).Append(hits.Count == 1 ? " skill" : " skills").Append(" found.\n");
        _ = text.Append(
            "\"loaded\" = already listed in your Skill tool; \"available\" = not listed, but loads the same way.\n"
        );
        AppendWeakLegend(text, hits.Any(h => h.Score < WeakScore));
        for (var i = 0; i < hits.Count; i++)
        {
            var (hit, score) = hits[i];
            AppendHitHeader(text, i, hit.Name, hit.Plugin ?? "workspace", score < WeakScore, listed.Contains(hit.Key));
            _ = text.Append("   ").Append(OneLine(hit.Description)).Append('\n');
            // The gateway name is unique (it qualifies clashes as mkt:plugin:skill) and is the only form Skill accepts.
            _ = text.Append("   Load: Skill(skill=\"").Append(hit.Name).Append("\")\n");
        }

        return text.ToString().TrimEnd();
    }

    private static string FormatAgentHits(
        string query,
        IReadOnlyList<(CapabilityItem Item, double Score)> hits,
        IReadOnlySet<string> listed,
        IReadOnlyList<string> names
    )
    {
        if (hits.Count == 0)
        {
            return $"No sub-agents matched \"{query}\". Try different words for what the task is about, or use a "
                + "sub-agent already listed in the Agent tool.";
        }

        var text = new StringBuilder();
        _ = text.Append(hits.Count).Append(hits.Count == 1 ? " sub-agent" : " sub-agents").Append(" found.\n");
        _ = text.Append(
            "\"loaded\" = already listed in your Agent tool; \"available\" = not listed, but runs the same way.\n"
        );
        var weak = hits.Select((h, i) => IsWeakByName(query, h.Item, h.Score, i == 0, names)).ToArray();
        AppendWeakLegend(text, weak.Contains(true));
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i].Item;
            AppendHitHeader(text, i, hit.Name, hit.Plugin ?? "none", weak[i], listed.Contains(hit.Key));
            _ = text.Append("   ").Append(OneLine(hit.Description)).Append('\n');
            _ = text.Append("   Run: Agent(subagent_type=\"").Append(hit.Name).Append("\")\n");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Tool hits, each with its arguments schema and its exact call: <c>X({...})</c> for a tool already in the agent's
    /// tool list, else <c>ExecuteTool</c> with the tool's exact name. A hit with no managed contract shows <c>{}</c>.
    /// The schema is <see cref="ShownSchema"/>, never stricter than ExecuteTool's check: each parameter keeps its
    /// description. A sandbox tool's contract keeps only each MCP property's type, so its item schemas and enum
    /// values are gone before this point: any choices survive only as description text.
    /// </summary>
    private static string FormatToolHits(
        string query,
        IReadOnlyList<(CapabilityItem Item, double Score)> hits,
        IReadOnlySet<string> listed,
        IReadOnlyDictionary<string, FunctionContract>? tools
    )
    {
        if (hits.Count == 0)
        {
            return $"No tools matched \"{query}\". Try different words for the action (for example \"run a shell "
                + "command\" or \"edit a file\"), or use a tool already in your tool list.";
        }

        var text = new StringBuilder();
        _ = text.Append(hits.Count).Append(hits.Count == 1 ? " tool" : " tools").Append(" found.\n");
        _ = text.Append(
            "\"loaded\" = in your tool list: call it directly; \"available\" = not in your tool list: run it with "
                + "ExecuteTool exactly as shown.\n"
        );
        var weak = hits.Select(h => IsWeakTool(query, h.Item, h.Score)).ToArray();
        AppendWeakLegend(text, weak.Contains(true));
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i].Item;
            var loaded = listed.Contains(hit.Key);
            _ = text.Append('\n')
                .Append(i + 1)
                .Append(". ")
                .Append(hit.Name)
                .Append(' ')
                .Append(weak[i] ? "[weak] " : string.Empty)
                .Append(loaded ? "[loaded]" : "[available]")
                .Append('\n');
            _ = text.Append("   ").Append(OneLine(hit.Description)).Append('\n');
            var schema =
                tools is not null && tools.TryGetValue(hit.Name, out var contract) ? ShownSchema(contract) : null;
            _ = text.Append("   Args: ")
                .Append(schema is null ? "{}" : JsonSerializer.Serialize(schema, ModelJsonOptions))
                .Append('\n');
            _ = loaded
                ? text.Append("   Call: ").Append(hit.Name).Append("({...})\n")
                : text.Append("   Call: ExecuteTool({\"tool\":\"")
                    .Append(hit.Name)
                    .Append("\",\"arguments\":{...}})\n");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// A tool's arguments schema as the model is shown it (SearchTool's Args, an <c>invalid_args</c> refusal):
    /// <see cref="FunctionContract.GetParametersSchema"/>, relaxed. An MCP tool's contract keeps only each property's
    /// type, so an object's keys are gone: the root and every object without properties (array items too) are open
    /// (the MCP server decides). The types, property names and required lists stay. ExecuteTool checks an available
    /// tool's arguments against <see cref="GateSchema"/>, never stricter, so a call that follows this schema passes.
    /// </summary>
    private static JsonObject ShownSchema(FunctionContract contract) => RelaxedSchema(contract, forGate: false);

    /// <summary>
    /// <see cref="ShownSchema"/> as ExecuteTool checks it: also without the type of a string-typed node, because MCP
    /// extraction maps a union, <c>anyOf</c> or missing type to string, and without the object keywords the contract
    /// writes on every node where they mean nothing.
    /// </summary>
    private static JsonObject GateSchema(FunctionContract contract) => RelaxedSchema(contract, forGate: true);

    private static JsonObject RelaxedSchema(FunctionContract contract, bool forGate)
    {
        var schema = JsonSerializer
            .SerializeToNode(contract.GetParametersSchema(), JsonSchemaValidator.SchemaSerializationOptions)!
            .AsObject();
        Relax(schema, forGate);
        schema["additionalProperties"] = true;
        return schema;
    }

    /// <summary>Relaxes one node of <see cref="RelaxedSchema"/> and, recursively, its properties and items.</summary>
    private static void Relax(JsonObject node, bool forGate)
    {
        var types = SchemaTypes(node);
        if (forGate && types.Contains("string"))
        {
            _ = node.Remove("type");
            types = [];
        }

        if (node["properties"] is JsonObject { Count: > 0 } properties)
        {
            foreach (var (_, property) in properties)
            {
                if (property is JsonObject child)
                {
                    Relax(child, forGate);
                }
            }
        }
        else if (types.Contains("object"))
        {
            node["additionalProperties"] = true;
        }
        else if (forGate)
        {
            // The contract writes these object keywords on every node. Here they mean nothing, or, once the type is
            // gone, they would close an object the tool may accept.
            _ = node.Remove("additionalProperties");
            if (node["required"] is JsonArray { Count: 0 })
            {
                _ = node.Remove("required");
            }
        }

        if (node["items"] is JsonObject items)
        {
            Relax(items, forGate);
        }
    }

    private static string[] SchemaTypes(JsonObject node) =>
        node["type"] switch
        {
            JsonValue one when one.TryGetValue<string>(out var type) => [type],
            JsonArray many =>
            [
                .. many.Select(t => t is JsonValue v && v.TryGetValue<string>(out var type) ? type : null)
                    .OfType<string>(),
            ],
            _ => [],
        };

    /// <summary>
    /// Each plugin hit with the skills and sub-agents it brings, at most <see cref="MaxPluginItemsPerKind"/> per kind.
    /// A skill belongs to the hit's exact marketplace and plugin. A sub-agent belongs to the plugin name in its own
    /// marketplace, or in every marketplace when it has none (a built-in or workspace agent), as <c>plugin:</c>
    /// selectors resolve.
    /// </summary>
    private static string FormatPluginHits(
        string query,
        IReadOnlyList<(CapabilityItem Item, double Score)> hits,
        IReadOnlySet<string> listed,
        CapabilityCatalog catalog
    )
    {
        if (hits.Count == 0)
        {
            return $"No plugins matched \"{query}\". Try different words for the area (for example \"debugging\" or "
                + "\"code review\"), or search skills and sub-agents directly with SearchSkill and SearchAgent.";
        }

        var text = new StringBuilder();
        _ = text.Append(hits.Count).Append(hits.Count == 1 ? " plugin" : " plugins").Append(" found.\n");
        _ = text.Append(
            "\"loaded\" = already listed in your Skill or Agent tool; \"available\" = not listed, but works the same "
                + "way.\n"
        );
        var names = NamesOf(catalog, CapabilityCatalog.PluginKind);
        var weak = hits.Select((h, i) => IsWeakByName(query, h.Item, h.Score, i == 0, names)).ToArray();
        AppendWeakLegend(text, weak.Contains(true));
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i].Item;
            _ = text.Append('\n')
                .Append(i + 1)
                .Append(". ")
                .Append(hit.Name)
                .Append(" (marketplace: ")
                .Append(hit.Marketplace)
                .Append(')')
                .Append(weak[i] ? " [weak]" : string.Empty)
                .Append('\n');
            _ = text.Append("   ").Append(OneLine(hit.Description)).Append('\n');
            var skills = catalog
                .Items.Where(item =>
                    item.Kind == CapabilityCatalog.SkillKind
                    && string.Equals(item.Marketplace, hit.Marketplace, StringComparison.Ordinal)
                    && string.Equals(item.Plugin, hit.Plugin, StringComparison.Ordinal)
                )
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            var agents = catalog
                .Items.Where(item =>
                    item.Kind == CapabilityCatalog.AgentKind
                    && string.Equals(item.Plugin, hit.Plugin, StringComparison.Ordinal)
                    && (
                        item.Marketplace is null
                        || string.Equals(item.Marketplace, hit.Marketplace, StringComparison.Ordinal)
                    )
                )
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            if (skills.Count == 0 && agents.Count == 0)
            {
                _ = text.Append("   No skills or sub-agents.\n");
                continue;
            }

            AppendPluginItems(text, "Skills", skills, listed, "Skill(skill=", SearchSkillToolName);
            AppendPluginItems(text, "Sub-agents", agents, listed, "Agent(subagent_type=", SearchAgentToolName);
        }

        return text.ToString().TrimEnd();
    }

    private static void AppendPluginItems(
        StringBuilder text,
        string heading,
        List<CapabilityItem> items,
        IReadOnlySet<string> listed,
        string callPrefix,
        string searchToolName
    )
    {
        if (items.Count == 0)
        {
            return;
        }

        _ = text.Append("   ").Append(heading).Append(" (").Append(items.Count).Append("):\n");
        foreach (var item in items.Take(MaxPluginItemsPerKind))
        {
            _ = text.Append("   - ")
                .Append(item.Name)
                .Append(listed.Contains(item.Key) ? " [loaded]: " : " [available]: ")
                .Append(callPrefix)
                .Append('"')
                .Append(item.Name)
                .Append("\")\n");
        }

        if (items.Count > MaxPluginItemsPerKind)
        {
            _ = text.Append("   +")
                .Append(items.Count - MaxPluginItemsPerKind)
                .Append(" more: use ")
                .Append(searchToolName)
                .Append('\n');
        }
    }

    /// <summary>
    /// A hit row: <c>N. name (plugin: p) [weak] [loaded|available]</c>. The weak mark goes before the tier mark, so
    /// every row still ends with it.
    /// </summary>
    private static void AppendHitHeader(
        StringBuilder text,
        int index,
        string name,
        string plugin,
        bool weak,
        bool loaded
    ) =>
        _ = text.Append('\n')
            .Append(index + 1)
            .Append(". ")
            .Append(name)
            .Append(" (plugin: ")
            .Append(plugin)
            .Append(") ")
            .Append(weak ? "[weak] " : string.Empty)
            .Append(loaded ? "[loaded]" : "[available]")
            .Append('\n');

    private static bool IsWeakTool(string query, CapabilityItem hit, double score) =>
        score < WeakToolScore && !CapabilityCatalog.QueryNamesItem(query, hit.Name);

    /// <summary>
    /// A sub-agent or plugin hit below <see cref="WeakScore"/> is weak, unless it is the <paramref name="first"/> hit and
    /// a word of the query is one that only its own name has among <paramref name="names"/>, the kind's indexed names.
    /// Words that name the kind itself ("an agent that ...") name no item.
    /// </summary>
    private static bool IsWeakByName(
        string query,
        CapabilityItem hit,
        double score,
        bool first,
        IReadOnlyList<string> names
    ) =>
        score < WeakScore
        && !(
            first
            && CapabilityCatalog.QueryNamesItemUniquely(
                query,
                hit.Name,
                names,
                hit.Kind == CapabilityCatalog.AgentKind ? AgentKindWords : PluginKindWords
            )
        );

    private static List<string> NamesOf(CapabilityCatalog catalog, string kind) =>
        [.. catalog.Items.Where(item => item.Kind == kind).Select(item => item.Name)];

    /// <summary>One legend line, only when a hit is weak: a result with none reads exactly as before.</summary>
    private static void AppendWeakLegend(StringBuilder text, bool anyWeak)
    {
        if (anyWeak)
        {
            _ = text.Append("\"weak\" = matches your words only loosely; check its description before you use it.\n");
        }
    }

    private static string OneLine(string description)
    {
        var flat = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= MaxDescriptionLength ? flat : flat[..(MaxDescriptionLength - 3)] + "...";
    }

    /// <summary>
    /// " " and the first sentence of <paramref name="description"/> on one line, cut to
    /// <see cref="MaxNoticeSentenceLength"/>; empty for none. A notice carries no full description.
    /// </summary>
    private static string FirstSentence(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return string.Empty;
        }

        var flat = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var end = flat.IndexOfAny(['.', '!', '?']);
        while (end >= 0 && end + 1 < flat.Length && flat[end + 1] != ' ')
        {
            end = flat.IndexOfAny(['.', '!', '?'], end + 1);
        }

        var sentence = end < 0 ? flat : flat[..(end + 1)];
        return " "
            + (
                sentence.Length <= MaxNoticeSentenceLength
                    ? sentence
                    : sentence[..(MaxNoticeSentenceLength - 3)] + "..."
            );
    }

    private static IReadOnlyList<string> Union(IEnumerable<string> first, IEnumerable<string> second) =>
        [.. first.Concat(second).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal)];

    private static PersistedView? ReadView(ThreadMetadata? metadata, out bool newerSchema)
    {
        newerSchema = false;
        if (metadata?.Properties is null || !metadata.Properties.TryGetValue(ViewPropertyKey, out var raw))
        {
            return null;
        }

        // An in-memory store hands back the string written; file and SQLite stores a JsonElement.
        var json = raw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement element => element.GetRawText(),
            _ => null,
        };
        if (json is null)
        {
            return null;
        }

        try
        {
            var view = JsonSerializer.Deserialize<PersistedView>(json);
            if (view is null)
            {
                return null;
            }

            if (view.SchemaVersion > PersistedView.CurrentSchemaVersion)
            {
                newerSchema = true;
                return null;
            }

            return view with
            {
                Loaded = view.Loaded ?? [],
            };
        }
        catch (JsonException)
        {
            return null; // corrupt: treat as absent and re-seed
        }
    }

    private static FunctionContract SearchSkillContract { get; } =
        new()
        {
            Name = SearchSkillToolName,
            Description =
                "Search every skill this workspace offers, including the many that are NOT listed in the Skill "
                + "tool. Use it when no listed skill fits the task. Returns the best matches, each with its "
                + "plugin, a one-line description, whether it is loaded (already listed in your Skill tool) or "
                + "available (not listed, still loadable), and the exact Skill call that loads it.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "query",
                    Description =
                        "What you need, in plain words: the task, tool, file type or activity (for example "
                        + "\"fill a pdf form\" or \"debug a failing test\").",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "limit",
                    Description = $"Most results to return (default {DefaultSearchLimit}, maximum {MaxSearchLimit}).",
                    ParameterType = new JsonSchemaObject { Type = new("integer") },
                    IsRequired = false,
                },
            ],
        };

    private static FunctionContract SearchAgentContract { get; } =
        new()
        {
            Name = SearchAgentToolName,
            Description =
                "Search every sub-agent type you can run, including the ones NOT listed in the Agent tool. Use it "
                + "when no listed sub-agent fits the task. Returns the best matches, each with its plugin, a one-line "
                + "description, whether it is loaded (already listed in your Agent tool) or available (not listed, "
                + "still runs), and the exact Agent call that runs it.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "query",
                    Description =
                        "What the sub-agent should do, in plain words (for example \"review a pull request\" or "
                        + "\"debug a failing test\").",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "limit",
                    Description = $"Most results to return (default {DefaultSearchLimit}, maximum {MaxSearchLimit}).",
                    ParameterType = new JsonSchemaObject { Type = new("integer") },
                    IsRequired = false,
                },
            ],
        };

    private static FunctionContract SearchPluginContract { get; } =
        new()
        {
            Name = SearchPluginToolName,
            Description =
                "Search the plugins this workspace offers by the area they cover. Returns the best matches, each with "
                + "its skills and sub-agents, whether each is loaded (already listed in your Skill or Agent tool) or "
                + "available (not listed, still usable), and the exact call that loads or runs it. Use it to find "
                + "everything for one area at once.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "query",
                    Description = "The area or task, in plain words (for example \"debugging\" or \"code review\").",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "limit",
                    Description = $"Most results to return (default {DefaultSearchLimit}, maximum {MaxSearchLimit}).",
                    ParameterType = new JsonSchemaObject { Type = new("integer") },
                    IsRequired = false,
                },
            ],
        };

    private static FunctionContract SearchToolContract { get; } =
        new()
        {
            Name = SearchToolToolName,
            Description =
                "Search every tool you can run, including the ones NOT in your tool list. Use it when no listed tool "
                + "fits the task. Returns the best matches, each with a one-line description, whether it is loaded (in "
                + "your tool list: call it directly) or available (run it with ExecuteTool), its arguments schema and "
                + "the exact call.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "query",
                    Description =
                        "The action you need, in plain words (for example \"run a shell command\" or \"find files by "
                        + "name\").",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "limit",
                    Description = $"Most results to return (default {DefaultSearchLimit}, maximum {MaxSearchLimit}).",
                    ParameterType = new JsonSchemaObject { Type = new("integer") },
                    IsRequired = false,
                },
            ],
        };

    private static FunctionContract ExecuteToolContract { get; } =
        new()
        {
            Name = ExecuteToolToolName,
            Description =
                "Run an available tool that is not in your tool list. Find it and its arguments with SearchTool first. "
                + "`tool` = exact name; `arguments` = that tool's arguments object.",
            Parameters =
            [
                new FunctionParameterContract
                {
                    Name = "tool",
                    Description = "The tool's exact name, as SearchTool shows it.",
                    ParameterType = new JsonSchemaObject { Type = new("string") },
                    IsRequired = true,
                },
                new FunctionParameterContract
                {
                    Name = "arguments",
                    Description = "The tool's own arguments: one object matching the Args schema SearchTool shows.",
                    // AdditionalProperties defaults to false and is always written: false would allow only {}.
                    ParameterType = new JsonSchemaObject { Type = new("object"), AdditionalProperties = true },
                    IsRequired = true,
                },
            ],
        };

    /// <summary>
    /// An agent's listed items (catalog keys of every kind, sorted), its persisted counters, and the kinds already
    /// seeded from config (sorted): a kind seeds once per view, so a later build only validates it.
    /// </summary>
    internal sealed record AgentView(
        IReadOnlyList<string> Loaded,
        int Revision,
        long RotatedThroughSeq,
        IReadOnlyList<string> SeededKinds
    )
    {
        /// <summary>The seeded kinds of a view that never seeded anything but skills (and of every slice-1 view).</summary>
        public static IReadOnlyList<string> SkillsOnly { get; } = [CapabilityCatalog.SkillKind];
    }

    private sealed record PersistedView(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("loaded")] IReadOnlyList<string> Loaded,
        [property: JsonPropertyName("revision")] int Revision,
        // The checkpoint row seq the last rotation counted through: the next window starts after it.
        [property: JsonPropertyName("rotatedThroughSeq")] long RotatedThroughSeq,
        // Absent = skills only (every view written before agents tiered). Optional, so the schema stays 1.
        [property: JsonPropertyName("seededKinds")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            IReadOnlyList<string>? SeededKinds = null
    )
    {
        public const int CurrentSchemaVersion = 1;
    }

    /// <summary>
    /// An agent's own <c>Skill</c> tool: its current contract (swapped by reference) and, for the root, the handler
    /// the host attached. A sub-agent's slot has no handler: each of its providers holds the parent's handler.
    /// </summary>
    internal sealed record SkillSlot(FunctionContract Contract, ToolHandler? Handler);

    /// <summary>Per-agent state; every read-modify-apply of it runs under <see cref="Gate"/>.</summary>
    private sealed class AgentState(string key, string threadId)
    {
        /// <summary><c>root</c>, or the sub-agent's id (<c>agent-N</c>).</summary>
        public string Key { get; } = key;

        /// <summary>The agent's own thread: where its view persists, and how the compaction hook finds it.</summary>
        public string ThreadId { get; } = threadId;

        /// <summary>The id its gateway tiers are set on; the root's is known once a configure goes active.</summary>
        public string? GatewayAgentId { get; set; }

        /// <summary>The configure (build) a sub-agent was last set up under; see <c>_build</c>.</summary>
        public int AppliedBuild { get; set; }

        /// <summary>The build in which a sub-agent's setup last failed or timed out; null = none.</summary>
        public int? FailedBuild { get; set; }

        /// <summary>
        /// A sub-agent's: true once its latest spawn was handed a tier tool of its own. False = that spawn's registry
        /// holds the parent's tools, so its compactions do not rotate it. Unused for the root.
        /// </summary>
        public bool OwnToolsInstalled { get; set; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public AgentView? View { get; set; }

        /// <summary>Keys always listed, of every kind; resolved from config at each build, never persisted.</summary>
        public IReadOnlySet<string> Pinned { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>The view revision the gateway and the served contract both match; 0 = none yet.</summary>
        public int AppliedRevision { get; set; }

        /// <summary>The agent's <c>Skill</c> tool, once the host attached it for this build.</summary>
        public SkillSlot? Skill { get; set; }

        /// <summary>A sub-agent's latest spawn while tools are tiered (<see cref="RecordSpawnTool"/>); null before.</summary>
        public SpawnTools? Spawn { get; set; }
    }

    /// <summary>
    /// The tools one spawn of a sub-agent was offered. Filled while the spawn builds its registry, one tool at a time,
    /// and read by that spawn's providers after; the next spawn gets a new instance.
    /// </summary>
    private sealed class SpawnTools
    {
        public HashSet<string> Offered { get; } = new(StringComparer.Ordinal);

        /// <summary>The tier-managed tools among them, by name: the sub-agent's Included tools.</summary>
        public ConcurrentDictionary<string, FunctionContract> Managed { get; } = new(StringComparer.Ordinal);

        /// <summary>True once this spawn was given its own <c>ExecuteTool</c>: only then do its tools rotate.</summary>
        public bool OwnExecuteTool { get; set; }
    }

    /// <summary>
    /// The root's tier tools: its <c>Skill</c> once a handler is attached, <c>SearchSkill</c>, while
    /// <paramref name="agentsTiered"/> <c>SearchAgent</c> and <c>SearchPlugin</c>, and while
    /// <paramref name="toolsTiered"/> <c>SearchTool</c> and <c>ExecuteTool</c>.
    /// </summary>
    private IEnumerable<FunctionDescriptor> RootFunctions(SkillSlot? skill, bool agentsTiered, bool toolsTiered)
    {
        if (skill is { Handler: { } handler })
        {
            yield return new FunctionDescriptor
            {
                Contract = skill.Contract,
                Handler = handler,
                ProviderName = TierProviderName,
            };
        }

        yield return new FunctionDescriptor
        {
            Contract = SearchSkillContract,
            Handler = (args, _, _) => SearchSkillAsync(_rootThreadId, args),
            ProviderName = TierProviderName,
        };
        if (agentsTiered)
        {
            yield return new FunctionDescriptor
            {
                Contract = SearchAgentContract,
                Handler = (args, _, _) => SearchAgentAsync(_rootThreadId, args),
                ProviderName = TierProviderName,
            };
            yield return new FunctionDescriptor
            {
                Contract = SearchPluginContract,
                Handler = (args, _, _) => SearchPluginAsync(_rootThreadId, args),
                ProviderName = TierProviderName,
            };
        }

        if (toolsTiered)
        {
            yield return new FunctionDescriptor
            {
                Contract = SearchToolContract,
                Handler = (args, _, _) => SearchToolAsync(_rootThreadId, args),
                ProviderName = TierProviderName,
            };
            yield return new FunctionDescriptor
            {
                Contract = ExecuteToolContract,
                Handler = ExecuteToolFailsClosed,
                ProviderName = TierProviderName,
            };
        }
    }

    private sealed class RootFunctionProvider(CapabilityTiers owner) : IDeferredToolProvider
    {
        public string ProviderName => TierProviderName;

        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
            owner._active
                ? owner.RootFunctions(
                    owner._agents.TryGetValue(owner._rootThreadId, out var root) ? root.Skill : null,
                    owner._settings is { AgentCap: not null },
                    owner._settings is { ToolCap: not null }
                )
                : [];

        public bool IsAdvertised(string functionName) =>
            !owner._active || IsToolAdvertised(functionName, owner._tools, owner.RootView());

        public ToolCallUnwrap? TryUnwrap(string functionName, string argumentsJson) =>
            owner._active && owner._settings is { ToolCap: not null }
                ? UnwrapExecuteTool(owner._tools, owner.RootView(), functionName, argumentsJson)
                : null;
    }

    private AgentView? RootView() => _agents.TryGetValue(_rootThreadId, out var root) ? root.View : null;

    /// <summary>
    /// False only for a tier-managed tool the view does not list. No managed set (not tiered, no attach) or no view
    /// lists everything: a missing piece never hides a tool.
    /// </summary>
    private static bool IsToolAdvertised(
        string functionName,
        IReadOnlyDictionary<string, FunctionContract>? tools,
        AgentView? view
    ) => tools is null || !tools.ContainsKey(functionName) || view is null || IsListed(view, functionName);

    private static bool IsListed(AgentView? view, string toolName) =>
        view is not null && view.Loaded.Contains(CapabilityCatalog.ToolKeyPrefix + toolName, StringComparer.Ordinal);

    /// <summary>
    /// The <c>ExecuteTool</c> decision (architecture-s234 §3): null for any other call. A refusal (never approved, never
    /// run) for arguments that name no tool, for <c>ExecuteTool</c>/<c>SearchTool</c> themselves, for any name that is
    /// not tier-managed, and for an available tool whose arguments fail its schema (with the errors and the schema).
    /// Otherwise the target with its own arguments: a listed tool runs exactly as a direct call would.
    /// </summary>
    private static ToolCallUnwrap? UnwrapExecuteTool(
        IReadOnlyDictionary<string, FunctionContract>? tools,
        AgentView? view,
        string functionName,
        string argumentsJson
    )
    {
        if (functionName != ExecuteToolToolName)
        {
            return null;
        }

        string tool;
        string? arguments;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("tool", out var named)
                || named.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(named.GetString())
            )
            {
                return Refusal(
                    $"ExecuteTool needs \"tool\": the exact name of the tool to run. {ExecuteToolUsage}",
                    InvalidArgsCode
                );
            }

            tool = named.GetString()!;
            arguments = root.TryGetProperty("arguments", out var given) ? ArgumentsObject(given) : "{}";
        }
        catch (JsonException ex)
        {
            return Refusal(
                $"ExecuteTool arguments are not valid JSON: {ex.Message} {ExecuteToolUsage}",
                InvalidArgsCode
            );
        }

        if (tool is ExecuteToolToolName or SearchToolToolName)
        {
            return Refusal($"Call {tool} directly; it is always in your tool list.", CallDirectlyCode);
        }

        // Not tier-managed for this agent: an always-listed tool, or one it can never get (excluded, or not in its
        // catalog). SearchTool cannot find either, so the refusal does not point there.
        if (tools is null || !tools.TryGetValue(tool, out var contract))
        {
            return Refusal(
                $"{tool} cannot be run through ExecuteTool. If it is in your tool list, call it directly; otherwise it is not available to this agent.",
                ToolNotAvailableCode
            );
        }

        if (arguments is null)
        {
            return Refusal(
                $"\"arguments\" must be an object holding {tool}'s arguments. {ExecuteToolUsage}",
                InvalidArgsCode
            );
        }

        if (!IsListed(view, tool))
        {
            var validation = new JsonSchemaValidator().ValidateDetailed(arguments, GateSchema(contract).ToJsonString());
            if (!validation.IsValid)
            {
                return ToolCallUnwrap.Refuse(
                    JsonSerializer.Serialize(
                        new
                        {
                            error = $"The arguments do not match {tool}'s schema. Fix them and call ExecuteTool again.",
                            errors = validation.Errors,
                            schema = ShownSchema(contract),
                        },
                        ModelJsonOptions
                    ),
                    InvalidArgsCode
                );
            }
        }

        return ToolCallUnwrap.Run(tool, arguments);
    }

    /// <summary>
    /// The raw JSON of an <c>arguments</c> object. A JSON string holding an object is read once (weak models
    /// double-encode); null is <c>{}</c> (the target's own checks then name what is missing). Null for anything else.
    /// </summary>
    private static string? ArgumentsObject(JsonElement given)
    {
        if (given.ValueKind == JsonValueKind.Object)
        {
            return given.GetRawText();
        }

        if (given.ValueKind == JsonValueKind.Null)
        {
            return "{}";
        }

        if (given.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        try
        {
            using var inner = JsonDocument.Parse(given.GetString()!);
            return inner.RootElement.ValueKind == JsonValueKind.Object ? inner.RootElement.GetRawText() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ToolCallUnwrap Refusal(string error, string code) =>
        ToolCallUnwrap.Refuse(JsonSerializer.Serialize(new { error }, ModelJsonOptions), code);

    /// <summary>
    /// One build's tiers, staged by <see cref="StageAsync"/>. The gateway already holds the staged view and the build
    /// reads the staged <c>Skill</c> and agent id, but the conversation's live tiers are untouched until
    /// <see cref="CommitAsync"/>. Disposing a stage that was never committed discards it.
    /// </summary>
    public sealed class Stage : IDisposable
    {
        internal const string NotConfiguredReason = "not configured";
        internal const string TimedOutReason = "configure timed out";
        internal const string InvalidThreadIdReason = "invalid thread id";
        internal const string CatalogUnavailableReason = "catalog unavailable";
        internal const string NewerSchemaReason = "persisted view has a newer schema";

        private const int Pending = 0;
        private const int Committed = 1;
        private const int Discarded = 2;

        private readonly CapabilityTiers _owner;
        private int _state;

        internal Stage(CapabilityTiers owner)
        {
            _owner = owner;
            RootProvider = new StagedRootProvider(this);
        }

        /// <summary>
        /// True when the build is tiered: the host registers <see cref="RootProvider"/> and attaches the root's
        /// <c>Skill</c> through <see cref="TryAttachRootSkill"/>. False wires tools as without tiers.
        /// </summary>
        public bool IsActive => RootId is not null;

        /// <summary>
        /// The root this build makes has <c>SearchTool</c> and <c>ExecuteTool</c> (tiered, with
        /// <see cref="TierSettings.ToolCap"/> set): the host appends <see cref="ExecuteToolPromptLine"/> to its system
        /// prompt.
        /// </summary>
        public bool OffersExecuteTool => IsActive && Settings is { ToolCap: not null };

        /// <summary>
        /// Plain text for whoever asked for this build when its tiers did not fully take; null for an untiered build
        /// and for one whose PATCH landed. A tiered build that went inert (timed out, catalog unavailable, newer
        /// saved view, invalid thread id) runs untiered and says why; with a failed PATCH, the tiers are live and the
        /// gateway catches up at the next rotation. Names no skill, agent id or gateway error.
        /// </summary>
        public string? SetupWarning =>
            InertReason switch
            {
                null or NotConfiguredReason => IsActive && !Applied
                    ? "Skill tiers could not be applied to the sandbox; they will be retried at the next compaction."
                    : null,
                _ =>
                    $"Skill tiers could not be set up for this mode ({SetupFailureCause(InertReason)}); this conversation runs without tiers until the agent is rebuilt.",
            };

        /// <summary>
        /// The build's <c>Skill</c> and <c>SearchSkill</c>: the staged ones until the commit, the conversation's live
        /// ones after it, and nothing once discarded.
        /// </summary>
        public IFunctionProvider RootProvider { get; }

        internal string? RootId { get; set; }

        internal string? InertReason { get; set; }

        internal CapabilityCatalog? Catalog { get; set; }

        /// <summary>
        /// <see cref="Catalog"/> is the instance that was live at the stage (nothing it indexes changed), not one the
        /// stage built: the commit never swaps it back in over a later agent refresh.
        /// </summary>
        internal bool CatalogReused { get; set; }

        /// <summary>The template snapshot <see cref="Catalog"/>'s agent items came from; null = none indexed.</summary>
        internal IReadOnlyDictionary<string, SubAgentTemplate>? IndexedTemplates { get; set; }

        internal SkillTierGateway? Gateway { get; set; }

        internal TierSettings? Settings { get; set; }

        internal IReadOnlySet<string> Pinned { get; set; } = new HashSet<string>(StringComparer.Ordinal);

        internal AgentView? View { get; set; }

        /// <summary>The view is a fresh seed: the commit persists it.</summary>
        internal bool Seeded { get; set; }

        /// <summary>The staged PATCH succeeded.</summary>
        internal bool Applied { get; set; }

        /// <summary>The root's live view when this stage read the store; another one at commit means a rotation ran.</summary>
        internal AgentView? LiveViewAtStage { get; set; }

        internal SkillSlot? Skill { get; private set; }

        /// <summary>
        /// Takes the root's <c>Skill</c> tool from the build's sandbox listing (made inside <see cref="EnterBuildFlow"/>,
        /// so as the staged agent). False, and takes nothing, for any other tool, while inert or once settled.
        /// </summary>
        public bool TryAttachRootSkill(FunctionContract contract, ToolHandler handler)
        {
            ArgumentNullException.ThrowIfNull(contract);
            ArgumentNullException.ThrowIfNull(handler);
            if (!IsActive || Volatile.Read(ref _state) != Pending || contract.Name != SkillToolName)
            {
                return false;
            }

            Skill = new SkillSlot(contract, handler);
            return true;
        }

        /// <summary>
        /// Hands the stage the tools this build's root sandbox connect registered (their handlers are already in the
        /// registry). With <see cref="TierSettings.ToolCap"/> set, every one but <c>Skill</c> becomes tier-managed: the
        /// root's view gains its <c>tool</c> keys (seeded from the mode once, then checked against these tools) and
        /// <see cref="RootProvider"/> hides the managed tools the view does not list. Call it once, after the connect
        /// and before the build reads its contracts, and only when the connect succeeded: a build that never calls it
        /// hides nothing. No-op while inert, once settled, without <c>ToolCap</c>, or for an empty list.
        /// </summary>
        public void AttachTools(IReadOnlyList<FunctionContract> sandboxTools) => _owner.AttachTools(this, sandboxTools);

        /// <summary>The managed tools <see cref="AttachTools"/> took, by name; null = none.</summary>
        internal IReadOnlyDictionary<string, FunctionContract>? Tools { get; set; }

        internal bool IsPending => Volatile.Read(ref _state) == Pending;

        /// <summary>
        /// Until disposed, <see cref="RootGatewayAgentId"/> on this async flow is the staged id, so the build's own
        /// sandbox listing is served as the agent being built. Every other flow, the live loop's included, keeps the
        /// live id.
        /// </summary>
        public IDisposable EnterBuildFlow()
        {
            var previous = _owner._buildFlow.Value;
            var flow = new BuildFlow(RootId);
            _owner._buildFlow.Value = flow;
            return new BuildFlowExit(_owner, flow, previous);
        }

        /// <summary>
        /// Makes this stage the conversation's tiers: its view, <c>Skill</c>, agent id and settings, persisting a
        /// seeded view. Call it once the build is the conversation's, and only then. An inert stage makes the
        /// conversation inert.
        /// </summary>
        public Task CommitAsync(CancellationToken ct) => _owner.CommitAsync(this, ct);

        /// <summary>Discards the stage unless it was committed.</summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _state, Discarded, Pending) == Pending)
            {
                _owner.DiscardStage(this);
            }
        }

        internal void MakeInert(string reason)
        {
            RootId = null;
            View = null;
            InertReason = reason;
        }

        internal bool TryMarkCommitted() => Interlocked.CompareExchange(ref _state, Committed, Pending) == Pending;

        private static string SetupFailureCause(string reason) =>
            reason switch
            {
                TimedOutReason => "setup timed out",
                CatalogUnavailableReason => "the sandbox catalog was unavailable",
                NewerSchemaReason => "the saved tiers come from a newer version",
                InvalidThreadIdReason => "the thread id is not a valid agent id",
                _ => "setup failed",
            };

        // Pending answers come from the stage: the loop estimates its tool tokens at construction, before the commit.
        private sealed class StagedRootProvider(Stage stage) : IDeferredToolProvider
        {
            public string ProviderName => TierProviderName;

            public int Priority => 100;

            public IEnumerable<FunctionDescriptor> GetFunctions() =>
                Volatile.Read(ref stage._state) switch
                {
                    Committed => stage._owner._rootProvider.GetFunctions(),
                    Pending when stage.IsActive => stage._owner.RootFunctions(
                        stage.Skill,
                        stage.Settings is { AgentCap: not null },
                        stage.Settings is { ToolCap: not null }
                    ),
                    _ => [],
                };

            public bool IsAdvertised(string functionName) =>
                Volatile.Read(ref stage._state) switch
                {
                    Committed => stage._owner._rootProvider.IsAdvertised(functionName),
                    Pending when stage.IsActive => IsToolAdvertised(functionName, stage.Tools, stage.View),
                    _ => true,
                };

            public ToolCallUnwrap? TryUnwrap(string functionName, string argumentsJson) =>
                Volatile.Read(ref stage._state) switch
                {
                    Committed => stage._owner._rootProvider.TryUnwrap(functionName, argumentsJson),
                    Pending when stage.IsActive && stage.Settings is { ToolCap: not null } => UnwrapExecuteTool(
                        stage.Tools,
                        stage.View,
                        functionName,
                        argumentsJson
                    ),
                    _ => null,
                };
        }
    }

    /// <summary>A build's flow; closed when the build leaves it, so a context captured inside it stops reading it.</summary>
    private sealed class BuildFlow(string? rootId)
    {
        public string? RootId { get; } = rootId;

        public bool IsOpen { get; set; } = true;
    }

    private sealed class BuildFlowExit(CapabilityTiers owner, BuildFlow flow, BuildFlow? previous) : IDisposable
    {
        public void Dispose()
        {
            if (!flow.IsOpen)
            {
                return;
            }

            flow.IsOpen = false;
            owner._buildFlow.Value = previous;
        }
    }

    /// <summary>
    /// One tool a sub-agent got in place of the parent's (<see cref="ForInheritedToolAsync"/>): its own <c>Skill</c>,
    /// served from its slot (so a rotation swaps the text) with the parent's handler, a <c>SearchSkill</c> that
    /// marks hits from its own view, or a <c>SearchTool</c> that marks hits from its own view and finds only the
    /// managed tools its <paramref name="spawn"/> inherited. Serves even after the conversation goes inert, so a
    /// running sub-agent never loses a tool it was given.
    /// </summary>
    private sealed class SubAgentTierToolProvider(
        CapabilityTiers owner,
        AgentState agent,
        string toolName,
        ToolHandler parentHandler,
        SpawnTools? spawn
    ) : IFunctionProvider
    {
        public string ProviderName => TierProviderName;

        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions()
        {
            if (toolName == SearchSkillToolName)
            {
                yield return new FunctionDescriptor
                {
                    Contract = SearchSkillContract,
                    Handler = (args, _, _) => owner.SearchSkillAsync(agent.ThreadId, args),
                    ProviderName = ProviderName,
                };
            }
            else if (toolName == SearchToolToolName && spawn is not null)
            {
                yield return new FunctionDescriptor
                {
                    Contract = SearchToolContract,
                    Handler = (args, _, _) => owner.SearchToolAsync(agent.ThreadId, args, spawn.Managed),
                    ProviderName = ProviderName,
                };
            }
            else if (toolName == SkillToolName && agent.Skill is { } skill)
            {
                yield return new FunctionDescriptor
                {
                    Contract = skill.Contract,
                    Handler = parentHandler,
                    ProviderName = ProviderName,
                };
            }
        }
    }

    /// <summary>
    /// A sub-agent's own <c>ExecuteTool</c> (<see cref="ForInheritedToolAsync"/>), and so its registry's deferred
    /// provider: the sub-agent's request lists only the managed tools its <paramref name="spawn"/> inherited that its
    /// own view lists, and its loop unwraps <c>ExecuteTool</c> against those tools by the root's rules. A tool its spawn
    /// did not inherit is refused <c>tool_not_available</c>. The parent's view is never read. Serves even after the
    /// conversation goes inert, like <see cref="SubAgentTierToolProvider"/>.
    /// </summary>
    private sealed class SubAgentExecuteToolProvider(AgentState agent, SpawnTools spawn) : IDeferredToolProvider
    {
        public string ProviderName => TierProviderName;

        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions()
        {
            yield return new FunctionDescriptor
            {
                Contract = ExecuteToolContract,
                Handler = ExecuteToolFailsClosed,
                ProviderName = ProviderName,
            };
        }

        public bool IsAdvertised(string functionName) => IsToolAdvertised(functionName, spawn.Managed, agent.View);

        public ToolCallUnwrap? TryUnwrap(string functionName, string argumentsJson) =>
            UnwrapExecuteTool(spawn.Managed, agent.View, functionName, argumentsJson);
    }
}
