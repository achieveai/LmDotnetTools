using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
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
public sealed record TierSettings(
    IReadOnlyList<string> Loaded,
    IReadOnlyList<string> Pinned,
    IReadOnlyList<string> SubAgentLoaded,
    IReadOnlyList<string> SubAgentPinned,
    int SkillCap = 10
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
/// each kind capped by its most recent uses. Persisted first, then applied; an unchanged view sends nothing.
/// </para>
/// </remarks>
public sealed class CapabilityTiers : IDisposable, IAsyncDisposable
{
    /// <summary>The thread-metadata key holding an agent's persisted view.</summary>
    public const string ViewPropertyKey = "capabilities.view";

    /// <summary>The tool name of the search tool.</summary>
    public const string SearchSkillToolName = "SearchSkill";

    /// <summary>The tool name of the gateway's skill tool.</summary>
    public const string SkillToolName = "Skill";

    private const string RootAgentKey = "root";
    private const string TierProviderName = "SkillTiers";
    private const int DefaultSearchLimit = 5;
    private const int MaxSearchLimit = 20;
    private const int MaxDescriptionLength = 200;

    // A rotation runs inside the compacting loop's request path; it never holds that path longer than this.
    private static readonly TimeSpan RotationBudget = TimeSpan.FromSeconds(10);

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

    private volatile CapabilityCatalog? _catalog;
    private volatile SkillTierGateway? _gateway;
    private volatile TierSettings? _settings;
    private volatile string? _rootGatewayAgentId;
    private volatile bool _active;
    private int _skillCap = int.MaxValue;

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

        CapabilityCatalog catalog;
        try
        {
            catalog = CandidateCatalog(await gateway.GetCatalog(ct).ConfigureAwait(false));
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
        // resolves, and a rotation always keeps it.
        var pinned = Resolve(RootAgentKey, catalog, settings.Pinned).ToHashSet(StringComparer.Ordinal);
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

            AdoptCatalog(stage.Catalog!);
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

            if (!_active)
            {
                _logger.LogInformation(
                    "Capabilities active {RootThreadId}: the root agent now uses gateway agent id {GatewayAgentId}",
                    _rootThreadId,
                    rootId
                );
            }

            root.Pinned = stage.Pinned;
            root.View = view;
            root.GatewayAgentId = rootId;
            root.AppliedRevision = stage.Applied && !raced ? view.Revision : 0;
            _gateway = stage.Gateway;
            _settings = settings;
            _skillCap = settings.SkillCap;
            _rootGatewayAgentId = rootId;
            _active = true;
            if (stage.Seeded)
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

    /// <summary>
    /// <c>SubAgentOptions.InheritedToolOverride</c>: gives a spawning sub-agent its OWN <c>Skill</c> (the gateway's
    /// text for its own view, served with the parent's handler) and a <c>SearchSkill</c> that marks hits from its
    /// own view. The sub-agent's first such tool loads its view from its own thread (else seeds it from the
    /// mode's <c>subAgent</c> sets, revision 1, persisted), PATCHes its gateway id and lists <c>Skill</c> again as
    /// that sub-agent. Null for any other tool, while inert, and on any failure or after
    /// <see cref="SpawnBudget"/> (logged once): the sub-agent then inherits the parent's tool exactly as before,
    /// and its other tool falls back at once in the same build. A sub-agent left on the parent's tools is not
    /// rotated at its compactions until a later spawn installs its own. Only the caller's cancellation propagates.
    /// </summary>
    public async ValueTask<IFunctionProvider?> ForInheritedToolAsync(InheritedTool tool, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (_disposed || tool.Contract.Name is not (SkillToolName or SearchSkillToolName))
        {
            return null;
        }

        // Depth 1: the sub-agent's thread hangs off the root thread, and its gateway id is the one its own calls
        // present (SandboxSubAgentIdentity.ForCurrentFlow under its actor scope).
        var threadId = SubAgentThreadIds.For(_rootThreadId, tool.AgentId);

        // This spawn builds a new registry: until a tier tool is handed out below, it holds the parent's tools.
        if (_agents.TryGetValue(threadId, out var known))
        {
            known.OwnToolsInstalled = false;
        }

        if (!_active)
        {
            return null;
        }

        var agent = _agents.GetOrAdd(
            threadId,
            _ => new AgentState(tool.AgentId, threadId)
            {
                GatewayAgentId = SandboxSubAgentIdentity.ForSubAgent(_rootThreadId, tool.AgentId),
            }
        );

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
        return new SubAgentTierToolProvider(this, agent, tool.Contract.Name, tool.Handler);
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

        agent.Pinned = Resolve(agent.Key, catalog, settings.SubAgentPinned).ToHashSet(StringComparer.Ordinal);
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
        foreach (var agent in _agents.Values)
        {
            agent.Gate.Dispose();
        }
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
        _rootGatewayAgentId = null;
    }

    /// <summary>The live catalog when the gateway's is unchanged, else a new one that only a commit adopts.</summary>
    private CapabilityCatalog CandidateCatalog(SandboxSkillCatalog gatewayCatalog)
    {
        var items = CapabilityCatalog.FromSkills(gatewayCatalog);
        var current = _catalog;
        return current is not null && current.Items.SequenceEqual(items) ? current : CapabilityCatalog.Build(items);
    }

    private void AdoptCatalog(CapabilityCatalog catalog)
    {
        var current = _catalog;
        if (ReferenceEquals(current, catalog))
        {
            return;
        }

        _catalog = catalog;
        // A search already holding the old instance gets ObjectDisposedException and retries on this one.
        current?.Dispose();
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
            var known = persisted.Loaded.Where(k => catalog.TryGet(k, out _)).ToList();
            foreach (var dropped in persisted.Loaded.Except(known, StringComparer.Ordinal))
            {
                _logger.LogWarning(
                    "Capabilities view of {AgentKey} names {Key}, which the sandbox no longer offers; dropped",
                    agent.Key,
                    dropped
                );
            }

            return (new AgentView(Union(known, pinned), persisted.Revision, persisted.RotatedThroughSeq), false);
        }

        var loaded = Union(Resolve(agent.Key, catalog, loadedSelectors), pinned);
        return (new AgentView(loaded, 1, 0), true);
    }

    private List<string> Resolve(string agentKey, CapabilityCatalog catalog, IReadOnlyList<string> selectors)
    {
        var issues = new List<SelectorIssue>();
        var keys = catalog.ResolveSelectors(selectors, issues);
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
            new PersistedView(PersistedView.CurrentSchemaVersion, view.Loaded, view.Revision, view.RotatedThroughSeq)
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
    /// text was swapped), so the loop re-estimates its tool tokens; false when inert, when the view did not
    /// change, for a sub-agent serving the parent's tools (nothing rotates), or when anything failed (logged,
    /// retried at the next activation or build). Only the caller's
    /// cancellation propagates.
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
        if (
            !_active
            || agent.View is not { } view
            || _gateway is not { } gateway
            || _catalog is not { } catalog
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
            if (seq > view.RotatedThroughSeq && UsedKey(agent, call, catalog) is { } key)
            {
                uses.Add((key, seq));
            }
        }

        var loaded = Rotate(agent.Pinned, uses, kind => kind == CapabilityCatalog.SkillKind ? _skillCap : int.MaxValue);
        var changed = !loaded.SequenceEqual(view.Loaded, StringComparer.Ordinal);
        var next = new AgentView(loaded, changed ? view.Revision + 1 : view.Revision, activation.CheckpointSeq);
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
            return false; // the old contract stays; the next activation or build tries again
        }

        if (agent.Skill is not { } slot)
        {
            agent.AppliedRevision = next.Revision; // this build has no Skill tool to swap
            return false;
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

    /// <summary>A key's kind is its prefix: <c>skill:</c>, later <c>agent:</c> and <c>tool:</c>.</summary>
    private static string KindOf(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? key[..colon] : key;
    }

    // The one place a tool call becomes a use: a switch on the tool name, so each kind's step adds its case here.
    private string? UsedKey(AgentState agent, ToolCall call, CapabilityCatalog catalog) =>
        call.FunctionName switch
        {
            SkillToolName => SkillKeyOf(agent, call.FunctionArgs, catalog),
            _ => null, // SearchSkill hits and every other tool never count as a use
        };

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
        Task.FromResult<ToolHandlerResult>(SearchSkill(viewThreadId, argsJson));

    /// <summary>One search, its hits marked loaded or available from the view of the agent on <paramref name="viewThreadId"/>.</summary>
    private ToolHandlerResult.Resolved SearchSkill(string viewThreadId, string argsJson)
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
                return MissingQuery();
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
            return MissingQuery();
        }

        // The caller's own actor when there is one: a sub-agent that inherited the root's SearchSkill logs as itself.
        var agentKey =
            AgentActorScope.Current?.AgentId
            ?? (_agents.TryGetValue(viewThreadId, out var owner) ? owner.Key : RootAgentKey);
        var catalog =
            _catalog
            ?? throw new InvalidOperationException("SearchSkill ran before this conversation loaded a skill catalog.");
        IReadOnlyList<CapabilityItem> hits;
        try
        {
            hits = catalog.Search(query, CapabilityCatalog.SkillKind, limit);
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
            hits = catalog.Search(query, CapabilityCatalog.SkillKind, limit);
        }

        // The query may carry user data: Information keeps its length only, and the text goes to Debug.
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
            hits.Select(h => h.Key).ToArray()
        );

        return ToolHandlerResult.FromText(FormatHits(viewThreadId, query, hits));
    }

    private static ToolHandlerResult.Resolved MissingQuery() =>
        ToolHandlerResult.FromError("SearchSkill needs a non-empty \"query\".", "invalid_args");

    private string FormatHits(string viewThreadId, string query, IReadOnlyList<CapabilityItem> hits)
    {
        if (hits.Count == 0)
        {
            return $"No skills matched \"{query}\". Try different words for what the task is about "
                + "(a tool, file type or activity), or use a skill already listed in the Skill tool.";
        }

        var listed =
            _agents.TryGetValue(viewThreadId, out var agent) && agent.View is { } view
                ? view.Loaded.ToHashSet(StringComparer.Ordinal)
                : [];
        // '\n', never AppendLine: the model reads the same bytes whatever OS the host runs on.
        var text = new StringBuilder();
        _ = text.Append(hits.Count).Append(hits.Count == 1 ? " skill" : " skills").Append(" found.\n");
        _ = text.Append(
            "\"loaded\" = already listed in your Skill tool; \"available\" = not listed, but loads the same way.\n"
        );
        for (var i = 0; i < hits.Count; i++)
        {
            var hit = hits[i];
            _ = text.Append('\n')
                .Append(i + 1)
                .Append(". ")
                .Append(hit.Name)
                .Append(" (plugin: ")
                .Append(hit.Plugin ?? "workspace")
                .Append(") [")
                .Append(listed.Contains(hit.Key) ? "loaded" : "available")
                .Append("]\n");
            _ = text.Append("   ").Append(OneLine(hit.Description)).Append('\n');
            // The gateway name is unique (it qualifies clashes as mkt:plugin:skill) and is the only form Skill accepts.
            _ = text.Append("   Load: Skill(skill=\"").Append(hit.Name).Append("\")\n");
        }

        return text.ToString().TrimEnd();
    }

    private static string OneLine(string description)
    {
        var flat = string.Join(' ', description.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= MaxDescriptionLength ? flat : flat[..(MaxDescriptionLength - 3)] + "...";
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

    /// <summary>An agent's listed skills (catalog keys, sorted), and its persisted counters.</summary>
    internal sealed record AgentView(IReadOnlyList<string> Loaded, int Revision, long RotatedThroughSeq);

    private sealed record PersistedView(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("loaded")] IReadOnlyList<string> Loaded,
        [property: JsonPropertyName("revision")] int Revision,
        // The checkpoint row seq the last rotation counted through: the next window starts after it.
        [property: JsonPropertyName("rotatedThroughSeq")] long RotatedThroughSeq
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
    }

    /// <summary>The root's tier tools: its <c>Skill</c> once a handler is attached, and <c>SearchSkill</c>.</summary>
    private IEnumerable<FunctionDescriptor> RootFunctions(SkillSlot? skill)
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
    }

    private sealed class RootFunctionProvider(CapabilityTiers owner) : IFunctionProvider
    {
        public string ProviderName => TierProviderName;

        public int Priority => 100;

        public IEnumerable<FunctionDescriptor> GetFunctions() =>
            owner._active
                ? owner.RootFunctions(owner._agents.TryGetValue(owner._rootThreadId, out var root) ? root.Skill : null)
                : [];
    }

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
                    $"Skill tiers could not be set up for this mode ({SetupFailureCause(InertReason)}); this conversation runs without tiers until its next mode switch.",
            };

        /// <summary>
        /// The build's <c>Skill</c> and <c>SearchSkill</c>: the staged ones until the commit, the conversation's live
        /// ones after it, and nothing once discarded.
        /// </summary>
        public IFunctionProvider RootProvider { get; }

        internal string? RootId { get; set; }

        internal string? InertReason { get; set; }

        internal CapabilityCatalog? Catalog { get; set; }

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

        private sealed class StagedRootProvider(Stage stage) : IFunctionProvider
        {
            public string ProviderName => TierProviderName;

            public int Priority => 100;

            public IEnumerable<FunctionDescriptor> GetFunctions() =>
                Volatile.Read(ref stage._state) switch
                {
                    Committed => stage._owner._rootProvider.GetFunctions(),
                    Pending when stage.IsActive => stage._owner.RootFunctions(stage.Skill),
                    _ => [],
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
    /// served from its slot (so a rotation swaps the text) with the parent's handler, or a <c>SearchSkill</c> that
    /// marks hits from its own view. Serves even after the conversation goes inert, so a running sub-agent never
    /// loses a tool it was given.
    /// </summary>
    private sealed class SubAgentTierToolProvider(
        CapabilityTiers owner,
        AgentState agent,
        string toolName,
        ToolHandler parentHandler
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
            else if (agent.Skill is { } skill)
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
}
