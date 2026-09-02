using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Recovery;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using Microsoft.Extensions.Logging;
// LmLifecycle carries its own copy of these constants because it is a wire contract that depends on
// no other project here. The values the loop actually reads come from the approval gate, so bind to
// LmCore's definition rather than letting the two collide on the bare name.
using ApprovalOutcomes = AchieveAi.LmDotnetTools.LmCore.Approval.ToolApprovalOutcomes;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Multi-turn agent implementation using raw LLM APIs with middleware pipeline.
/// Thread-safe for concurrent input via SendAsync.
/// Supports multiple independent output subscribers via SubscribeAsync.
/// Supports deferred tool execution: a tool handler may return
/// <see cref="ToolHandlerResult.Deferred"/>, in which case the loop records a placeholder
/// in history, ends the run, and waits for an external caller to invoke
/// <see cref="ResolveToolCallAsync"/>. When all deferrals from the latest turn resolve,
/// a new run starts automatically.
/// </summary>
/// <remarks>
/// This implementation uses a middleware stack for message processing:
/// - MessageTransformationMiddleware (assigns messageOrderIdx, handles aggregates)
/// - JsonFragmentUpdateMiddleware (handles JSON fragment updates)
/// - MessagePublishingMiddleware (publishes ALL messages to subscribers - updates + full)
/// - MessageUpdateJoinerMiddleware (joins update messages into full messages for history)
/// - ToolCallInjectionMiddleware (injects function contracts for tool calling)
/// </remarks>
public sealed class MultiTurnAgentLoop : MultiTurnAgentBase, ISubAgentContextSink, ISpawnSuppressingAgent
{
    private readonly IStreamingAgent _agent;
    private readonly IDictionary<string, ToolHandler> _toolHandlers;

    // Per-generation context observation (#681). The ordinal is loop-local and monotonic across restarts:
    // seeded lazily from the persisted latest observation on the first generation after a restart, then
    // advanced in memory. Only the run loop's single sequential turn path touches it; the latest
    // observation is read by hosts from other threads, hence the volatile accessor.
    private long _generationOrdinal;
    private bool _generationOrdinalSeeded;
    private ContextObservation? _latestContextObservation;

    /// <summary>
    /// Names of the tools that declare at least one required parameter, snapshot at construction from
    /// the same registry the handlers came from. Consulted by the tool-dispatch guard in
    /// <see cref="ExecuteToolCallAsync"/> so an empty argument payload is rejected only for tools that
    /// actually need arguments — a genuinely parameterless tool called with empty args still runs.
    /// Ordinal, matching the handler-dictionary lookup.
    /// </summary>
    private readonly HashSet<string> _functionsRequiringArgs;

    /// <summary>The sub-agent manager for this loop, or null when no sub-agent options were supplied.
    /// Exposed so a host-side trigger source (e.g. the sample's subagent-completion source) can observe
    /// sub-agent completions; the manager itself is still owned and disposed by the loop.</summary>
    public SubAgentManager? SubAgentManager { get; }

    /// <summary>The sub-agent tool provider registered on this loop, or null when no sub-agent options
    /// were supplied. Exposed so a host can suppress new child creation for one run while retaining
    /// messaging and result access to children that already exist.</summary>
    public SubAgentToolProvider? SubAgentTools { get; }

    /// <inheritdoc />
    public override bool EnforcesSpawnSuppression => true;

    /// <summary>
    /// This loop's handle on the hierarchy-wide collaboration, or null when the host did not enable
    /// one. Null is the historical configuration and keeps every collaboration surface off.
    /// </summary>
    public AgentCollaborationSetup? Collaboration { get; }

    /// <summary>
    ///     This loop's own usage sink (its <c>UsageLedger</c>), or null when usage accounting is disabled.
    ///     Exposed so a host can fold an out-of-band descendant loop's usage into this conversation's total.
    /// </summary>
    public IUsageSink? UsageSink => UsageLedger;

    /// <summary>
    /// The loop's most recent context observation (#681): estimated before the current generation was
    /// dispatched, measured once its usage arrived. Null before the first generation. A host reading the
    /// persisted observation reports this one as <i>fresh</i> when the loop is alive to vouch for it.
    /// </summary>
    public ContextObservation? LatestContextObservation => Volatile.Read(ref _latestContextObservation);

    /// <inheritdoc />
    /// <remarks>
    /// Delegates to <see cref="SubAgentManager.TryDeliverToRunningAsync"/>. A loop with no
    /// sub-agent manager owns no sub-agents, so every id is <see cref="SubAgentContextDeliveryResult.NotOwned"/>.
    /// </remarks>
    public Task<SubAgentContextDeliveryResult> TryDeliverContextAsync(
        string agentId,
        IReadOnlyList<IMessage> messages,
        CancellationToken cancellationToken = default
    ) =>
        SubAgentManager?.TryDeliverToRunningAsync(agentId, messages, cancellationToken)
        ?? Task.FromResult(SubAgentContextDeliveryResult.NotOwned);

    /// <summary>
    /// The names of every tool registered on this loop after the middleware stack was built —
    /// the parent's own tools plus any Agent/Wait tools the loop self-registered. Read-only; the
    /// order is unspecified. Internal diagnostic/test accessor used to assert a loop's effective
    /// tool surface (e.g. the workflow controller's restricted set).
    /// </summary>
    internal IReadOnlyCollection<string> RegisteredToolNames => [.. _toolHandlers.Keys];

    // Owns the Wait/trigger lifecycle when trigger options are supplied. Null otherwise.
    private readonly TriggerRuntime? _triggerRuntime;

    // Resolved root delivery target for a descendant's parked AskUserQuestion (#246). Always non-null
    // after construction — see the ctor's descendantQuestionSink resolution.
    private readonly Func<NotifyMessage, CancellationToken, ValueTask> _descendantQuestionSink;

    // Everything about tool calls that deferred: what is outstanding, which run parked on it, and
    // which resolved results are waiting to be run as child runs. See DelayedResultCoordinator for
    // why this is a collaborator rather than fields here.
    private readonly DelayedResultCoordinator _delayed = new();

    // The just-in-time compaction policy and the view it maintains (#684). Null when the host supplied
    // no CompactionSetup, in which case nothing on the request path changes.
    private readonly CompactionRuntime? _compaction;

    // Guards _wakeScheduled so at most one wake sentinel is ever outstanding on the input channel.
    // The sentinel carries nothing — it exists only to break RunLoopAsync out of its wait so it can
    // drain the coordinator, which is where the actual work is.
    private readonly object _wakeLock = new();
    private bool _wakeScheduled;

    // The requesting run whose delayed continuation must retain a prior no-spawn guarantee.
    // The coordinator owns delayed-result state; this lock owns only the suppression marker.
    private readonly object _spawnSuppressionLock = new();
    private string? _spawnSuppressedRunId;

    private const string SpawnSuppressedRunIdProperty = "spawn_suppressed_run_id";

    // Automatic stream recovery is budgeted per LOGICAL INPUT, but a logical input does not always
    // fit inside one run: a turn that parks on a deferred tool call resumes in a CHILD run, and a
    // child run's turn counter — recovery budget included — is otherwise born at zero. Without this,
    // "at most one automatic recovery" silently becomes "one per park/resume hop", so a transport
    // that fails on every attempt could be retried once per client-tool round trip forever.
    //
    // The architecture's own identity for a logical input is the ParentRunId chain, and this walks
    // it one hop at a time: a parking run records what it has spent under its own id, and the child
    // that resumes it takes that entry and re-records it under its own. Chains of any depth are
    // therefore covered without ever resolving a root. Entries are written only when a budget has
    // actually been spent — the overwhelmingly common zero case stores nothing — and are removed by
    // the run that inherits them, so this holds at most one entry per parked-and-recovered run.
    //
    // This dictionary is the fast path only, and it is keyed on a run id that a restart does not
    // preserve: a deferred entry rebuilt from history carries no requesting run, so the chain hop
    // above has nothing to look up. A park can easily outlive the process — the whole point of a
    // deferred client tool is that its answer may arrive minutes later, from a human — and an
    // in-memory-only budget is silently REFUNDED by that restart, handing the resumed input a second
    // automatic recovery and a second provider call for a turn that already ran, once per restart,
    // forever. So the park also writes a durable single-slot budget (see
    // PersistRecoveryBudgetMarkerAsync), which the continuation claims when the in-memory chain has
    // nothing to give. One slot is exact: the loop parks one run at a time, and the continuation that
    // resumes a park consumes the slot before another park can write it.
    private readonly object _recoveryBudgetLock = new();
    private readonly Dictionary<string, int> _recoveryBudgetByRunId = [];
    private int _restoredParkRecoveryBudget;

    private const string RecoveryBudgetProperty = "recovery_budget_spent";

    /// <summary>
    /// Framework instruction appended to the request of a turn that is continuing work interrupted
    /// mid-stream. Fixed and content-free: it describes the situation without quoting any part of the
    /// conversation, so it neither leaks transcript content nor varies with it.
    /// </summary>
    internal const string InterruptedTurnContinuationInstruction =
        "The previous response was cut off by a connection failure before it finished. "
        + "Everything already present in this conversation was delivered successfully — including any "
        + "tool calls and their results. Continue from that point: do not repeat work that is already "
        + "here, and do not re-issue a tool call that already has a result.";

    /// <summary>
    /// Creates a new MultiTurnAgentLoop with FunctionRegistry for tool management.
    /// The loop owns the complete middleware stack creation.
    /// </summary>
    /// <param name="providerAgent">The base provider streaming agent (without middleware - the loop builds the stack)</param>
    /// <param name="functionRegistry">The function registry containing tool definitions and handlers</param>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template (ModelId, Temperature, MaxThinkingTokens, etc.)</param>
    /// <param name="maxTurnsPerRun">Maximum turns per run before stopping (default: 50)</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="store">Optional persistence store for conversation state</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="subAgentOptions">Optional sub-agent orchestration configuration</param>
    /// <param name="subAgentTemplateSource">
    ///     Optional mutable template catalog shared with an outer owner (e.g. a sandbox
    ///     session registry that activates discovered subagents mid-session). When null
    ///     and <paramref name="subAgentOptions"/> is provided, the loop wraps
    ///     <c>subAgentOptions.Templates</c> in a fresh, private source.
    /// </param>
    /// <param name="loggerFactory">
    ///     Optional logger factory used to give the internal message pipeline middlewares
    ///     (MessageTransformation, MessageUpdateJoiner) their own category loggers so their
    ///     ordering/de-dup decisions are visible in structured logs. When null they stay silent.
    /// </param>
    /// <param name="persistRunLedger">When true, enables durable run-ledger persistence via <see cref="IRunLedgerStore"/> (requires <paramref name="store"/> to implement it).</param>
    /// <param name="triggerOptions">
    ///     Optional Wait/trigger configuration. When provided, the loop enables the
    ///     <c>Wait</c>/<c>CancelWait</c>/<c>ListWaits</c> tools backed by a <see cref="TriggerRuntime"/>
    ///     (with the built-in one-shot <c>timer</c> source plus any host registrations). When null,
    ///     no wait tools are exposed.
    /// </param>
    /// <param name="pricingResolver">
    ///     Optional public-pricing resolver for conversation-wide usage accounting (#196). When supplied,
    ///     the usage ledger fills an estimated public cost per model; null still captures token totals.
    /// </param>
    /// <param name="externalUsageSink">
    ///     Optional external root sink this loop's usage ledger forwards every record to (#196).
    /// </param>
    /// <param name="lifecycleServices">
    ///     Optional lifecycle observation and tool approval. Null leaves both off.
    /// </param>
    /// <param name="subAgentLifecycleServices">
    ///     Optional lifecycle bundle inherited by spawned sub-agents when it must differ from this loop's
    ///     own bundle. Null uses <paramref name="lifecycleServices"/> after this loop stamps it.
    /// </param>
    /// <param name="collaboration">
    ///     Optional handle on the hierarchy-wide collaboration this loop takes part in. Null leaves
    ///     every collaboration behaviour off.
    /// </param>
    /// <remarks>
    /// This overload is kept byte-for-byte identical to the constructor that shipped on <c>main</c>
    /// before this work — same 20 parameters, same order, same optionality/defaults — so an
    /// already-compiled caller of the packable <c>AchieveAi.LmDotnetTools.LmMultiTurn</c> library
    /// does not hit a <see cref="MissingMethodException"/> after upgrading. It always registers both
    /// <c>AskUserQuestion</c> and <c>NotifyClient</c>, and always routes a descendant's parked
    /// question through this loop's own persist-and-publish path — the only behavior this
    /// constructor ever had. Callers that need to control tool registration or supply a custom
    /// descendant-question sink (e.g. a spawned sub-agent, or a workflow controller loop with no
    /// browser socket of its own) must use the
    /// <see cref="MultiTurnAgentLoop(IStreamingAgent, FunctionRegistry, string, bool, bool, string?, GenerateReplyOptions?, int, int, int, IConversationStore?, ILogger{MultiTurnAgentLoop}?, SubAgentOptions?, MutableSubAgentTemplateSource?, ILoggerFactory?, bool, TriggerOptions?, IPricingResolver?, IUsageSink?, MultiTurnLifecycleServices?, MultiTurnLifecycleServices?, AgentCollaborationSetup?, Func{NotifyMessage, CancellationToken, ValueTask}?, CompactionSetup?)"/>
    /// overload instead. Both route through the same implementation.
    /// </remarks>
    public MultiTurnAgentLoop(
        IStreamingAgent providerAgent,
        FunctionRegistry functionRegistry,
        string threadId,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int maxTurnsPerRun = 50,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger<MultiTurnAgentLoop>? logger = null,
        SubAgentOptions? subAgentOptions = null,
        MutableSubAgentTemplateSource? subAgentTemplateSource = null,
        ILoggerFactory? loggerFactory = null,
        bool persistRunLedger = false,
        TriggerOptions? triggerOptions = null,
        IPricingResolver? pricingResolver = null,
        IUsageSink? externalUsageSink = null,
        MultiTurnLifecycleServices? lifecycleServices = null,
        MultiTurnLifecycleServices? subAgentLifecycleServices = null,
        AgentCollaborationSetup? collaboration = null
    )
        : this(
            providerAgent,
            functionRegistry,
            threadId,
            includeAskUserQuestionTool: true,
            includeNotifyClientTool: true,
            systemPrompt: systemPrompt,
            defaultOptions: defaultOptions,
            maxTurnsPerRun: maxTurnsPerRun,
            inputChannelCapacity: inputChannelCapacity,
            outputChannelCapacity: outputChannelCapacity,
            store: store,
            logger: logger,
            subAgentOptions: subAgentOptions,
            subAgentTemplateSource: subAgentTemplateSource,
            loggerFactory: loggerFactory,
            persistRunLedger: persistRunLedger,
            triggerOptions: triggerOptions,
            pricingResolver: pricingResolver,
            externalUsageSink: externalUsageSink,
            lifecycleServices: lifecycleServices,
            subAgentLifecycleServices: subAgentLifecycleServices,
            collaboration: collaboration,
            descendantQuestionSink: null
        ) { }

    /// <summary>
    /// Creates a new MultiTurnAgentLoop with FunctionRegistry for tool management, with explicit
    /// control over whether the browser-hosted client tools (#246) are registered. This is the
    /// designated constructor; every other overload forwards here.
    /// </summary>
    /// <param name="providerAgent">The base provider streaming agent (without middleware - the loop builds the stack)</param>
    /// <param name="functionRegistry">The function registry containing tool definitions and handlers</param>
    /// <param name="threadId">Unique identifier for this conversation thread</param>
    /// <param name="includeAskUserQuestionTool">
    ///     Whether this loop has a browser result channel capable of resolving <c>AskUserQuestion</c>.
    /// </param>
    /// <param name="includeNotifyClientTool">
    ///     Whether this loop has a browser subscriber capable of receiving <c>NotifyClient</c>.
    /// </param>
    /// <param name="systemPrompt">System prompt for the agent (persists across all runs)</param>
    /// <param name="defaultOptions">Default GenerateReplyOptions template (ModelId, Temperature, MaxThinkingTokens, etc.)</param>
    /// <param name="maxTurnsPerRun">Maximum turns per run before stopping (default: 50)</param>
    /// <param name="inputChannelCapacity">Capacity of the input queue (default: 100)</param>
    /// <param name="outputChannelCapacity">Capacity per subscriber output channel (default: 1000)</param>
    /// <param name="store">Optional persistence store for conversation state</param>
    /// <param name="logger">Optional logger</param>
    /// <param name="subAgentOptions">Optional sub-agent orchestration configuration</param>
    /// <param name="subAgentTemplateSource">
    ///     Optional mutable template catalog shared with an outer owner (e.g. a sandbox
    ///     session registry that activates discovered subagents mid-session). When null
    ///     and <paramref name="subAgentOptions"/> is provided, the loop wraps
    ///     <c>subAgentOptions.Templates</c> in a fresh, private source.
    /// </param>
    /// <param name="loggerFactory">
    ///     Optional logger factory used to give the internal message pipeline middlewares
    ///     (MessageTransformation, MessageUpdateJoiner) their own category loggers so their
    ///     ordering/de-dup decisions are visible in structured logs. When null they stay silent.
    /// </param>
    /// <param name="persistRunLedger">When true, enables durable run-ledger persistence via <see cref="IRunLedgerStore"/> (requires <paramref name="store"/> to implement it).</param>
    /// <param name="triggerOptions">
    ///     Optional Wait/trigger configuration. When provided, the loop enables the
    ///     <c>Wait</c>/<c>CancelWait</c>/<c>ListWaits</c> tools backed by a <see cref="TriggerRuntime"/>
    ///     (with the built-in one-shot <c>timer</c> source plus any host registrations). When null,
    ///     no wait tools are exposed.
    /// </param>
    /// <param name="pricingResolver">
    ///     Optional public-pricing resolver for conversation-wide usage accounting (#196). When supplied,
    ///     the usage ledger fills an estimated public cost per model; null still captures token totals.
    /// </param>
    /// <param name="externalUsageSink">
    ///     Optional external root sink this loop's usage ledger forwards every record to (#196).
    /// </param>
    /// <param name="lifecycleServices">
    ///     Optional lifecycle observation and tool approval. Null leaves both off.
    /// </param>
    /// <param name="subAgentLifecycleServices">
    ///     Optional lifecycle bundle inherited by spawned sub-agents when it must differ from this loop's
    ///     own bundle. Null uses <paramref name="lifecycleServices"/> after this loop stamps it.
    /// </param>
    /// <param name="collaboration">
    ///     Optional handle on the hierarchy-wide collaboration this loop takes part in. Null leaves
    ///     every collaboration behaviour off.
    /// </param>
    /// <param name="descendantQuestionSink">
    ///     Optional root-conversation delivery target for a descendant's parked <c>AskUserQuestion</c>.
    ///     Null resolves to this loop's own persist-and-publish path.
    /// </param>
    /// <param name="compaction">
    ///     Optional just-in-time compaction setup (#684, spec 679 §5). Null leaves the request path
    ///     exactly as it was: no policy pass, no recall tool, no observation. Spawned sub-agents
    ///     inherit it through <see cref="SubAgentOptions.Compaction"/>.
    /// </param>
    public MultiTurnAgentLoop(
        IStreamingAgent providerAgent,
        FunctionRegistry functionRegistry,
        string threadId,
        bool includeAskUserQuestionTool,
        bool includeNotifyClientTool,
        string? systemPrompt = null,
        GenerateReplyOptions? defaultOptions = null,
        int maxTurnsPerRun = 50,
        int inputChannelCapacity = 100,
        int outputChannelCapacity = 1000,
        IConversationStore? store = null,
        ILogger<MultiTurnAgentLoop>? logger = null,
        SubAgentOptions? subAgentOptions = null,
        MutableSubAgentTemplateSource? subAgentTemplateSource = null,
        ILoggerFactory? loggerFactory = null,
        bool persistRunLedger = false,
        TriggerOptions? triggerOptions = null,
        IPricingResolver? pricingResolver = null,
        IUsageSink? externalUsageSink = null,
        MultiTurnLifecycleServices? lifecycleServices = null,
        MultiTurnLifecycleServices? subAgentLifecycleServices = null,
        AgentCollaborationSetup? collaboration = null,
        Func<NotifyMessage, CancellationToken, ValueTask>? descendantQuestionSink = null,
        CompactionSetup? compaction = null
    )
        : base(
            threadId,
            systemPrompt,
            defaultOptions,
            maxTurnsPerRun,
            inputChannelCapacity,
            outputChannelCapacity,
            store,
            logger,
            persistRunLedger: persistRunLedger,
            lifecycleServices: MultiTurnLifecycleServices.ForAgent(
                lifecycleServices,
                LifecycleAgentKinds.Raw,
                defaultOptions?.ModelId
            )
        )
    {
        ArgumentNullException.ThrowIfNull(providerAgent);
        ArgumentNullException.ThrowIfNull(functionRegistry);

        // Conversation-wide usage accounting (#196): one ledger per root conversation, shared below with
        // the SubAgentManager so the primary loop's own usage and every descendant's usage accumulate
        // into a single root total. Usage is captured in MultiTurnAgentBase.AddToHistory. When an external
        // sink is supplied (a nested-root loop, e.g. a workflow controller), the ledger also forwards each
        // record there so this whole subtree folds into the parent conversation's total.
        UsageLedger = new UsageLedger(
            threadId,
            pricingResolver,
            forwardTo: externalUsageSink,
            onAggregateUpdated: PublishUsageAggregateFrame
        );

        Collaboration = collaboration;

        // Self-registration is idempotent by design: a spawned sub-agent was already registered by its
        // parent's manager, so only an unregistered root publishes itself here.
        if (collaboration is not null && collaboration.Directory.FindById(collaboration.AgentId) is null)
        {
            _ = collaboration.Directory.TryRegister(
                collaboration.Context,
                collaboration.Name,
                AgentCollaborationStatuses.Running,
                new AgentLoopWriteEndpoint(this)
            );
        }

        _descendantQuestionSink = descendantQuestionSink ?? DeliverClientNotificationAsync;

        // Just-in-time compaction (#684). The runtime holds no reference to this loop — every fact it
        // needs is a delegate — and it is built before the inheritable-tool snapshot below so the recall
        // tool it names can be registered after that snapshot (a child registers its own instance).
        _compaction = compaction is null
            ? null
            : new CompactionRuntime(
                compaction,
                new CompactionRuntimeHost
                {
                    ThreadId = threadId,
                    SystemPrompt = systemPrompt,
                    Store = store,
                    RunLedgerStore = RunLedgerStore,
                    DefaultOptions = DefaultOptions,
                    Pricing = pricingResolver,
                    AgentId = LifecycleServices.Lineage.SubAgentId ?? "root",
                    HistorySnapshot = GetHistorySnapshot,
                    OwedContinuations = () => _delayed.PendingCauseCount,
                    LiveDeferredCount = () => _delayed.IsEmpty ? 0 : 1,
                    Roster = RosterForCompaction,
                    AppendInMemory = message => RestoreHistory([message]),
                    RecordSummaryUsage = RecordCompactionUsage,
                    Lifecycle = Lifecycle,
                    Logger = Logger,
                },
                providerAgent
            );

        // Client-facing tools register before the sub-agent inheritable-tool snapshot. Each descendant
        // constructs its own correctly-scoped provider instances when the owning host enables them.
        if (includeAskUserQuestionTool)
        {
            _ = functionRegistry.AddProvider(new AskUserQuestionToolProvider());
        }

        if (includeNotifyClientTool)
        {
            _ = functionRegistry.AddProvider(new NotifyClientToolProvider(DeliverClientNotificationAsync));
        }

        // When sub-agent orchestration is configured, snapshot the current tools
        // and register Agent/CheckAgent tools before building the middleware stack.
        if (subAgentOptions != null)
        {
            // IMPORTANT: Snapshot parent tools BEFORE registering sub-agent tools.
            // This ensures sub-agents inherit the parent's domain tools but NOT the
            // Agent/CheckAgent tools, preventing unbounded recursive delegation.
            var (contracts, handlers) = functionRegistry.Build();

            // Additionally drop any host-declared non-inherited tools (e.g. StartWorkflowAgent/
            // CheckWorkflow/WaitWorkflow) from the snapshot handed to sub-agents. Unlike the
            // Agent-family tools — excluded structurally because they're registered AFTER this
            // snapshot — these are registered on the parent's own registry BEFORE the loop is
            // built, so they're already in the snapshot and would otherwise be inherited by a
            // sub-agent whose template sets EnabledTools = null. Filtering the snapshot copy does
            // not touch the parent's own tool set (built from the full registry below).
            var inheritableContracts = FilterInheritableContracts(contracts, subAgentOptions.NonInheritedToolNames)
                .ToList();

            // Transparency seam (WorkflowAgent): a nested-root loop — a workflow controller — runs on
            // its own isolated, workflow-only registry, yet its delegate sub-agents must inherit the
            // tools of the first non-WorkflowAgent ancestor (the launching conversation). Those
            // ancestor tools arrive via ExternalInheritableTools and are merged into the snapshot
            // handed to THIS loop's sub-agents. The loop's OWN advertised tools (built from the full
            // registry below) are untouched, so the controller surface stays workflow-only. Skip any
            // name excluded from inheritance or already present, so an external tool can never shadow
            // a control-plane tool.
            if (subAgentOptions.ExternalInheritableTools is { } externalTools)
            {
                var excluded = subAgentOptions.NonInheritedToolNames is { } names
                    ? new HashSet<string>(names, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                var present = new HashSet<string>(inheritableContracts.Select(c => c.Name), StringComparer.Ordinal);
                var mergedHandlers = new Dictionary<string, ToolHandler>(handlers);
                var beforeMerge = inheritableContracts.Count;

                foreach (var contract in externalTools.Contracts)
                {
                    if (
                        excluded.Contains(contract.Name)
                        || present.Contains(contract.Name)
                        || !externalTools.Handlers.TryGetValue(contract.Name, out var handler)
                    )
                    {
                        continue;
                    }

                    inheritableContracts.Add(contract);
                    mergedHandlers[contract.Name] = handler;
                    _ = present.Add(contract.Name);
                }

                handlers = mergedHandlers;

                // Observability (content-free: counts only, no task/prompt text): make the transparency
                // merge traceable in the logs so "did the delegate inherit the ancestor's tools?" is
                // answerable from JSONL rather than inferred from the /subagents API.
                var mergedCount = inheritableContracts.Count - beforeMerge;
                logger?.LogDebug(
                    "Merged external inheritable tools into the sub-agent snapshot for {ThreadId}: "
                        + "offered {OfferedCount}, merged {MergedCount}, skipped {SkippedCount}, "
                        + "inheritable total {InheritableTotal}.",
                    threadId,
                    externalTools.Contracts.Count,
                    mergedCount,
                    externalTools.Contracts.Count - mergedCount,
                    inheritableContracts.Count
                );
            }

            // Use the caller-supplied source when present (so an outer owner — typically
            // a sandbox session registry — can activate discovered subagents mid-session
            // by calling TryRegister on it). Otherwise wrap the static template dictionary
            // in a fresh source so behavior matches the previous immutable contract.
            var source = subAgentTemplateSource ?? new MutableSubAgentTemplateSource(subAgentOptions.Templates);

            SubAgentManager = new SubAgentManager(
                parentAgent: this,
                parentContracts: [.. inheritableContracts],
                parentHandlers: handlers,
                // A root that compacts hands the same setup down so every level of the hierarchy runs
                // the policy over its own thread with its own summarizer (see CompactionSetup).
                options: subAgentOptions.Compaction is null && compaction is not null
                    ? subAgentOptions with
                    {
                        Compaction = compaction,
                    }
                    : subAgentOptions,
                source: source,
                logger: logger,
                // Sub-agents whose template/override sets no model inherit the parent's model, so a
                // built-in template doesn't fall back to the provider's stale hardcoded default.
                parentModelId: DefaultOptions.ModelId,
                // Sub-agents whose template sets no budget inherit the parent's effective per-turn output
                // budget (never null here — MultiTurnAgentBase floors it), so a delegate's Write/Bash
                // tool_use JSON isn't truncated by the provider's 4096 default (stop_reason=max_tokens).
                parentMaxToken: DefaultOptions.MaxToken,
                // Share the root ledger so descendant usage folds into the same conversation total (#196).
                usageSink: UsageLedger,
                // Persist immediately on each descendant observation (covers late/background descendants).
                persistUsageAsync: PersistCurrentUsageAsync,
                // Thread the SAME resolved root delivery target straight through so a grandchild's parked
                // question bubbles directly to the true root without an extra relay hop (#246).
                descendantQuestionSink: _descendantQuestionSink,
                // The parent's own wiring, from which each child derives its bundle at spawn time.
                // A sub-agent's events belong in the parent's ordered stream, and a host that gates
                // the parent's tools did not mean to leave the children's ungated. A workflow controller
                // can exempt only its own control-plane tools while explicitly retaining the host bundle
                // for delegates through subAgentLifecycleServices.
                lifecycleServices: subAgentLifecycleServices ?? LifecycleServices,
                // This loop's own collaboration handle, from which the manager derives each child's.
                collaboration: collaboration
            );

            SubAgentTools = new SubAgentToolProvider(SubAgentManager, source, subAgentOptions.ExposedToolNames);

            _ = functionRegistry.AddProvider(SubAgentTools);
        }

        // When trigger options are supplied, stand up the Wait/trigger runtime and register the
        // Wait/CancelWait/ListWaits tools. Registered AFTER the sub-agent snapshot so sub-agents
        // don't inherit the wait surface. The runtime resolves parked block waits through the
        // loop's existing public ResolveToolCallAsync — no new loop API is exposed.
        if (triggerOptions != null)
        {
            _triggerRuntime = new TriggerRuntime(
                triggerOptions,
                resolve: (toolCallId, result, isError, ct) =>
                    ResolveToolCallAsync(toolCallId, result, isError, contentBlocks: null, ct),
                notify: (payload, isError, ct) => EnqueueTriggerNotifyAsync(payload, isError, ct),
                tryNotify: TryEnqueueTriggerNotify,
                logger: logger
            );
            _triggerRuntime.RegisterBuiltIns();
            foreach (var registration in triggerOptions.AdditionalRegistrations)
            {
                _triggerRuntime.Register(registration);
            }

            _ = functionRegistry.AddProvider(new WaitToolProvider(_triggerRuntime));
        }

        // RecallConversation (spec 679 §6): registered after the inheritable snapshot so a child never
        // inherits this loop's instance (each child binds its own over its own thread), and only when
        // compaction is at least in Warn mode. The tool list is static from here on: with no active
        // checkpoint the tool answers nothing_compacted rather than appearing and disappearing.
        if (_compaction is { IsEnabled: true })
        {
            _ = functionRegistry.AddProvider(
                new RecallConversationToolProvider(
                    threadId,
                    store,
                    () => _compaction.ActiveBoundarySeq,
                    _compaction.Options.Recall
                )
            );
        }

        // Build tool call components from registry
        var (toolCallMiddleware, finalHandlers) = functionRegistry.BuildToolCallComponents(name: "MultiTurnAgentTools");
        _toolHandlers = finalHandlers;

        // Snapshot which tools declare a required parameter so the dispatch guard in
        // ExecuteToolCallAsync can reject an empty/truncated argument payload for a tool that needs
        // args (e.g. Write/Bash) while still letting a genuinely parameterless tool run with empty
        // args. Sourced from the same registry the handlers came from, so names line up with
        // _toolHandlers (Build() applies the same collision-renaming BuildToolCallComponents does).
        var (registeredContracts, _) = functionRegistry.Build();
        _functionsRequiringArgs = new HashSet<string>(
            registeredContracts.Where(c => c.Parameters?.Any(p => p.IsRequired) == true).Select(c => c.Name),
            StringComparer.Ordinal
        );

        // Create publishing middleware that publishes to subscribers
        // Positioned BEFORE MessageUpdateJoinerMiddleware to capture streaming updates
        var publishingMiddleware = new MessagePublishingMiddleware(PublishToAllAsync);

        // Build the complete middleware stack (loop owns the pipeline)
        // Response path order: Provider -> MessageTransformation -> JsonFragment -> Publishing -> Joiner -> ToolCall
        _agent = providerAgent
            .WithMessageTransformation(loggerFactory?.CreateLogger<MessageTransformationMiddleware>())
            .WithMiddleware(new JsonFragmentUpdateMiddleware())
            .WithMiddleware(publishingMiddleware)
            .WithMiddleware(
                new MessageUpdateJoinerMiddleware(
                    name: "MessageJoiner",
                    logger: loggerFactory?.CreateLogger<MessageUpdateJoinerMiddleware>()
                )
            )
            .WithMiddleware(toolCallMiddleware);
    }

    /// <summary>
    /// The <c>NotifyClient</c> tool's narrow persist+publish path (#246). Deliberately bypasses
    /// <c>SendAsync</c>/the input queue — it must never start or inject a turn — mirroring how
    /// <see cref="TriggerRuntime"/> is handed <c>resolve</c>/<c>notify</c> delegates closing over
    /// this loop's own methods rather than calling back through the public run surface.
    /// </summary>
    private ValueTask DeliverClientNotificationAsync(NotifyMessage notify, CancellationToken ct)
    {
        AddToHistory(notify);
        return PublishToAllAsync(notify, ct);
    }

    /// <inheritdoc />
    protected override async Task OnDisposeAsync()
    {
        if (SubAgentManager != null)
        {
            await SubAgentManager.DisposeAsync();
        }

        if (_triggerRuntime != null)
        {
            await _triggerRuntime.DisposeAsync();
        }
    }

    /// <summary>
    /// Filters the parent-tool snapshot handed to sub-agents, dropping any contract whose name is
    /// in <paramref name="nonInheritedToolNames"/>. Returns the input unchanged when there is
    /// nothing to exclude. The parent's own tool set is unaffected — only the inherited copy is
    /// filtered. See <see cref="SubAgentOptions.NonInheritedToolNames"/>.
    /// </summary>
    internal static IReadOnlyList<FunctionContract> FilterInheritableContracts(
        IEnumerable<FunctionContract> contracts,
        IReadOnlyCollection<string>? nonInheritedToolNames
    )
    {
        if (nonInheritedToolNames is not { Count: > 0 })
        {
            return contracts as IReadOnlyList<FunctionContract> ?? [.. contracts];
        }

        var excluded = new HashSet<string>(nonInheritedToolNames, StringComparer.Ordinal);
        return [.. contracts.Where(c => !excluded.Contains(c.Name))];
    }

    /// <inheritdoc />
    protected override async Task RunLoopAsync(CancellationToken ct)
    {
        Logger.LogDebug("MultiTurnAgentLoop run loop started");

        // Ordinary input that arrived while a delayed result was still waiting for its child run.
        // Loop-local, because only this thread ever touches it.
        List<QueuedInput> heldInputs = [];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Delayed results outrank queued input. A result that resolved after its run had
                // already ended is carried by its own child run, and until every one of them has
                // been carried the conversation is not in a state that can be sent anywhere: the
                // history still holds placeholders that no provider will accept.
                if (_delayed.TryDequeueCause(out var cause) && cause != null)
                {
                    await RunDelayedChildAsync(cause, ct);
                    continue;
                }

                List<QueuedInput> realInputs;
                if (heldInputs.Count > 0)
                {
                    realInputs = heldInputs;
                    heldInputs = [];
                }
                else
                {
                    // Wait for at least one input
                    if (!await InputReader.WaitToReadAsync(ct))
                    {
                        break; // Channel completed
                    }

                    // Drain all available inputs
                    _ = TryDrainInputs(out var batch);
                    if (batch.Count == 0)
                    {
                        continue;
                    }

                    // A batch can mix real input with the internal wake sentinel, which carries no
                    // messages of its own — it exists only to break the wait above so the delayed
                    // queue gets drained at the top of the next iteration.
                    realInputs = [.. batch.Where(b => b.Resume == null)];
                    if (realInputs.Count < batch.Count)
                    {
                        ResetWakeScheduled();
                    }

                    if (realInputs.Count == 0)
                    {
                        continue;
                    }
                }

                // A cause committed in the window between the drain above and here. Hold this input
                // rather than racing the child run to the provider; the next iteration runs the
                // child and the one after picks these back up.
                if (_delayed.HasPendingCauses)
                {
                    heldInputs = realInputs;
                    continue;
                }

                // Parked-on-deferral safety, scoped to notifications. When the conversation is parked on
                // an unresolved deferral a fresh model turn cannot run (the provider would reject the
                // pending tool_result — see the ExecuteTurnAsync precondition). For an out-of-band
                // NotifyMessage (e.g. a background sub-agent completing while the parent is parked on a
                // Wait) we fold it into history now — persisted under the deferring run and published live
                // as a pill — and let the delayed-result child run deliver it to the model once the
                // deferral resolves, turning what was an unconditional RunFailed into correct
                // at-continuation delivery. A regular user input while deferred deliberately keeps the
                // existing fail-fast guard (the caller must resolve the deferral first), so this is
                // restricted to batches that are entirely notifications.
                if (
                    !_delayed.IsEmpty
                    && AllMessagesAreNotifications(realInputs)
                    && await TryAppendParkedInputsAsync(realInputs, ct)
                )
                {
                    continue;
                }

                var (batchParent, isExplicitFork) = ResolveBatchParent(realInputs);
                var assignment = await StartRunAsync(realInputs, batchParent, ct, wasForked: isExplicitFork);
                await PublishToAllAsync(new RunAssignmentMessage { Assignment = assignment, ThreadId = ThreadId }, ct);

                using var spawnSuppression = new RunSpawnSuppression(this);
                _ = spawnSuppression.LatchIfRequested(realInputs);
                if (spawnSuppression.IsLatched)
                {
                    Logger.LogInformation("Run {RunId} starts with sub-agent spawning suppressed", assignment.RunId);
                }

                foreach (var input in realInputs)
                {
                    foreach (var msg in input.Input.Messages)
                    {
                        AddToHistory(msg);
                        await PublishIfNotifyAsync(msg, ct);
                    }
                }

                await ExecuteAssignedRunAsync(assignment, isExplicitFork, spawnSuppression, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Logger.LogDebug("MultiTurnAgentLoop run loop cancelled");
        }
        catch (ChannelClosedException)
        {
            Logger.LogDebug("Input channel closed");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Unexpected error in run loop");
            throw;
        }
        finally
        {
            await OnAfterRunAsync();
        }
    }

    /// <summary>
    /// Runs an already-announced assignment to completion, turning any failure into a completed-with-
    /// error run rather than letting it escape and kill the loop.
    /// </summary>
    private async Task ExecuteAssignedRunAsync(
        RunAssignment assignment,
        bool isExplicitFork,
        RunSpawnSuppression spawnSuppression,
        CancellationToken ct
    )
    {
        try
        {
            // Execute turns - poll for new input between turns
            await ExecuteRunTurnsAsync(assignment.RunId, assignment.GenerationId, spawnSuppression, ct);

            // Complete run - simple loop has no pending messages
            await CompleteRunAsync(
                assignment.RunId,
                assignment.GenerationId,
                wasForked: isExplicitFork,
                forkedToRunId: isExplicitFork ? assignment.RunId : null,
                pendingMessageCount: PendingInputCount,
                ct: ct
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per-run error: log, notify client, but keep the loop alive. First size the accumulated
            // conversation (the diff plus every fanned-out sub-agent result, folded into one history) so
            // a context-window overflow is debuggable AND so the error the CALLER receives carries WHY:
            // when a conversation outgrows the window the endpoint usually aborts the stream, and a bare
            // "response ended prematurely" tells a parent nothing about why a sub-agent dimension dropped.
            // Best-effort sizing — never masks the real error below.
            long estTokens = 0;
            try
            {
                var snapshot = GetHistorySnapshot();
                long chars = 0;
                foreach (var m in snapshot)
                {
                    try
                    {
                        chars += JsonSerializer.Serialize(m).Length;
                    }
                    catch
                    { /* a message that won't serialize contributes 0 to the estimate */
                    }
                }

                estTokens = chars / 4;
                Logger.LogWarning(
                    "Run {RunId} failing ({ExType}): conversation = {Messages} messages, ~{Chars} serialized "
                        + "chars (~{Tokens} tokens est).",
                    assignment.RunId,
                    ex.GetType().Name,
                    snapshot.Count,
                    chars,
                    estTokens
                );
            }
            catch
            {
                // Diagnostics must never mask the real per-run error logged below.
            }

            Logger.LogError(ex, "Error during run {RunId}", assignment.RunId);

            // Classify the failure in the error the caller sees so a dropped sub-agent reads as "context too
            // large" when — and only when — the exception says so. The provider's own overflow error is
            // definitive at any size; a transport abort on a large conversation is the shape a huge request
            // usually fails in (the endpoint cuts the stream rather than returning a clean 400), so it is
            // called LIKELY. Size alone earns nothing: a disposed client or a programming fault on a big
            // history is still a disposed client or a programming fault (#693).
            var errorMessage = ProviderErrorClassifier.ClassifyContextOverflow(ex, estTokens) switch
            {
                ContextOverflowVerdict.Overflow => $"{ex.Message} (conversation ~{estTokens} tokens est — exceeded "
                    + "the model context window; reduce scope or use a bigger-window model)",
                ContextOverflowVerdict.LikelyOverflow => $"{ex.Message} (conversation ~{estTokens} tokens est — "
                    + "likely exceeded the model context window; reduce scope or use a bigger-window model)",
                _ => ex.Message,
            };

            await CompleteRunAsync(
                assignment.RunId,
                assignment.GenerationId,
                wasForked: isExplicitFork,
                forkedToRunId: isExplicitFork ? assignment.RunId : null,
                pendingMessageCount: PendingInputCount,
                isError: true,
                errorMessage: errorMessage,
                ct: ct
            );
        }
    }

    /// <summary>
    /// Runs the child run that a delayed tool result causes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The child's cause is the real <see cref="ToolCallResultMessage"/> that arrived — not a
    /// fabricated user message and not an empty input. That result is <i>already</i> in history:
    /// resolution fills the placeholder in place, so the child carries it for attribution and
    /// reporting but must not append it, or the provider would be handed the same tool result
    /// twice.
    /// </para>
    /// <para>
    /// A child that is not the continuation owner takes no model turn at all. Its siblings from the
    /// same turn are still unresolved, and a request carrying a half-filled set of tool results is
    /// not a request any provider will accept — so it completes immediately, recording that it is
    /// waiting on them rather than pretending it finished the work.
    /// </para>
    /// </remarks>
    private async Task RunDelayedChildAsync(DelayedCause cause, CancellationToken ct)
    {
        var causeInput = new QueuedInput(
            new UserInput([cause.Result], InputId: null, ParentRunId: cause.RequestingRunId),
            ReceiptId: $"delayed:{cause.ToolCallId}",
            QueuedAt: DateTimeOffset.UtcNow,
            Resume: null
        );

        var assignment = await StartRunAsync(
            [causeInput],
            cause.RequestingRunId,
            ct,
            wasForked: false,
            runId: cause.ChildRunId,
            causeKind: LifecycleRunCauseKinds.ToolResult,
            causeToolCallId: cause.ToolCallId
        );

        // The run row is the only thing that tells the next process this continuation has begun —
        // recovery re-queues exactly those resolutions whose named child run has no row. Talking to
        // the provider without it would mean this process carries the result and the next one
        // carries it again. So the marker comes first, and when it is missing nothing irreversible
        // happens here: the durable state is left precisely as recovery wants to find it (the
        // resolution names this child, no row names it), and the continuation is picked up by the
        // process that comes after rather than run twice.
        if (!Lifecycle.IsRunStartDurable(assignment.RunId))
        {
            Logger.LogError(
                "Child run {RunId} for the delayed result of tool call {ToolCallId} ({ToolName}) could "
                    + "not be durably recorded as started; it is abandoned here rather than run "
                    + "un-recorded, and stays recoverable on restart",
                assignment.RunId,
                cause.ToolCallId,
                cause.ToolName
            );

            await CompleteRunAsync(
                assignment.RunId,
                assignment.GenerationId,
                pendingMessageCount: 0,
                isError: true,
                errorMessage: "the child run carrying this delayed tool result could not be durably "
                    + "recorded as started; it was not run, and remains recoverable",
                ct: ct
            );
            return;
        }

        await PublishToAllAsync(new RunAssignmentMessage { Assignment = assignment, ThreadId = ThreadId }, ct);

        if (!cause.IsContinuationOwner)
        {
            Logger.LogInformation(
                "Run {RunId} carries the delayed result for tool call {ToolCallId} ({ToolName}) but takes no "
                    + "turn: sibling tool calls from the same turn are still unresolved",
                assignment.RunId,
                cause.ToolCallId,
                cause.ToolName
            );

            await CompleteRunAsync(
                assignment.RunId,
                assignment.GenerationId,
                pendingMessageCount: 0,
                outcome: LifecycleRunOutcomes.AwaitingSiblingResults,
                ct: ct
            );
            return;
        }

        Logger.LogInformation(
            "Run {RunId} continuing conversation from delayed result for tool call {ToolCallId} ({ToolName}), "
                + "resolved in position {Ordinal}",
            assignment.RunId,
            cause.ToolCallId,
            cause.ToolName,
            cause.Ordinal
        );

        using var spawnSuppression = new RunSpawnSuppression(this);
        _ = spawnSuppression.LatchIfContinuing(cause.RequestingRunId);

        // One hop along the ParentRunId chain. This child is the same logical input as the run that
        // parked, so it inherits that run's spent stream-recovery budget rather than a fresh one.
        // Only the continuation owner reaches here, so the budget is claimed exactly once however
        // many siblings resolve.
        //
        // Both sources are claimed and the larger wins, because they are two views of the SAME park
        // and only one is ever populated: in this process the chain entry holds it and the durable
        // slot was never restored, while after a restart the chain entry is gone — a deferred entry
        // rebuilt from history carries no requesting run id — and the slot is all that remembers.
        CarryRecoveryBudgetForward(
            assignment.RunId,
            Math.Max(TakeCarriedRecoveryBudget(cause.RequestingRunId), TakeRestoredParkBudget())
        );

        // The park is over, so the durable slot describing it must go: the child now carries the
        // budget in memory, and leaving the slot behind would re-arm a continuation that has already
        // resumed. The child's own park, if it parks again, writes the slot afresh.
        await PersistRecoveryBudgetMarkerAsync(spent: 0, ct);

        await ExecuteAssignedRunAsync(assignment, isExplicitFork: false, spawnSuppression, ct);
    }

    /// <summary>
    /// Records the recovery budget <paramref name="runId"/> has already spent, so the child run that
    /// resumes it after a deferral does not start over with a full one.
    /// </summary>
    /// <param name="runId">The run that is parking.</param>
    /// <param name="spent">How many automatic recoveries this logical input has used.</param>
    private void CarryRecoveryBudgetForward(string runId, int spent)
    {
        if (spent == 0)
        {
            // Nothing spent is the same state a child run is born in, so storing it would only add
            // an entry for every parked run in exchange for no behaviour.
            return;
        }

        lock (_recoveryBudgetLock)
        {
            _recoveryBudgetByRunId[runId] = spent;
        }
    }

    /// <summary>
    /// Takes the recovery budget carried into <paramref name="runId"/>, if any, consuming it.
    /// </summary>
    /// <param name="runId">
    /// The run about to take its turns, or the run it is resuming. <see langword="null" /> — a cause
    /// with no requesting run — carries nothing, because there is no ancestor to have spent anything.
    /// </param>
    /// <returns>The number of automatic recoveries this logical input has already spent.</returns>
    private int TakeCarriedRecoveryBudget(string? runId)
    {
        if (runId is null)
        {
            return 0;
        }

        lock (_recoveryBudgetLock)
        {
            return _recoveryBudgetByRunId.Remove(runId, out var spent) ? spent : 0;
        }
    }

    /// <summary>
    /// Ends a run because a generation left tool calls unresolved, preserving the guarantees the run
    /// has already made so the child run that resumes it inherits them.
    /// </summary>
    /// <param name="runId">The run that is parking.</param>
    /// <param name="spawnSuppression">The run's sub-agent spawn suppression.</param>
    /// <param name="unresolvedCount">How many of the turn's calls are still outstanding.</param>
    /// <param name="recoverySpent">How many automatic stream recoveries this logical input has used.</param>
    /// <param name="ct">Cancels the persist.</param>
    private async Task ParkOnDeferralsAsync(
        string runId,
        RunSpawnSuppression spawnSuppression,
        int unresolvedCount,
        int recoverySpent,
        CancellationToken ct
    )
    {
        var suppressedRunId = spawnSuppression.IsLatched ? runId : null;
        lock (_spawnSuppressionLock)
        {
            _spawnSuppressedRunId = suppressedRunId;
        }

        // A delayed continuation may run after a host restart. Persist before acknowledging the
        // pause so a no-spawn guarantee already returned to the caller cannot silently disappear.
        await PersistSuppressedRunMarkerAsync(suppressedRunId, ct);
        CarryRecoveryBudgetForward(runId, recoverySpent);

        // Same reasoning as the suppression marker above, for the same restart: a budget that lives
        // only in this process is refunded by a crash, and the resumed input would buy a second
        // automatic recovery — a second provider call for a turn that already ran.
        await PersistRecoveryBudgetMarkerAsync(recoverySpent, ct);

        Logger.LogInformation(
            "Run {RunId} pausing on {Count} deferred tool call(s); awaiting external resolution",
            runId,
            unresolvedCount
        );
    }

    /// <summary>
    /// Execute the agentic turns for a run, polling for new input between turns.
    /// Ends the run early if any tool call deferrals from the current generation remain
    /// unresolved at the end of a turn — see <see cref="ResolveToolCallAsync"/>.
    /// </summary>
    private async Task ExecuteRunTurnsAsync(
        string runId,
        string generationId,
        RunSpawnSuppression spawnSuppression,
        CancellationToken ct
    )
    {
        var turnCount = 0;

        // Tracks WHY the loop stopped. The trailing `turnCount >= MaxTurnsPerRun` test is also true
        // when a turn broke naturally (or on a deferral) exactly AT the budget boundary, so it can't
        // by itself distinguish a genuine cap hit. Only a genuine cap hit — the while-condition going
        // false with the last turn still holding tool calls — needs the synthesizing wrap-up turn.
        var brokeEarly = false;

        // Automatic provider-stream recovery, budgeted for this whole logical input rather than per
        // turn: an input that can buy a fresh retry with every turn it survives can loop against a
        // failing transport indefinitely. Seeded from — not reset by — whatever a parked ancestor
        // run already spent, because a logical input that parks on a deferred client tool resumes
        // here in a child run and must not be handed a second budget for crossing that boundary.
        // `pendingResume` is what makes the very next turn a continuation instead of an unrelated
        // new turn, and it is cleared as soon as that turn consumes it.
        var recoveryCount = TakeCarriedRecoveryBudget(runId);
        ResumeSentinel? pendingResume = null;
        _compaction?.OnRunStarted();

        while (turnCount < MaxTurnsPerRun)
        {
            ct.ThrowIfCancellationRequested();

            // POLL: Check for new inputs before each turn (skip resume sentinels — they're
            // not real injected messages, they only exist to wake the channel).
            if (TryDrainInputs(out var newInputs) && newInputs.Count > 0)
            {
                var realNewInputs = newInputs.Where(b => b.Resume == null).ToList();
                if (realNewInputs.Count < newInputs.Count)
                {
                    // A wake-up drained here is consumed exactly as the run loop's own drain
                    // consumes one, and the flag has to come back for the same reason: a cause
                    // committed later, with the loop idle, schedules nothing while it is still set —
                    // and since a cause is never written to the channel itself, nothing would ever
                    // stir the loop to drain it. This is reachable whenever a wake-up is still in
                    // the channel while a run is going, which is exactly what restart recovery
                    // leaves behind: it queues the causes and schedules the wake-up before the loop
                    // starts, and the loop takes the cause from the coordinator — never from the
                    // channel — so the sentinel is still sitting there when the child run polls.
                    ResetWakeScheduled();
                }

                if (realNewInputs.Count > 0)
                {
                    if (spawnSuppression.LatchIfRequested(realNewInputs))
                    {
                        Logger.LogInformation(
                            "Input injected into run {RunId} requested sub-agent spawn suppression; "
                                + "suppressed for the remainder of the run",
                            runId
                        );
                    }

                    var injectionAssignment = new RunAssignment(
                        RunId: runId,
                        GenerationId: generationId,
                        InputIds: [.. realNewInputs.Select(i => i.ReceiptId)],
                        ParentRunId: null,
                        WasInjected: true
                    );

                    await PublishToAllAsync(
                        new RunAssignmentMessage { Assignment = injectionAssignment, ThreadId = ThreadId },
                        ct
                    );

                    await RecordInjectedInputsAsync(runId, injectionAssignment.InputIds!, ct);

                    foreach (var input in realNewInputs)
                    {
                        foreach (var msg in input.Input.Messages)
                        {
                            AddToHistory(msg);
                            await PublishIfNotifyAsync(msg, ct);
                        }
                    }

                    Logger.LogInformation(
                        "Injected {Count} new inputs into run {RunId}, sent RunAssignment",
                        realNewInputs.Count,
                        runId
                    );
                }
            }

            turnCount++;

            // Per-turn generationId. The client merge key is kind-runId-generationId-messageOrderIdx
            // and messageOrderIdx RESETS every turn (a fresh OrderingState per streaming invocation),
            // so a single run-scoped generationId makes turn N and turn N+1 collide — later turns'
            // reasoning/text (which carry no per-instance id, unlike tool calls' tool_call_id)
            // collapse onto the first block. Give each turn its own generationId so turns stay
            // distinct, while every message WITHIN a turn still shares one id (the #105/H1
            // requirement that a turn's tool_call + tool_call_result group together). Turn 1 reuses
            // the run's generationId so run_assignment's advertised id matches the first turn and
            // single-turn runs are unchanged; turns 2+ get a fresh id. Pillbox grouping is
            // arrival-order based on the client (not generationId), so this never changes grouping.
            var turnGenerationId = turnCount == 1 ? generationId : Guid.NewGuid().ToString("N");

            Logger.LogDebug(
                "Executing turn {Turn} of run {RunId} (generation {GenerationId})",
                turnCount,
                runId,
                turnGenerationId
            );

            BeginTurn(runId, turnGenerationId);
            var turn = await ExecuteTurnAsync(runId, turnGenerationId, turnCount, pendingResume?.InterruptedTurn, ct);
            pendingResume = null;

            if (turn.Overflow is { } overflow)
            {
                // The provider refused the request as too large. The generation produced nothing: report
                // it and drop its partials exactly as an interruption is handled, then compact once and
                // retry the same input once (spec 679 §5.1). A second overflow — or a compaction that
                // could not activate — fails the run with the typed reason (§5.6).
                await CompleteTurnAsync(runId, turnGenerationId, LifecycleTurnOutcomes.Interrupted, ct);
                await PublishToAllAsync(new GenerationAbandonedMessage(ThreadId, runId, turnGenerationId), ct);

                if (_compaction is not null && await _compaction.TryReactiveAsync(runId, turnGenerationId, ct))
                {
                    Logger.LogWarning(
                        overflow,
                        "Run {RunId} overflowed the context window at generation {GenerationId}; compacted, retrying once",
                        runId,
                        turnGenerationId
                    );
                    continue;
                }

                if (_compaction is not null)
                {
                    await _compaction.ReportOverflowAfterCompactionAsync(runId, turnGenerationId, ct);
                }

                throw new ContextOverflowException(
                    CompactionFailureReasons.OverflowAfterCompaction,
                    overflow.Message,
                    overflow
                );
            }

            if (turn.RetryableInterruption is { } interruption)
            {
                // This generation produced nothing further and never will. Report it as interrupted
                // rather than completed, then tell subscribers to drop whatever of it is still
                // unfinalized — canonical messages were delivered whole and stay. Both happen before
                // the retry decision so a client is never rendering a dead partial next to live output
                // from its replacement.
                await CompleteTurnAsync(runId, turnGenerationId, LifecycleTurnOutcomes.Interrupted, ct);
                await PublishToAllAsync(new GenerationAbandonedMessage(ThreadId, runId, turnGenerationId), ct);

                // A deferred tool call outlives the stream that requested it. If this turn left any
                // unresolved — a question put to the user, a client-side effect still running — then
                // there is nothing to recover TO: the next request would have to carry a half-filled
                // set of tool results, which no provider accepts and which the turn precondition
                // rejects outright. So the run parks exactly as an uninterrupted turn would, and the
                // continuation happens in the child run the eventual resolution causes.
                //
                // The recovery is counted as spent BEFORE parking. The interrupted attempt is being
                // abandoned and the child run's first turn is what replaces it, so that turn IS this
                // input's one automatic retry — it simply happens on the far side of the boundary.
                if (_delayed.TryPark(runId, turnGenerationId, out var interruptedUnresolved))
                {
                    Logger.LogWarning(
                        "Run {RunId} was interrupted with {Count} deferred tool call(s) outstanding from "
                            + "generation {GenerationId}; parking instead of continuing",
                        runId,
                        interruptedUnresolved,
                        turnGenerationId
                    );

                    await ParkOnDeferralsAsync(runId, spawnSuppression, interruptedUnresolved, recoveryCount + 1, ct);
                    brokeEarly = true;
                    break;
                }

                if (recoveryCount > 0)
                {
                    // The budget is spent. Failing here — rather than retrying again — is what keeps a
                    // persistently broken transport from consuming turns, tokens, and tool effects
                    // until the turn cap ends it. The run's error path classifies it for the client.
                    throw new StreamInterruptedAfterRecoveryException(interruption);
                }

                recoveryCount++;
                var interrupted = new InterruptedTurnResume(runId, turnGenerationId, turn.Attempt.HasCanonicalMessages);

                // Built FROM the resume record so the sentinel's ids cannot drift from the ones the
                // continuation turn actually reads.
                pendingResume = new ResumeSentinel(interrupted.InterruptedRunId, interrupted.InterruptedGenerationId)
                {
                    InterruptedTurn = interrupted,
                };

                Logger.LogWarning(
                    "Recovering run {RunId} from interrupted generation {GenerationId}: "
                        + "{Mode} (completed messages: {CompletedCount})",
                    runId,
                    turnGenerationId,
                    interrupted.HadCanonicalOutput ? "continuing after completed output" : "retrying empty attempt",
                    turn.Attempt.CompletedMessages.Count
                );

                // Loop round: the next iteration mints a fresh generation id, so the replacement
                // attempt can never collide with the abandoned one on the client's merge key.
                continue;
            }

            // Report the turn before deciding what the run does next, so a subscriber sees the turn
            // that produced the deferrals ahead of the run parking on them. A turn that ends any
            // other way — an exception or a cancellation out of ExecuteTurnAsync — is reported by
            // the finalizer when the run terminalizes, carrying the run's outcome.
            await CompleteTurnAsync(runId, turnGenerationId, ct: ct);

            // If any tool call from this generation deferred and is still unresolved, end the run
            // cleanly. Each result that arrives from here on carries its own child run, and the one
            // that clears the last outstanding call continues the conversation. Keyed on the
            // per-turn generationId — the turn's tool calls are tagged with it, so this stays
            // internally consistent. Marking and deciding happen together inside the coordinator,
            // which is what makes a resolution landing at this exact moment safe either way.
            if (_delayed.TryPark(runId, turnGenerationId, out var unresolvedCount))
            {
                await ParkOnDeferralsAsync(runId, spawnSuppression, unresolvedCount, recoveryCount, ct);
                brokeEarly = true;
                break;
            }

            if (!turn.HasToolCalls)
            {
                Logger.LogDebug("No tool calls in turn {Turn}, run complete", turnCount);
                brokeEarly = true;
                break;
            }
        }

        // Only a genuine cap hit reaches here without brokeEarly: the loop ran out of turn budget
        // while the last turn still emitted tool calls, so the run would otherwise end on a tool
        // result mid-stream. Run one synthesizing wrap-up turn so the run always terminates on an
        // assistant status message. The natural-completion and deferral-pause exits set brokeEarly
        // (they already end on assistant text or intentionally park for auto-resume), so they skip
        // this — even when they happen to land exactly on the budget boundary.
        if (!brokeEarly && turnCount >= MaxTurnsPerRun)
        {
            Logger.LogWarning(
                "Max turns ({MaxTurns}) reached for run {RunId}; running a wrap-up turn to return a final status",
                MaxTurnsPerRun,
                runId
            );
            await ExecuteWrapUpTurnAsync(runId, ct);
        }
    }

    /// <summary>
    /// Runs a single final turn after a run exhausts its turn budget, so the run ends on a
    /// synthesizing assistant status message instead of a bare tool result left mid-stream.
    /// </summary>
    /// <remarks>
    /// The loop — not the provider — executes tools, so a text-only close is guaranteed here without
    /// relying on <c>ToolChoice</c> (which is serialized inconsistently across providers and would
    /// 400 on Anthropic). This turn simply does not route any tool call the model emits: it is not
    /// executed and not added to history (persisting a dangling <c>tool_use</c> with no matching
    /// <c>tool_result</c> would break a later resume on providers that require the pair). A transient
    /// wrap-up instruction is appended to the SENT messages only — never to history — so the persisted
    /// transcript is not polluted with a synthetic "you hit the limit" user turn. If the model returns
    /// no usable text, a deterministic fallback status is published so the run never dead-ends on a
    /// tool result.
    /// </remarks>
    private async Task ExecuteWrapUpTurnAsync(string runId, CancellationToken ct)
    {
        // A distinct turn gets its own generationId (turn 1 reuses the run's id; every later turn,
        // this wrap-up included, gets a fresh one) so its messages don't collide with earlier turns
        // on the client merge key kind-runId-generationId-messageOrderIdx.
        var wrapUpGenerationId = Guid.NewGuid().ToString("N");
        BeginTurn(runId, wrapUpGenerationId);

        var turnOutcome = LifecycleTurnOutcomes.Completed;
        try
        {
            var options = DefaultOptions with { RunId = runId, ThreadId = ThreadId, GenerationId = wrapUpGenerationId };

            // Ephemeral instruction, appended to the sent messages only (NOT AddToHistory). Anthropic
            // merges consecutive same-role messages, so a Role.User instruction after the final
            // tool_result (also a user-role turn) is safe cross-provider.
            var wrapUpInstruction = new TextMessage
            {
                Text =
                    "You have reached the maximum number of tool-use turns for this run. Do not call any "
                    + "more tools. Write a concise final message that summarizes what you accomplished, "
                    + "the current status, and any remaining or unfinished work so nothing is left "
                    + "mid-stream.",
                Role = Role.User,
            };

            // The wrap-up reads the same view a turn would (an active checkpoint stays in force) but
            // never runs the policy: it is not a place a compaction may start (spec 679 §5.1).
            var messagesToSend = (_compaction?.BuildView() ?? GetMessagesWithSystemPrompt()).Concat([
                wrapUpInstruction,
            ]);

            IAsyncEnumerable<IMessage> stream;
            try
            {
                stream = await _agent.GenerateReplyStreamingAsync(messagesToSend, options, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The wrap-up is best-effort: if the model call itself fails, still close the run on a
                // deterministic status rather than propagating (which would fail the whole run) or
                // leaving it on a tool result. The lifecycle turn remains an error even though the run gets
                // a fallback assistant message.
                turnOutcome = LifecycleTurnOutcomes.Error;
                Logger.LogWarning(ex, "Wrap-up model turn failed for run {RunId}; publishing fallback status", runId);
                await PublishWrapUpFallbackAsync(runId, wrapUpGenerationId, ct);
                return;
            }

            var producedText = false;
            await foreach (var msg in stream.WithCancellation(ct))
            {
                // Drop any tool call the model emits despite the instruction: do not execute it and do
                // not persist it. Not executing guarantees the turn adds no new work and ends on text;
                // not persisting avoids a dangling tool_use with no tool_result that would break a later
                // resume. It was already surfaced to subscribers by the in-pipeline publishing middleware
                // (result-less pill), which is cosmetic and rare.
                if (msg is ToolCallMessage or ToolsCallMessage or ToolCallUpdateMessage or ToolsCallUpdateMessage)
                {
                    continue;
                }

                AddToHistory(msg);
                ObserveTurnMessage(runId, wrapUpGenerationId, msg);

                // A finalized, non-thinking, non-blank text message counts as a real wrap-up.
                if (msg is TextMessage { IsThinking: false } text && !string.IsNullOrWhiteSpace(text.Text))
                {
                    producedText = true;
                }
            }

            if (!producedText)
            {
                await PublishWrapUpFallbackAsync(runId, wrapUpGenerationId, ct);
            }
        }
        catch (OperationCanceledException)
        {
            turnOutcome = LifecycleTurnOutcomes.Cancelled;
            throw;
        }
        catch
        {
            turnOutcome = LifecycleTurnOutcomes.Error;
            throw;
        }
        finally
        {
            await CompleteTurnAsync(runId, wrapUpGenerationId, turnOutcome, CancellationToken.None);
        }
    }

    /// <summary>
    /// Publishes and records a deterministic final assistant status so a wrapped-up run always ends
    /// on an assistant message even when the model produced no usable text. Stamped via
    /// <see cref="MessageExtensions.WithIds(IMessage, string?, string?, string?, string?)"/> so its
    /// client merge key is consistent with the rest of the run.
    /// </summary>
    private async Task PublishWrapUpFallbackAsync(string runId, string generationId, CancellationToken ct)
    {
        var fallback = new TextMessage
        {
            Text =
                "This run reached its maximum number of tool-use turns and was stopped before the "
                + "task was fully completed. Some work may be unfinished — please review the steps "
                + "above and continue if needed.",
            Role = Role.Assistant,
        }.WithIds(runId, parentRunId: null, threadId: ThreadId, generationId: generationId);

        AddToHistory(fallback);
        ObserveTurnMessage(runId, generationId, fallback);
        await PublishToAllAsync(fallback, ct);
    }

    /// <summary>
    /// True when the batch is non-empty and every message in it is a <see cref="NotifyMessage"/>. A
    /// zero-allocation foreach (vs. LINQ) since this runs on every input drain.
    /// </summary>
    private static bool AllMessagesAreNotifications(List<QueuedInput> inputs)
    {
        if (inputs.Count == 0)
        {
            return false;
        }

        foreach (var input in inputs)
        {
            var messages = input.Input.Messages;
            if (messages.Count == 0)
            {
                return false;
            }

            foreach (var message in messages)
            {
                if (message is not NotifyMessage)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Publishes an out-of-band <see cref="NotifyMessage"/> to subscribers so its pill renders live and
    /// lands in the in-flight replay buffer. Injected inputs are otherwise only appended to history
    /// (persisted), never published, so without this a notification would appear only on a REST reload.
    /// No-op for any other message type (regular user turns render from the client's optimistic echo).
    /// </summary>
    private async Task PublishIfNotifyAsync(IMessage message, CancellationToken ct)
    {
        if (message is NotifyMessage)
        {
            await PublishToAllAsync(message, ct);
        }
    }

    /// <summary>
    /// Folds inputs that arrived while the conversation is parked on an unresolved deferral into history
    /// (persisted under the deferring run so they survive a restart) and publishes any notification pills
    /// live — without starting a model turn. The delayed-result child run delivers them to the model once
    /// the deferral resolves. See the call site in <see cref="RunLoopAsync"/> for why a turn cannot run here.
    /// </summary>
    private async Task<bool> TryAppendParkedInputsAsync(List<QueuedInput> realInputs, CancellationToken ct)
    {
        var parkRunId = _delayed.LastParkedRunId;
        var requestsSuppression = realInputs.Any(i => i.Input.SuppressSubAgentSpawning);
        if (requestsSuppression)
        {
            if (parkRunId == null)
            {
                return false;
            }

            lock (_spawnSuppressionLock)
            {
                _spawnSuppressedRunId = parkRunId;
            }

            await PersistSuppressedRunMarkerAsync(parkRunId, ct);
        }

        var count = 0;
        foreach (var input in realInputs)
        {
            foreach (var msg in input.Input.Messages)
            {
                AddToHistory(msg, parkRunId);
                await PublishIfNotifyAsync(msg, ct);
                count++;
            }
        }

        Logger.LogInformation(
            "Parked on unresolved deferral(s); folded {Count} input message(s) into history for delivery on resume.",
            count
        );

        return true;
    }

    private Task PersistSuppressedRunMarkerAsync(string? suppressedRunId, CancellationToken ct)
    {
        var store = Store;
        if (store == null)
        {
            return Task.CompletedTask;
        }

        return store.UpdateMetadataAsync(
            ThreadId,
            existing =>
            {
                var properties =
                    existing?.Properties?.ToBuilder() ?? ImmutableDictionary.CreateBuilder<string, object>();

                if (suppressedRunId == null)
                {
                    _ = properties.Remove(SpawnSuppressedRunIdProperty);
                }
                else
                {
                    properties[SpawnSuppressedRunIdProperty] = suppressedRunId;
                }

                return (existing ?? new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 0 }) with
                {
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Properties = properties.ToImmutable(),
                };
            },
            ct
        );
    }

    private async Task<string?> LoadSuppressedRunMarkerAsync(CancellationToken ct)
    {
        var store = Store;
        if (store == null)
        {
            return null;
        }

        var metadata = await store.LoadMetadataAsync(ThreadId, ct);
        return metadata?.Properties?.TryGetValue(SpawnSuppressedRunIdProperty, out var marker) == true
            ? marker?.ToString()
            : null;
    }

    /// <summary>
    /// Mirrors the parked run's spent recovery budget into thread metadata, so the resumed input is
    /// still bound by "at most one automatic recovery" when its answer arrives in a different process.
    /// <see langword="0"/> clears the slot.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT keyed by run id. A deferred entry rebuilt from persisted history carries no
    /// requesting run, so a keyed slot could never be matched by the continuation that needs it. A
    /// bare count is exact anyway: the loop parks one run at a time, so at most one park is
    /// outstanding, and the continuation consumes the slot before another park can write it. Writing
    /// on every park — including the zero case, which clears — is what stops the slot from outliving
    /// the park it describes.
    /// <para>
    /// Metadata rather than a schema change: this is the same mechanism, and the same
    /// single-property shape, as <see cref="PersistSuppressedRunMarkerAsync"/>, so it works unchanged
    /// across the in-memory, file, and SQLite stores with no migration.
    /// </para>
    /// </remarks>
    private Task PersistRecoveryBudgetMarkerAsync(int spent, CancellationToken ct)
    {
        var store = Store;
        if (store == null)
        {
            return Task.CompletedTask;
        }

        return store.UpdateMetadataAsync(
            ThreadId,
            existing =>
            {
                var properties =
                    existing?.Properties?.ToBuilder() ?? ImmutableDictionary.CreateBuilder<string, object>();

                if (spent <= 0)
                {
                    _ = properties.Remove(RecoveryBudgetProperty);
                }
                else
                {
                    // A string for the same reason the suppression marker is one: the value survives
                    // the JSON round trip of the file and SQLite stores as text either way.
                    properties[RecoveryBudgetProperty] = spent.ToString(CultureInfo.InvariantCulture);
                }

                return (existing ?? new ThreadMetadata { ThreadId = ThreadId, LastUpdated = 0 }) with
                {
                    LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Properties = properties.ToImmutable(),
                };
            },
            ct
        );
    }

    /// <summary>
    /// Re-arms the durable park budget from metadata, so a continuation that resumes after a restart
    /// inherits what its parked ancestor spent instead of a refunded budget.
    /// </summary>
    private async Task RestoreRecoveryBudgetAsync(CancellationToken ct)
    {
        var store = Store;
        if (store == null)
        {
            return;
        }

        var metadata = await store.LoadMetadataAsync(ThreadId, ct);
        if (metadata?.Properties?.TryGetValue(RecoveryBudgetProperty, out var marker) != true)
        {
            return;
        }

        var text = marker?.ToString();
        if (!int.TryParse(text, CultureInfo.InvariantCulture, out var spent) || spent <= 0)
        {
            // A marker we cannot read must not stop the restore — the conversation still has to come
            // back — so this reports and continues with a fresh budget.
            Logger.LogWarning("Ignoring unreadable persisted recovery budget marker {Marker}", text ?? "(null)");
            return;
        }

        lock (_recoveryBudgetLock)
        {
            _restoredParkRecoveryBudget = spent;
        }

        Logger.LogInformation(
            "Restored parked recovery budget: {Spent} automatic recovery/recoveries already spent",
            spent
        );
    }

    /// <summary>
    /// Claims the durable park budget, if a restart left one, consuming it.
    /// </summary>
    /// <remarks>
    /// Only the delayed continuation may call this. Any other run starting after a restart is a NEW
    /// logical input and is entitled to its own full budget.
    /// </remarks>
    private int TakeRestoredParkBudget()
    {
        lock (_recoveryBudgetLock)
        {
            var spent = _restoredParkRecoveryBudget;
            _restoredParkRecoveryBudget = 0;
            return spent;
        }
    }

    /// <summary>
    /// Runs one provider turn: streams the reply, records it, and dispatches its tool calls.
    /// </summary>
    /// <param name="runId">The run this turn belongs to.</param>
    /// <param name="generationId">The generation this turn streams under.</param>
    /// <param name="turnNumber">The turn's 1-based position within the run.</param>
    /// <param name="continuation">
    /// Set when this turn replaces one whose stream was cut short by a recoverable transport failure.
    /// It never introduces new user input — it only tells the provider, and only for this request,
    /// that completed work already exists and must be continued rather than restarted.
    /// </param>
    /// <param name="ct">Cancels the request and the streaming enumeration.</param>
    /// <returns>
    /// How the turn ended. A recoverable interruption is REPORTED rather than thrown, because the
    /// decision it feeds — retry, continue, or fail the run — belongs to the run loop, which is the
    /// only place that knows how much recovery this input has already used.
    /// </returns>
    private async Task<TurnExecutionResult> ExecuteTurnAsync(
        string runId,
        string generationId,
        int turnNumber,
        InterruptedTurnResume? continuation,
        CancellationToken ct
    )
    {
        // Use defaultOptions as template, override run-specific fields.
        // GenerationId carries the run's generation so providers stamp it (via WithIds) onto every
        // emitted message, overriding any opaque per-message id a provider would otherwise set
        // (BUG H1). This is the value run_assignment advertises, keeping the client merge key
        // kind-runId-generationId-messageOrderIdx consistent across the whole run.
        var options = DefaultOptions with
        {
            RunId = runId,
            ThreadId = ThreadId,
            GenerationId = generationId,
        };

        // Build messages list with system prompt prepended (if configured). Materialized once: this
        // list IS the request, and the deferral precondition below, the context report, and the
        // provider all have to be reading the same snapshot rather than three enumerations of a
        // history that a concurrent send could have moved on between them.
        List<IMessage> messagesToSend = [.. _compaction?.BuildView() ?? GetMessagesWithSystemPrompt()];

        // Precondition: never send a request while any deferred tool result is unresolved.
        // The provider would reject an empty tool_result.content, but the real bug is sending
        // at all — host code must call ResolveToolCallAsync before resuming.
        // Fast path: if no known deferrals exist, skip the O(n) history scan. The scan is
        // belt-and-suspenders against history rebuilt from a store that wasn't routed
        // through the coordinator (e.g., a partially-applied OnHistoryRestoredAsync); when
        // the coordinator IS empty, its index is the authoritative answer.
        if (!_delayed.IsEmpty)
        {
            foreach (var m in messagesToSend)
            {
                if (m is ToolCallResultMessage tcr && tcr.IsDeferred)
                {
                    throw new InvalidOperationException(
                        $"Cannot send request: tool call '{tcr.ToolCallId}' is still deferred. "
                            + "Resolve all deferred tool calls via ResolveToolCallAsync before resuming."
                    );
                }

                if (m is ToolsCallResultMessage agg)
                {
                    foreach (var r in agg.ToolCallResults)
                    {
                        if (r.IsDeferred)
                        {
                            throw new InvalidOperationException(
                                $"Cannot send request: tool call '{r.ToolCallId}' is still deferred. "
                                    + "Resolve all deferred tool calls via ResolveToolCallAsync before resuming."
                            );
                        }
                    }
                }
            }
        }

        // The compaction policy runs here and only here: immediately before the provider call, on the
        // request that is about to go out (spec 679 §5.1). A compaction replaces the request with the
        // view over the new checkpoint; a refusal is the harness declining to knowingly send beyond the
        // reserve after compaction failed (#678 AC 7).
        if (_compaction is { IsEnabled: true })
        {
            var pass = await _compaction.EvaluateAsync(
                runId,
                generationId,
                messagesToSend,
                continuation is not null,
                ct
            );
            if (pass.Refusal is { } refusal)
            {
                throw refusal;
            }

            if (pass.View is { } view)
            {
                messagesToSend = [.. view];
            }
        }

        // Continuing an interrupted turn that already produced completed output: tell the provider to
        // resume from what survives in the request rather than restart. Ephemeral instruction,
        // appended to the sent messages only (NOT AddToHistory, never published) — exactly the
        // discipline ExecuteWrapUpTurnAsync uses, so neither the persisted transcript nor the client
        // ever shows a synthetic user bubble. A fragment-only interruption gets no instruction at all:
        // nothing survived, so the correct request is the original one, byte for byte.
        if (continuation is { HadCanonicalOutput: true })
        {
            messagesToSend.Add(new TextMessage { Text = InterruptedTurnContinuationInstruction, Role = Role.User });
        }

        // Report the discovered context this request carries, read back out of the snapshot that is
        // about to go out. This is the last point at which "what the model will receive" is both
        // knowable and settled — earlier is a guess, later is history.
        await ReportContextLoadedAsync(runId, generationId, messagesToSend, ct);

        // Size the request against the model's window before it goes out (#681). Same snapshot as the
        // provider receives, so the estimate describes the request and not a history that moved on.
        await ObserveContextAsync(runId, generationId, messagesToSend, usage: null, ct);

        var attempt = new TurnAttemptState(generationId);

        try
        {
            var stream = await _agent.GenerateReplyStreamingAsync(messagesToSend, options, ct);

            await foreach (var msg in stream.WithCancellation(ct))
            {
                // Only canonical messages enter history (they are already published by
                // MessagePublishingMiddleware). Fragments are deltas of a value their canonical
                // message repeats in full, so keeping them buys nothing on a normal turn — and on an
                // interrupted one it is the difference between an attempt that left no trace and an
                // attempt that permanently poisoned the transcript with half a sentence. Lifecycle
                // observation stays unconditional so per-turn message counts are unchanged.
                //
                // Defense in depth, and currently unreachable: the loop-owned MessageUpdateJoiner sits
                // upstream of this point and absorbs all four fragment types into a builder, so no
                // fragment reaches here today and removing this gate breaks no test. It is kept because
                // that is a property of one middleware ORDERING this method does not assert; a new
                // fragment type the joiner does not accumulate, or a reordering, would otherwise persist
                // a partial with no other line standing in the way. Observe's own classification is
                // pinned by TurnAttemptStateTests.
                if (attempt.Observe(msg))
                {
                    AddToHistory(msg);
                }

                ObserveTurnMessage(runId, generationId, msg);

                // The provider's own count for the request that just went out replaces the estimate.
                if (msg is UsageMessage usageMessage)
                {
                    await ObserveContextAsync(runId, generationId, messagesToSend, usageMessage, ct);
                }

                // Handle tool calls - MessageTransformationMiddleware converts ToolsCallMessage -> ToolCallMessage
                if (msg is ToolCallMessage toolCall)
                {
                    if (toolCall.ExecutionTarget != ExecutionTarget.LocalFunction)
                    {
                        // Provider/server tools are executed remotely and should not be routed
                        // through local tool handlers.
                        Logger.LogDebug(
                            "Skipping non-local tool call (executed remotely): FunctionName={FunctionName}, ToolCallId={ToolCallId}, ExecutionTarget={ExecutionTarget}",
                            toolCall.FunctionName,
                            toolCall.ToolCallId,
                            toolCall.ExecutionTarget
                        );
                        continue;
                    }

                    // Fail-fast: ToolCallId is required for proper correlation
                    if (string.IsNullOrEmpty(toolCall.ToolCallId))
                    {
                        throw new InvalidOperationException(
                            $"ToolCallMessage.ToolCallId is required but was null or empty. "
                                + $"FunctionName: {toolCall.FunctionName ?? "(null)"}"
                        );
                    }

                    Logger.LogDebug(
                        "Tool call received: {FunctionName} (id: {ToolCallId})",
                        toolCall.FunctionName,
                        toolCall.ToolCallId
                    );

                    // Start execution and publish result immediately when complete
                    // This runs in parallel with LLM streaming and other tool executions.
                    // Pass the run's generationId so deferred entries are tagged consistently —
                    // toolCall.GenerationId is set by the provider/middleware and may be missing.
                    var executionTask = ExecuteAndPublishToolCallAsync(toolCall, runId, generationId, ct);
                    attempt.TrackToolTask(toolCall.ToolCallId, executionTask);
                }
            }
        }
        catch (Exception ex)
            when (!ct.IsCancellationRequested
                && _compaction is { Mode: CompactionMode.Compact }
                && _compaction.IsContextOverflow(ex, CompactionRuntime.EstimateTokens(messagesToSend))
            )
        {
            // The request itself was refused as too large. Nothing streamed, but the same settling
            // discipline as an interruption applies in case a tool did start before the failure surfaced.
            try
            {
                await attempt.SettleToolTasksAsync();
            }
            catch (Exception toolFailure)
            {
                Logger.LogWarning(
                    toolFailure,
                    "Tool execution dispatched by overflowed generation {GenerationId} of run {RunId} failed while settling",
                    generationId,
                    runId
                );
            }

            return new TurnExecutionResult(attempt) { Overflow = ex };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && IsRetryableStreamInterruption(ex))
        {
            // Terminally account for every tool this attempt dispatched BEFORE handing recovery back
            // to the run loop: these executions are still running against the same host, and letting
            // one land while the replacement attempt is streaming is exactly the duplicated-effect
            // race recovery exists to prevent. A tool that failed on its own is logged and swallowed
            // here — surfacing it would replace the interruption we can recover from with one we
            // cannot, and the tool's failure is already published to subscribers either way.
            try
            {
                await attempt.SettleToolTasksAsync();
            }
            catch (Exception toolFailure)
            {
                Logger.LogWarning(
                    toolFailure,
                    "Tool execution dispatched by interrupted generation {GenerationId} of run {RunId} failed while settling",
                    generationId,
                    runId
                );
            }

            Logger.LogWarning(
                ex,
                "Provider stream for run {RunId} generation {GenerationId} was interrupted after "
                    + "{CompletedCount} completed message(s) and {ToolCount} tool call(s)",
                runId,
                generationId,
                attempt.CompletedMessages.Count,
                attempt.PendingToolTasks.Count
            );

            return new TurnExecutionResult(attempt, ex);
        }

        // Wait for all tool executions to complete before next turn
        // Results are already published as each tool completes. Deferred handlers complete
        // synchronously with a placeholder, so this never blocks on external resolution.
        if (attempt.PendingToolTasks.Count > 0)
        {
            Logger.LogDebug("Awaiting {Count} tool call results", attempt.PendingToolTasks.Count);
            await attempt.SettleToolTasksAsync();
        }

        return new TurnExecutionResult(attempt);
    }

    /// <summary>
    /// Records one context observation for the generation (#681; spec 679 §4.1–4.2): estimated from the
    /// request when <paramref name="usage"/> is null, measured from the provider's usage otherwise. The
    /// observation is kept in memory, persisted under the thread's metadata (one ring entry per
    /// generation — the measured one supersedes the estimate), published as <c>context_measured</c>, and
    /// broadcast as a transient <c>context_pressure</c> frame when the window is known.
    /// </summary>
    /// <remarks>
    /// Observation never breaks the thing being observed: a store or publisher failure is logged and the
    /// turn proceeds. Cancellation is the caller's and is not swallowed.
    /// </remarks>
    private async Task ObserveContextAsync(
        string runId,
        string generationId,
        IReadOnlyList<IMessage> request,
        UsageMessage? usage,
        CancellationToken ct
    )
    {
        try
        {
            var previous = Volatile.Read(ref _latestContextObservation);
            var sameGeneration =
                previous is not null && string.Equals(previous.GenerationId, generationId, StringComparison.Ordinal);
            var ordinal = sameGeneration ? previous!.GenerationOrdinal : await NextGenerationOrdinalAsync(ct);

            var modelId = LifecycleServices.ModelId ?? DefaultOptions.ModelId;
            if (string.IsNullOrEmpty(modelId))
            {
                modelId = "unknown";
            }

            var capacity = LifecycleServices.CapacityResolver?.Resolve(modelId);
            var estimated = sameGeneration
                ? previous!.EstimatedInputTokens
                : (LifecycleServices.ContextTokenEstimator ?? DefaultContextTokenEstimator.Instance).Estimate(request);
            long? measured = usage is null ? null : MeasuredInputTokens(usage.Usage);

            var observation = new ContextObservation
            {
                ThreadId = ThreadId,
                AgentId = ObservationAgentId(),
                RunId = runId,
                GenerationId = generationId,
                GenerationOrdinal = ordinal,
                ObservedAtUtc = LifecycleServices.TimeProvider.GetUtcNow(),
                EffectiveModelId = modelId,
                EstimatedInputTokens = estimated,
                MeasuredInputTokens = measured,
                Provenance = measured is null ? MeasurementProvenance.Estimated : MeasurementProvenance.Measured,
                WindowTokens = capacity?.WindowTokens,
                // §5.2: the reserve is what the loop asked the model to keep for output; the catalog's
                // ceiling stands in only when the loop set no budget of its own.
                ReserveTokens = DefaultOptions.MaxToken ?? capacity?.MaxOutputTokens ?? 0,
                PromptCachingEnabled = DefaultOptions.PromptCaching != PromptCachingMode.Off,
                // The execution view and its active checkpoint arrive with the cut (#683); until then the
                // request is the whole canonical history.
                RowsInView = request.Count,
            };
            Volatile.Write(ref _latestContextObservation, observation);

            if (Store is { } store)
            {
                await ContextObservationProjection.RecordAsync(store, observation, ct: ct);
            }

            _ = await Lifecycle.ContextMeasuredAsync(observation, ct);

            if (capacity is not null)
            {
                await PublishToAllAsync(
                    ContextPressureMessage.FromObservation(observation) with
                    {
                        FromAgent = observation.AgentId,
                    },
                    ct
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(
                ex,
                "Context observation for run {RunId} generation {GenerationId} failed; the turn proceeds unobserved",
                runId,
                generationId
            );
        }
    }

    /// <summary>
    /// The next loop-local generation ordinal, continuing from the persisted latest observation the first
    /// time it is asked after construction so cooldown arithmetic (§5.4) survives a restart.
    /// </summary>
    private async ValueTask<long> NextGenerationOrdinalAsync(CancellationToken ct)
    {
        if (!_generationOrdinalSeeded)
        {
            if (Store is { } store)
            {
                var persisted = await ContextObservationProjection.LoadLatestAsync(store, ThreadId, ct);
                if (persisted is not null)
                {
                    _generationOrdinal = Math.Max(_generationOrdinal, persisted.GenerationOrdinal);
                }
            }

            _generationOrdinalSeeded = true;
        }

        return ++_generationOrdinal;
    }

    /// <summary>
    /// <c>root</c>, or the sub-agent id — from lineage when the host wired lifecycle, else from the thread
    /// id the SubAgentManager stamps on a child loop, so a child without a lifecycle bundle is still not
    /// reported as the root.
    /// </summary>
    private string ObservationAgentId() =>
        LifecycleServices.Lineage.SubAgentId
        ?? (
            ThreadId.StartsWith(AgentExecutionRef.SubAgentThreadIdPrefix, StringComparison.Ordinal)
                ? AgentExecutionRef.AgentIdFromThreadId(ThreadId)
                : AgentExecutionRef.RootAgentId
        );

    /// <summary>
    /// The request's measured size from the provider's usage: input plus cache creation, plus cache reads
    /// when the provider reports them ADDITIVELY (Anthropic's <c>input_tokens</c> excludes them, and it
    /// is the provider that surfaces <c>cache_creation_input_tokens</c>) rather than as a subset of input.
    /// </summary>
    private static long MeasuredInputTokens(Usage usage)
    {
        var additiveCacheAccounting = usage.ExtraProperties.ContainsKey("cache_creation_input_tokens");
        return usage.PromptTokens
            + usage.GetExtraProperty<int>("cache_creation_input_tokens")
            + (additiveCacheAccounting ? usage.TotalCachedTokens : 0);
    }

    /// <summary>
    /// Decides whether a failure that ended a provider stream is one an automatic retry can act on.
    /// </summary>
    /// <remarks>
    /// Typed classification only. Matching on exception text would quietly promote arbitrary provider,
    /// serialization, and tool faults to "retryable" and double their work; only a transport that
    /// ended the response body early is known to be safe. Cancellation is excluded outright — a user
    /// who stopped the run must never be answered with a second request.
    /// </remarks>
    private static bool IsRetryableStreamInterruption(Exception exception)
    {
        for (var candidate = exception; candidate is not null; candidate = candidate.InnerException)
        {
            if (candidate is OperationCanceledException)
            {
                return false;
            }

            if (
                candidate
                is HttpIOException { HttpRequestError: HttpRequestError.ResponseEnded }
                    or HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded }
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Executes a tool call and immediately publishes the result to all subscribers.
    /// This enables parallel execution with LLM streaming - results are sent to clients
    /// as each tool completes, rather than waiting for all tools to finish.
    /// For deferred handlers, the placeholder is registered with <see cref="_delayed"/>
    /// before publishing so a racing <see cref="ResolveToolCallAsync"/> always finds it.
    /// </summary>
    private async Task<ToolCallResultMessage> ExecuteAndPublishToolCallAsync(
        ToolCallMessage toolCall,
        string runId,
        string generationId,
        CancellationToken ct
    )
    {
        var startedAt = Stopwatch.GetTimestamp();
        var result = await ExecuteToolCallAsync(toolCall, runId, generationId, ct);
        var elapsed = Stopwatch.GetElapsedTime(startedAt);

        // Stamp ordering onto the result so the client merge/order logic (keyed partly on
        // messageOrderIdx) does not drop it (BUG H3b). The loop publishes tool results out-of-band,
        // bypassing the MessageTransformation middleware that stamps ordering on streamed messages,
        // so without this the result reaches subscribers with MessageOrderIdx == null. The result
        // immediately follows its tool call, so it takes the call's index + 1 (a different message
        // kind than the call, so no merge-key collision with the next call at the same index).
        if (result.MessageOrderIdx == null && toolCall.MessageOrderIdx is { } callOrderIdx)
        {
            result = result with { MessageOrderIdx = callOrderIdx + 1 };
        }

        if (result.IsDeferred)
        {
            // Reserve BEFORE making the placeholder visible to history or subscribers. Any incoming
            // ResolveToolCallAsync needs to find the reservation to succeed, and reserving here —
            // on the loop's own thread, where a failure is just a failed run — is what lets a
            // resolution arriving later never be turned away for want of capacity. Stamp with the
            // run's runId/generationId: toolCall.RunId/GenerationId are set by the
            // provider/middleware and may be unset in some agents/tests.
            var deferredAtUnixMs = result.DeferredAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var deferredEntry = new DeferredEntry(
                result.ToolCallId!,
                toolCall.FunctionName ?? string.Empty,
                toolCall.FunctionArgs ?? "{}",
                deferredAtUnixMs,
                toolCall.RunId ?? runId,
                toolCall.GenerationId ?? generationId
            );
            _ = _delayed.TryReserve(deferredEntry);

            // Persist synchronously so a webhook-triggered ReplaceMessageAsync cannot race
            // the placeholder's append. If persistence fails, unwind the reservation so a
            // future resolution doesn't find a half-applied state — the exception propagates
            // up to the run loop and surfaces as a RunFailed.
            try
            {
                await AddDeferredToHistoryAsync(result, ct);
            }
            catch
            {
                _ = _delayed.Release(result.ToolCallId!);
                throw;
            }

            // Durable half, so a call deferred by a process that then dies is still known to the
            // process that replaces it. Best-effort by design — see the wrapper.
            await Lifecycle.RecordDeferredToolCallAsync(
                deferredEntry.RunId ?? runId,
                new DeferredToolCallRecord
                {
                    ToolCallId = deferredEntry.ToolCallId,
                    ToolName = deferredEntry.FunctionName,
                    GenerationId = deferredEntry.GenerationId,
                    DeferredAt = DateTimeOffset.FromUnixTimeMilliseconds(deferredAtUnixMs),
                },
                ct
            );

            await PublishToAllAsync(result, ct);

            Logger.LogInformation(
                "Tool call {ToolCallId} ({FunctionName}) deferred with placeholder length {Length}",
                toolCall.ToolCallId,
                toolCall.FunctionName,
                result.Result.Length
            );

            return result;
        }

        await PublishToolCompletedAsync(result, toolCall, runId, generationId, elapsed, wasDeferred: false, ct);

        // Non-deferred result. Add text-only version to LLM history (captions are in the
        // text for id:// referencing); publish full version with ContentBlocks to
        // subscribers (for image data resolution).
        var historyResult = result.ContentBlocks != null ? result with { ContentBlocks = null } : result;
        AddToHistory(historyResult);
        await PublishToAllAsync(result, ct);

        Logger.LogDebug(
            "Tool result for {ToolCallId}: {ResultPreview}",
            toolCall.ToolCallId,
            result.Result.Length > 100 ? result.Result[..100] + "..." : result.Result
        );

        return result;
    }

    /// <summary>
    /// Emits <c>tool_completed</c> for a tool call that reached its final state.
    /// </summary>
    /// <remarks>
    /// Emitted for every host-executed call, not only deferred ones — an event that fired only for
    /// the rare delayed case would describe a tool surface no consumer could reason about. Lifecycle
    /// is off unless a host wires it up, so on the default path this is a branch and a return.
    /// </remarks>
    private Task PublishToolCompletedAsync(
        ToolCallResultMessage result,
        ToolCallMessage toolCall,
        string runId,
        string? generationId,
        TimeSpan? elapsed,
        bool wasDeferred,
        CancellationToken ct
    )
    {
        if (!Lifecycle.IsEnabled)
        {
            return Task.CompletedTask;
        }

        return Lifecycle.ToolCompletedAsync(
            runId,
            generationId,
            result.ToolCallId ?? toolCall.ToolCallId ?? string.Empty,
            toolCall.FunctionName ?? string.Empty,
            ClassifyToolOutcome(result),
            wasDeferred: wasDeferred,
            durationMilliseconds: elapsed == null ? null : (long)elapsed.Value.TotalMilliseconds,
            error: result.IsError
                ? new LifecycleError { Code = result.ErrorCode ?? string.Empty, Message = result.Result }
                : null,
            ct: ct
        );
    }

    /// <summary>
    /// Maps a tool result onto the lifecycle outcome vocabulary.
    /// </summary>
    /// <remarks>
    /// A refusal is reported as <see cref="LifecycleToolOutcomes.Denied"/> rather than
    /// <see cref="LifecycleToolOutcomes.Failed"/>, because the two say different things to anyone
    /// auditing the stream: denied means the handler never ran. The distinguishing signal is the
    /// error code, which <c>PreparedToolInvocation.ToBlockedResult</c> sets to the approval outcome
    /// — the same constants the payload's approval decision uses.
    /// </remarks>
    private static string ClassifyToolOutcome(ToolCallResultMessage result)
    {
        if (!result.IsError)
        {
            return LifecycleToolOutcomes.Succeeded;
        }

        return result.ErrorCode switch
        {
            ApprovalOutcomes.Cancelled => LifecycleToolOutcomes.Cancelled,
            ApprovalOutcomes.Denied
            or ApprovalOutcomes.ProviderPolicyDenied
            or ApprovalOutcomes.HostPolicyDenied
            or ApprovalOutcomes.Timeout
            or ApprovalOutcomes.Overload
            or ApprovalOutcomes.Revoked
            or ApprovalOutcomes.HookError
            or ApprovalOutcomes.MissingApprover => LifecycleToolOutcomes.Denied,
            _ => LifecycleToolOutcomes.Failed,
        };
    }

    private async Task<ToolCallResultMessage> ExecuteToolCallAsync(
        ToolCallMessage toolCall,
        string runId,
        string generationId,
        CancellationToken ct
    )
    {
        // Fail-fast validation: these fields are required for proper tool execution
        ArgumentNullException.ThrowIfNull(toolCall);

        if (string.IsNullOrEmpty(toolCall.ToolCallId))
        {
            throw new ArgumentException(
                "ToolCallMessage.ToolCallId is required but was null or empty.",
                nameof(toolCall)
            );
        }

        if (string.IsNullOrEmpty(toolCall.FunctionName))
        {
            throw new ArgumentException(
                $"ToolCallMessage.FunctionName is required but was null or empty. ToolCallId: {toolCall.ToolCallId}",
                nameof(toolCall)
            );
        }

        // FunctionArgs can be null for parameterless functions - treat as empty object
        var functionArgs = toolCall.FunctionArgs ?? "{}";

        try
        {
            if (!_toolHandlers.TryGetValue(toolCall.FunctionName, out var handler))
            {
                // Not every unregistered name is a hallucination: the sub-agent provider WITHDRAWS the
                // spawn tool at the delegation limit (and while spawning is suppressed) while the model
                // may still have it in history. Telling that model only "Unknown function" leaves it
                // unable to tell a rule it must respect from a name it invented, and gives it nothing to
                // do instead - so ask the provider for the reason first (#671).
                if (
                    SubAgentTools is { } subAgentTools
                    && subAgentTools.TryDescribeWithdrawnTool(
                        toolCall.FunctionName,
                        out var withdrawnCode,
                        out var withdrawnText
                    )
                )
                {
                    Logger.LogInformation(
                        "Function '{FunctionName}' is withdrawn ({WithdrawalCode}); returning the reason "
                            + "instead of an unknown-function error. ToolCallId: {ToolCallId}",
                        toolCall.FunctionName,
                        withdrawnCode,
                        toolCall.ToolCallId
                    );

                    return BuildErrorResultMessage(
                        toolCall,
                        JsonSerializer.Serialize(
                            new
                            {
                                error = withdrawnText,
                                code = withdrawnCode,
                                available_functions = _toolHandlers.Keys.ToArray(),
                            }
                        ),
                        withdrawnCode
                    );
                }

                // Unknown function - likely LLM hallucination, return error to allow self-correction
                Logger.LogWarning(
                    "No handler registered for function '{FunctionName}'. Returning error to LLM. "
                        + "ToolCallId: {ToolCallId}. Available functions: [{AvailableFunctions}]",
                    toolCall.FunctionName,
                    toolCall.ToolCallId,
                    string.Join(", ", _toolHandlers.Keys)
                );

                return BuildErrorResultMessage(
                    toolCall,
                    JsonSerializer.Serialize(
                        new
                        {
                            error = $"Unknown function: {toolCall.FunctionName}",
                            available_functions = _toolHandlers.Keys.ToArray(),
                        }
                    )
                );
            }

            // The gate opens here and nowhere earlier: an unknown function has already returned
            // above, so a hallucinated tool name never reaches an approver. With nothing
            // configured this is a synchronous approval that consults nothing.
            var prepared = await LifecycleServices.Approval.PrepareAsync(
                new ToolInvocationRequest
                {
                    ToolName = toolCall.FunctionName,
                    ArgumentsJson = functionArgs,
                    ToolCallId = toolCall.ToolCallId,
                    ExecutionTarget = ExecutionTarget.LocalFunction,
                    ThreadId = ThreadId,
                    RunId = toolCall.RunId ?? runId,
                    GenerationId = toolCall.GenerationId ?? generationId,
                },
                ct
            );

            if (!prepared.IsApproved)
            {
                // Refusal is rendered as an ordinary error result rather than an exception, so the
                // model sees why its call did not run and can choose something else — the same
                // shape an unknown function gets. The handler is never reached.
                Logger.LogWarning(
                    "Tool call refused before execution: {FunctionName}, ToolCallId: {ToolCallId}, "
                        + "Outcome: {Outcome}",
                    toolCall.FunctionName,
                    toolCall.ToolCallId,
                    prepared.Outcome
                );

                return ToolCallResultMessage.FromToolCallResult(
                    ToolResultLimits.Apply(prepared.ToBlockedResult()),
                    role: Role.User,
                    fromAgent: toolCall.FromAgent,
                    generationId: toolCall.GenerationId
                );
            }

            var ctx = new ToolCallContext { ToolCallId = toolCall.ToolCallId };

            // Truncation guard: when the provider hits its per-turn output ceiling it stops with
            // stop_reason=max_tokens and cuts the streaming tool_use argument JSON off mid-string (or
            // emits nothing at all). Executing a side-effecting tool — Write a file, run a Bash command —
            // with those corrupt/empty args is the actual harm. For a tool that declares a required
            // parameter, refuse to dispatch when the payload is empty/whitespace or not well-formed JSON,
            // returning a recoverable, LLM-visible error so the run survives and the model can retry.
            // Parameterless tools are exempt (empty args are legitimate for them).
            if (_functionsRequiringArgs.Contains(toolCall.FunctionName) && !ArgumentsAreWellFormed(functionArgs))
            {
                Logger.LogWarning(
                    "Rejecting tool call '{FunctionName}' with malformed/empty arguments (likely truncated "
                        + "at the provider max_tokens ceiling). Returning a recoverable error to the LLM. "
                        + "ToolCallId: {ToolCallId}, ArgsLength: {ArgsLength}",
                    toolCall.FunctionName,
                    toolCall.ToolCallId,
                    toolCall.FunctionArgs?.Length ?? 0
                );

                return BuildErrorResultMessage(
                    toolCall,
                    JsonSerializer.Serialize(
                        new
                        {
                            error = $"Malformed arguments for '{toolCall.FunctionName}': the tool-call argument JSON was "
                                + "empty or not well-formed (it may have been truncated). Re-issue the tool call with "
                                + "complete, valid JSON arguments.",
                        }
                    )
                );
            }

            var result = await handler(prepared.Arguments.Json, ctx, ct);

            return BuildResultMessage(toolCall, result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-initiated cancellation (shutdown, run abort) must propagate — converting
            // it into an LLM-visible error message would silently swallow the abort and let
            // the loop continue.
            throw;
        }
        catch (Exception ex)
        {
            // Tool execution errors are returned to the LLM for retry/correction
            Logger.LogError(ex, "Error executing tool call: {FunctionName}", toolCall.FunctionName);
            return BuildErrorResultMessage(toolCall, JsonSerializer.Serialize(new { error = ex.Message }));
        }
    }

    /// <summary>
    /// True when <paramref name="functionArgs"/> is a non-empty, well-formed JSON document — the
    /// precondition for dispatching a tool that declares required parameters. Empty/whitespace text
    /// (Bash args arriving as 0 bytes) and a truncated fragment (Write args cut off mid-string at the
    /// max_tokens ceiling, so the JSON never closes) both return false. A well-formed but incomplete
    /// object (missing a required field) still parses here and is left to the handler's own
    /// deserialization to surface — this guard targets the truncation failure mode, not schema
    /// validation.
    /// </summary>
    private static bool ArgumentsAreWellFormed(string functionArgs)
    {
        if (string.IsNullOrWhiteSpace(functionArgs))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(functionArgs);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private ToolCallResultMessage BuildResultMessage(ToolCallMessage toolCall, ToolHandlerResult result)
    {
        var tcr = ToolCallResultBuilder.FromHandlerResult(
            result,
            toolCall.ToolCallId,
            ToolResultLimits,
            toolCall.FunctionName
        );
        return ToolCallResultMessage.FromToolCallResult(
            tcr,
            role: Role.User,
            fromAgent: toolCall.FromAgent,
            generationId: toolCall.GenerationId
        );
    }

    /// <summary>
    /// Builds the error result the model gets back for a call that never ran (unknown function,
    /// malformed arguments) or that threw. Goes through the same <see cref="ToolResultLimits"/>
    /// as a successful result, so an exception message of arbitrary size cannot enter history
    /// unbounded (#694). <c>errorCode</c> is the machine-readable reason, persisted alongside
    /// <c>is_error</c> so a consumer can act on the refusal without parsing prose; it is null for
    /// errors that carry no stable code.
    /// </summary>
    private ToolCallResultMessage BuildErrorResultMessage(
        ToolCallMessage toolCall,
        string errorJson,
        string? errorCode = null
    )
    {
        var tcr = ToolResultLimits.Apply(
            new ToolCallResult(toolCall.ToolCallId, errorJson)
            {
                ToolName = toolCall.FunctionName,
                IsError = true,
                ErrorCode = errorCode,
            }
        );
        return ToolCallResultMessage.FromToolCallResult(
            tcr,
            role: Role.User,
            fromAgent: toolCall.FromAgent,
            generationId: toolCall.GenerationId
        );
    }

    /// <summary>
    /// Resolves a previously-deferred tool call. Mutates the placeholder in history and
    /// persisted store, publishes the resolved <see cref="ToolCallResultMessage"/> to
    /// subscribers, and — when the run that requested the call has already ended — queues a
    /// child run carrying the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent for byte-equal duplicate deliveries (webhook retries are common).
    /// </para>
    /// <para>
    /// Throws <see cref="InvalidOperationException"/> if no matching deferred call exists, or if
    /// the call has already been resolved with different content. It also propagates whatever the
    /// durable lifecycle store threw when it could not record the resolution: that failure means
    /// the call is <em>still deferred</em> and the delivery is safe to send again, which is exactly
    /// the case a caller must not mistake for success.
    /// </para>
    /// <para>
    /// Use <see cref="TryResolveToolCallAsync"/> when the caller needs to tell a retryable failure
    /// from a permanent one — an exception cannot carry that distinction.
    /// </para>
    /// </remarks>
    public async Task ResolveToolCallAsync(
        string toolCallId,
        string result,
        bool isError = false,
        IList<ToolResultContentBlock>? contentBlocks = null,
        CancellationToken ct = default
    )
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        ArgumentNullException.ThrowIfNull(result);

        var (_, failure) = await ResolveToolCallInternalAsync(toolCallId, result, isError, contentBlocks, ct);
        if (failure != null)
        {
            // Rethrow the original, not a wrapper: callers (and tests) match on the exact messages
            // this method has always produced.
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// Resolves a previously-deferred tool call and reports what happened instead of throwing.
    /// </summary>
    /// <returns>
    /// The outcome. <see cref="ResolveToolCallOutcome.StoreFailed"/> and
    /// <see cref="ResolveToolCallOutcome.Cancelled"/> mean the call is untouched and the delivery
    /// can be retried unchanged; <see cref="ResolveToolCallOutcome.NotFound"/> and
    /// <see cref="ResolveToolCallOutcome.Conflict"/> mean retrying is pointless.
    /// </returns>
    /// <remarks>
    /// This is the shape a delivery endpoint wants. A webhook handler that catches an exception
    /// knows only that something went wrong — not whether redelivering would help or would just
    /// replay a permanent rejection forever. Everything else about the operation is identical to
    /// <see cref="ResolveToolCallAsync"/>; only argument validation still throws.
    /// </remarks>
    public async Task<ResolveToolCallOutcome> TryResolveToolCallAsync(
        string toolCallId,
        string result,
        bool isError = false,
        IList<ToolResultContentBlock>? contentBlocks = null,
        CancellationToken ct = default
    )
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        ArgumentNullException.ThrowIfNull(result);

        var (outcome, _) = await ResolveToolCallInternalAsync(toolCallId, result, isError, contentBlocks, ct);
        return outcome;
    }

    /// <summary>
    /// The one implementation behind both public resolve methods. Returns the outcome plus, when
    /// the outcome is a failure, the exception the throwing overload should raise — so the two
    /// surfaces cannot drift apart in what they consider an error.
    /// </summary>
    private async Task<(ResolveToolCallOutcome Outcome, Exception? Failure)> ResolveToolCallInternalAsync(
        string toolCallId,
        string result,
        bool isError,
        IList<ToolResultContentBlock>? contentBlocks,
        CancellationToken ct
    )
    {
        // Canonical form first. The bounded text is what history stores and replays, so it — not
        // the raw delivery — drives the fingerprint, the identical-redelivery check and the
        // conflict decision. A byte-equal redelivery bounds to the same text and stays idempotent.
        var truncated = TryBoundResolution(toolCallId, ref result, ref contentBlocks);
        var fingerprint = ComputeResolutionFingerprint(result, isError);

        if (!_delayed.TryBeginResolve(toolCallId, fingerprint, out var pending, out var inFlightFingerprint))
        {
            if (inFlightFingerprint != null)
            {
                // A different delivery of the same call holds the claim right now. Which of the two
                // "wins" is already decided — the claim holder does — so the only question is
                // whether this delivery is saying the same thing.
                if (string.Equals(inFlightFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    Logger.LogDebug(
                        "Resolution of tool call {ToolCallId} is a duplicate of one already being applied",
                        toolCallId
                    );
                    return (ResolveToolCallOutcome.Duplicate, null);
                }

                return (
                    ResolveToolCallOutcome.Conflict,
                    new InvalidOperationException(ResolutionConflictMessage(toolCallId))
                );
            }

            return await ResolveUnclaimedAsync(toolCallId, result, isError, contentBlocks, truncated, ct);
        }

        return await ResolveClaimedAsync(pending!, toolCallId, result, isError, contentBlocks, truncated, ct);
    }

    /// <summary>
    /// Applies <see cref="MultiTurnAgentBase.ToolResultLimits"/> to a delivered resolution before
    /// anything is derived from it. Returns whether something was cut.
    /// </summary>
    private bool TryBoundResolution(
        string toolCallId,
        ref string result,
        ref IList<ToolResultContentBlock>? contentBlocks
    )
    {
        if (!ToolResultLimits.TryApply(new ToolCallResult(toolCallId, result, contentBlocks), out var bounded))
        {
            return false;
        }

        Logger.LogWarning(
            "Deferred tool result truncated: ToolCallId={ToolCallId}, OriginalBytes={OriginalBytes}, MaxResultBytes={MaxResultBytes}",
            toolCallId,
            Encoding.UTF8.GetByteCount(result),
            ToolResultLimits.MaxResultBytes
        );
        result = bounded.Result;
        contentBlocks = bounded.ContentBlocks;
        return true;
    }

    /// <summary>
    /// Applies a resolution the coordinator has granted a claim for: durable record first, then
    /// history, then the child run it causes.
    /// </summary>
    /// <remarks>
    /// The durable write goes first deliberately. If it fails, nothing about the conversation has
    /// changed and the caller can send the result again — whereas a store failure <em>after</em>
    /// history was mutated would leave a resolution the process believes happened and the store
    /// does not, which no retry can repair.
    /// </remarks>
    private async Task<(ResolveToolCallOutcome Outcome, Exception? Failure)> ResolveClaimedAsync(
        ResolvingDeferral pending,
        string toolCallId,
        string result,
        bool isError,
        IList<ToolResultContentBlock>? contentBlocks,
        bool truncated,
        CancellationToken ct
    )
    {
        DeferredResolutionOutcome durable;
        try
        {
            durable = await Lifecycle.TryResolveDeferredToolCallAsync(
                toolCallId,
                ComputeResolutionFingerprint(result, isError),
                pending.ChildRunId,
                ct
            );
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _delayed.AbortResolve(pending);
            return (ResolveToolCallOutcome.Cancelled, ex);
        }
        catch (Exception ex)
        {
            // Anything else, including an OperationCanceledException the store raised for its own
            // reasons — a connection timeout, an internal linked token. That is a store failure, not
            // the caller withdrawing the delivery, and telling the caller "cancelled" would invite it
            // to drop a result the store is perfectly willing to take on the next attempt. Either way
            // the claim is already back, so the state is retry-safe.
            _delayed.AbortResolve(pending);
            Logger.LogWarning(
                ex,
                "Could not durably record the resolution of tool call {ToolCallId}; the call stays deferred and the result can be delivered again",
                toolCallId
            );
            return (ResolveToolCallOutcome.StoreFailed, ex);
        }

        if (durable == DeferredResolutionOutcome.Conflict)
        {
            _delayed.AbortResolve(pending);
            return (
                ResolveToolCallOutcome.Conflict,
                new InvalidOperationException(ResolutionConflictMessage(toolCallId))
            );
        }

        // Duplicate and NotFound both continue. Duplicate means a previous attempt committed the
        // durable record and then died before touching history — the in-memory claim proves history
        // is still unresolved, so finishing the job is precisely right. NotFound means the store
        // never got the deferral recorded in the first place (that write is best-effort); the
        // in-process reservation is the authority on a call we know we deferred.
        if (durable == DeferredResolutionOutcome.NotFound)
        {
            Logger.LogDebug(
                "Durable store had no deferral record for tool call {ToolCallId}; resolving from in-process state",
                toolCallId
            );
        }
        else
        {
            // The requesting run can have parked while that write was in flight, and a run that has
            // parked can no longer absorb the result — a child run has to carry it, and the record
            // that just went out names no child run at all. Settle it here, where failing is still
            // free: history has not been touched and the claim is still live, so refusing the whole
            // resolution leaves the caller free to deliver it again. Afterwards neither is true.
            var (attached, refusal, attachFailure) = await AttachChildRunBeforeHistoryAsync(
                pending,
                toolCallId,
                durable,
                ct
            );
            if (refusal != null)
            {
                return (refusal.Value, attachFailure);
            }

            pending = attached;
        }

        // A reservation says the call was outstanding when it was taken; history is what actually
        // gets sent to the provider, and it has the final say. It can disagree — a placeholder that
        // a previous process already resolved, for instance — and when it does, the resolution
        // already in history wins over the one arriving now.
        var alreadyIdentical = false;
        ToolCallResultMessage oldMessage;
        ToolCallResultMessage newMessage;
        try
        {
            (oldMessage, newMessage) = UpdateToolResultByCallId(
                toolCallId,
                existing =>
                {
                    if (existing.IsDeferred)
                    {
                        return ApplyResolution(existing, result, isError, truncated);
                    }

                    if (existing.Result == result && existing.IsError == isError)
                    {
                        alreadyIdentical = true;
                        return existing;
                    }

                    throw new InvalidOperationException(ResolutionConflictMessage(toolCallId));
                }
            );
        }
        catch (Exception ex)
        {
            // Give the claim back rather than retiring it: the call stays visibly outstanding, which
            // is the truth — this delivery did not resolve it and nothing else has either.
            _delayed.AbortResolve(pending);
            Logger.LogWarning(
                ex,
                "Tool call {ToolCallId} could not be resolved in history despite an outstanding deferral",
                toolCallId
            );
            return (ResolveToolCallOutcome.Conflict, ex);
        }

        if (!alreadyIdentical)
        {
            await ReplacePersistedAsync(oldMessage, newMessage, ct);

            // Publish the full message (including ContentBlocks) to subscribers so UIs can
            // render images. The history entry stays text-only.
            var publishMessage =
                contentBlocks != null && contentBlocks.Count > 0
                    ? newMessage with
                    {
                        ContentBlocks = contentBlocks,
                    }
                    : newMessage;
            await PublishToAllAsync(publishMessage, ct);
        }
        else
        {
            // History was already carrying this exact result. Nothing to write or announce — but the
            // reservation still has to retire below, or the conversation stays parked on a call that
            // is, as far as the provider is concerned, long since answered.
            Logger.LogDebug(
                "Tool call {ToolCallId} was already resolved in history with identical content; retiring the outstanding deferral",
                toolCallId
            );
        }

        // Emitted before the cause is committed so a subscriber always sees the tool finish before
        // the run that carries its result starts — the loop can pick up a queued cause the instant
        // it exists.
        if (Lifecycle.IsEnabled)
        {
            await Lifecycle.ToolCompletedAsync(
                pending.Entry.RunId ?? LatestRunId ?? string.Empty,
                pending.Entry.GenerationId,
                toolCallId,
                pending.Entry.FunctionName,
                isError ? LifecycleToolOutcomes.Failed : LifecycleToolOutcomes.Succeeded,
                wasDeferred: true,
                durationMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pending.Entry.DeferredAtUnixMs,
                error: isError
                    ? new LifecycleError { Code = newMessage.ErrorCode ?? string.Empty, Message = result }
                    : null,
                ct: ct
            );
        }

        var cause = _delayed.CompleteResolve(pending, newMessage);

        Logger.LogInformation(
            "Tool call {ToolCallId} resolved (was deferred for {ElapsedMs}ms)",
            toolCallId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - pending.Entry.DeferredAtUnixMs
        );

        if (cause != null)
        {
            if (pending.ChildRunId == null)
            {
                // Minted later still: the run parked during the history write, after the attempt
                // above had already found it live. Nothing can be refused now — the result is in
                // history and the reservation is retired — so this attaches what it can and says so
                // plainly when it cannot.
                await ReportLateChildRunAsync(toolCallId, cause.ChildRunId);
            }

            ScheduleLoopWake();
        }

        return (ResolveToolCallOutcome.Resolved, null);
    }

    /// <summary>
    /// Durably settles which child run carries this resolution, while refusing the resolution is
    /// still a clean thing to do.
    /// </summary>
    /// <param name="pending">The claim, as it stands after the durable resolution write.</param>
    /// <param name="toolCallId">The call being resolved.</param>
    /// <param name="durable">What that write did.</param>
    /// <param name="ct">The delivering caller's token.</param>
    /// <returns>
    /// The claim to carry forward, and — when the resolution must not proceed — the outcome to
    /// report and the exception the throwing surface should raise. The outcome is null on the path
    /// that continues.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Two situations need a write here, and they are the two where the resolution write did not
    /// name the child run itself: the requesting run parked <em>during</em> that write, so the id
    /// did not exist yet when it went out; or the write found the resolution already committed by an
    /// earlier attempt (<see cref="DeferredResolutionOutcome.Duplicate"/>), which by definition
    /// left the child run named however that attempt left it. The second is why the store reports
    /// the standing id back: an attempt that died before its child ran already named it, and this
    /// delivery must run <em>that</em> run rather than mint a second one for the same result.
    /// </para>
    /// <para>
    /// A store failure is reported as <see cref="ResolveToolCallOutcome.StoreFailed"/> for the same
    /// reason the resolution write itself is: the claim goes back, history is untouched, and the
    /// caller may deliver the result again. That is the whole value of doing it here. What is
    /// <em>not</em> treated as a failure is either way of learning that no child run can be named —
    /// a record that holds no resolved entry, or a store old enough not to implement naming at all.
    /// Neither changes on a retry, and the resolution write has already committed, so refusing would
    /// refuse every redelivery forever. Both proceed: the continuation runs, unrecoverable across a
    /// restart but not lost now.
    /// </para>
    /// </remarks>
    private async Task<(
        ResolvingDeferral Pending,
        ResolveToolCallOutcome? Outcome,
        Exception? Failure
    )> AttachChildRunBeforeHistoryAsync(
        ResolvingDeferral pending,
        string toolCallId,
        DeferredResolutionOutcome durable,
        CancellationToken ct
    )
    {
        var parked = _delayed.MintChildRunIfParked(pending);
        if (parked.ChildRunId == null)
        {
            // The requesting run is still going, so this result folds into it and there is no child
            // run to name.
            return (pending, null, null);
        }

        if (durable == DeferredResolutionOutcome.Resolved && parked.ChildRunId == pending.ChildRunId)
        {
            // This resolution's own write committed the record and carried the id with it.
            return (pending, null, null);
        }

        try
        {
            var standing = await Lifecycle.AttachDeferredChildRunAsync(toolCallId, parked.ChildRunId, ct);
            if (standing != null)
            {
                return (parked with { ChildRunId = standing }, null, null);
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _delayed.AbortResolve(pending);
            return (pending, ResolveToolCallOutcome.Cancelled, ex);
        }
        catch (NotSupportedException ex)
        {
            // A store that predates the member, which is not a failure and not retryable: the
            // resolution write above has already committed, so every redelivery would find it
            // Duplicate, arrive back here, and be refused again — stranding a durably resolved call
            // with history never told about it. Treated as the record refusing to carry a child run,
            // because that is exactly what it is.
            Logger.LogWarning(
                ex,
                "The lifecycle store cannot name child runs, so the continuation of tool call "
                    + "{ToolCallId} runs in this process but could not be recovered after a restart",
                toolCallId
            );
            return (parked, null, null);
        }
        catch (Exception ex)
        {
            _delayed.AbortResolve(pending);
            Logger.LogWarning(
                ex,
                "Could not durably name the child run for the resolution of tool call {ToolCallId}; "
                    + "the call stays deferred and the result can be delivered again",
                toolCallId
            );
            return (pending, ResolveToolCallOutcome.StoreFailed, ex);
        }

        Logger.LogWarning(
            "Durable record holds no resolved entry for tool call {ToolCallId}, so child run "
                + "{ChildRunId} could not be named; the continuation runs in this process but could "
                + "not be recovered after a restart",
            toolCallId,
            parked.ChildRunId
        );
        return (parked, null, null);
    }

    /// <summary>
    /// Names a child run that was minted after the resolution had already been applied, where the
    /// only thing left to do about a failure is to be explicit about it.
    /// </summary>
    /// <remarks>
    /// <see cref="CancellationToken.None"/> deliberately: the resolution has happened, and the token
    /// of whichever caller delivered it must not be able to cancel the record of what that delivery
    /// caused. The two ways this can come to nothing are not the same thing and are not reported as
    /// though they were. No record naming the call at all is ordinary — the same
    /// <see cref="DeferredResolutionOutcome.NotFound"/> case the resolution write itself treats as
    /// normal, reached by a host that injected deferred history the store never saw — and only costs
    /// this continuation its recoverability. A record that names some <em>other</em> child run is a
    /// genuine contradiction: <see cref="AttachChildRunBeforeHistoryAsync"/> has already adopted any
    /// committed name, so nothing should be able to disagree by the time this runs.
    /// </remarks>
    private async Task ReportLateChildRunAsync(string toolCallId, string childRunId)
    {
        string? standing;
        try
        {
            standing = await Lifecycle.AttachDeferredChildRunAsync(toolCallId, childRunId, CancellationToken.None);
        }
        catch (NotSupportedException ex)
        {
            // A store that predates the member. Nothing is wrong with it and nothing is wrong here;
            // this thread simply cannot recover delayed continuations across a restart.
            Logger.LogWarning(
                ex,
                "The lifecycle store cannot name child runs, so the continuation of tool call "
                    + "{ToolCallId} runs in this process but could not be recovered after a restart",
                toolCallId
            );
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Could not durably attach child run {ChildRunId} to the resolution of tool call "
                    + "{ToolCallId}; it still runs in this process, but a crash before it does would "
                    + "leave the result resolved with nothing recorded to carry it",
                childRunId,
                toolCallId
            );
            return;
        }

        if (standing == childRunId)
        {
            return;
        }

        if (standing == null)
        {
            // No record holds this call — the store has no resolved entry to attach anything to.
            // Expected wherever the deferral itself was never recorded, so it is reported as the
            // lost recoverability it is rather than as a disagreement about the child run.
            Logger.LogWarning(
                "Durable record holds no resolved entry for tool call {ToolCallId}, so child run "
                    + "{ChildRunId} could not be named; the result is carried in this process but the "
                    + "continuation would not survive a restart",
                toolCallId,
                childRunId
            );
            return;
        }

        Logger.LogError(
            "Durable record for tool call {ToolCallId} names {Standing} rather than the child run "
                + "{ChildRunId} this process is about to run; the result is carried here but the "
                + "record cannot be relied on to recover it",
            toolCallId,
            standing,
            childRunId
        );
    }

    /// <summary>
    /// Classifies a resolution for a call the coordinator is not tracking, by asking history what
    /// it already holds.
    /// </summary>
    /// <remarks>
    /// Normally this means the call resolved earlier — a redelivery or a contradiction. The third
    /// possibility is a placeholder in history that no reservation covers, which only a host that
    /// injected deferred history itself can produce; rather than let the result strand, it is
    /// adopted (pre-parked, since no live run is waiting on it) and resolved through the normal path.
    /// </remarks>
    private async Task<(ResolveToolCallOutcome Outcome, Exception? Failure)> ResolveUnclaimedAsync(
        string toolCallId,
        string result,
        bool isError,
        IList<ToolResultContentBlock>? contentBlocks,
        bool truncated,
        CancellationToken ct
    )
    {
        var noOp = false;
        ToolCallResultMessage? orphan = null;

        try
        {
            _ = UpdateToolResultByCallId(
                toolCallId,
                existing =>
                {
                    if (existing.IsDeferred)
                    {
                        orphan = existing;
                        return existing;
                    }

                    if (existing.Result == result && existing.IsError == isError)
                    {
                        noOp = true;
                        return existing;
                    }

                    throw new InvalidOperationException(ResolutionConflictMessage(toolCallId));
                }
            );
        }
        catch (InvalidOperationException ex)
        {
            var outcome = ex.Message.Contains("already been resolved", StringComparison.Ordinal)
                ? ResolveToolCallOutcome.Conflict
                : ResolveToolCallOutcome.NotFound;
            return (outcome, ex);
        }

        if (noOp)
        {
            Logger.LogDebug(
                "ResolveToolCallAsync no-op: '{ToolCallId}' was already resolved with identical content",
                toolCallId
            );
            return (ResolveToolCallOutcome.Duplicate, null);
        }

        if (orphan == null)
        {
            return (ResolveToolCallOutcome.Duplicate, null);
        }

        Logger.LogWarning(
            "Tool call {ToolCallId} is deferred in history but was not registered as outstanding; adopting it so the result is not lost",
            toolCallId
        );

        var adopted = new DeferredEntry(
            orphan.ToolCallId!,
            orphan.ToolName ?? string.Empty,
            "{}",
            orphan.DeferredAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            orphan.RunId,
            orphan.GenerationId
        );

        // Pre-parked: whatever run requested this is not the one running now, so its result can
        // only come back as a child run.
        _ = _delayed.TryReserve(adopted, parked: true);

        var fingerprint = ComputeResolutionFingerprint(result, isError);
        if (!_delayed.TryBeginResolve(toolCallId, fingerprint, out var pending, out _))
        {
            // Someone claimed it between the adoption and now; theirs stands.
            return (ResolveToolCallOutcome.Duplicate, null);
        }

        return await ResolveClaimedAsync(pending!, toolCallId, result, isError, contentBlocks, truncated, ct);
    }

    private static ToolCallResultMessage ApplyResolution(
        ToolCallResultMessage existing,
        string result,
        bool isError,
        bool truncated
    )
    {
        return existing with
        {
            Result = result,
            ContentBlocks = null, // history-side is text-only; subscribers get full
            IsError = isError,
            ErrorCode = isError ? "deferred_resolution_error" : null,
            IsDeferred = false,
            ResolvedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            IsTruncated = truncated,
        };
    }

    private static string ResolutionConflictMessage(string toolCallId) =>
        $"Tool call '{toolCallId}' has already been resolved with different content. "
        + "Cannot resolve again with a different value.";

    /// <summary>
    /// A stable digest of what a resolution carries, used to tell a redelivery of the same result
    /// from a genuinely different one without keeping the payloads themselves around.
    /// </summary>
    private static string ComputeResolutionFingerprint(string result, bool isError)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes((isError ? "1\n" : "0\n") + result));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Returns the set of tool calls currently deferred (awaiting external resolution).
    /// Hosts use this to inspect state, render pending UI, or — on process restart —
    /// reconnect external workflows to the calls they're supposed to complete.
    /// </summary>
    public Task<IReadOnlyList<DeferredToolCallInfo>> GetDeferredToolCallsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var snapshot = _delayed
            .Snapshot()
            .Select(e => new DeferredToolCallInfo
            {
                ToolCallId = e.ToolCallId,
                FunctionName = e.FunctionName,
                FunctionArgs = e.FunctionArgs,
                DeferredAtUnixMs = e.DeferredAtUnixMs,
                RunId = e.RunId,
                GenerationId = e.GenerationId,
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<DeferredToolCallInfo>>(snapshot);
    }

    /// <inheritdoc />
    /// <summary>
    ///     Persists a row and tells the compaction runtime the id it got, so the runtime can place the
    ///     in-memory row against the store's <c>Seq</c> when it reconciles before a cut.
    /// </summary>
    protected override Task PersistMessageAsync(IMessage message, string runId, CancellationToken ct)
    {
        if (_compaction is null || Store is null)
        {
            return base.PersistMessageAsync(message, runId, ct);
        }

        PersistedMessage persisted;
        try
        {
            persisted = MessagePersistenceConverter.ToPersistedMessage(message, ThreadId, runId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to persist message");
            return Task.CompletedTask;
        }

        var append = AppendPersistedAsync(persisted, ct);
        _compaction.TrackPersisted(message, persisted.Id, append);
        return append;
    }

    private async Task AppendPersistedAsync(PersistedMessage persisted, CancellationToken ct)
    {
        try
        {
            await Store!.AppendMessagesAsync(ThreadId, [persisted], ct);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to persist message");
        }
    }

    private IReadOnlyList<AgentRef> RosterForCompaction() =>
        SubAgentManager is null
            ? []
            :
            [
                .. SubAgentManager
                    .ListAgents()
                    .Select(a => new AgentRef
                    {
                        AgentId = a.AgentId,
                        Template = a.TemplateName,
                        Task = a.Task,
                        Status = a.Status.ToString().ToLowerInvariant(),
                        ThreadId = a.ThreadId,
                    }),
            ];

    /// <summary>The summary pass's usage lands in the conversation ledger under its own execution kind.</summary>
    private void RecordCompactionUsage(UsageMessage usage, string checkpointId, string? model)
    {
        if (UsageLedger is null)
        {
            return;
        }

        UsageLedger.RecordUsage(
            UsageRecordMapper.FromUsageMessage(usage, ThreadId, UsageExecutionKind.Compaction, model) with
            {
                CompactionCheckpointId = checkpointId,
            }
        );
        _ = PersistCurrentUsageAsync();
    }

    protected override async Task OnHistoryRestoredAsync(IReadOnlyList<IMessage> messages, CancellationToken ct)
    {
        // Pair the restored rows with the store's and reconcile compaction state before anything
        // else reads the history: the view the first turn builds depends on it.
        if (_compaction is not null)
        {
            await _compaction.TrackRestoredAsync(messages ?? [], ct);
        }

        // Rebuild the deferred registry from persisted history. Each ToolCallResultMessage
        // with IsDeferred=true gets re-registered so GetDeferredToolCallsAsync surfaces it
        // and ResolveToolCallAsync can complete it after restart.
        if (messages == null || messages.Count == 0)
        {
            return;
        }

        // Index ToolCallMessages by ToolCallId so we can recover function name/args for
        // each deferred result.
        var toolCallsById = new Dictionary<string, ToolCallMessage>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            if (msg is ToolCallMessage tc && !string.IsNullOrEmpty(tc.ToolCallId))
            {
                toolCallsById[tc.ToolCallId] = tc;
            }
        }

        var restoredCount = 0;
        string? mostRecentDeferringRun = null;
        string? mostRecentDeferringGen = null;

        foreach (var msg in messages)
        {
            if (msg is not ToolCallResultMessage tcr || !tcr.IsDeferred || string.IsNullOrEmpty(tcr.ToolCallId))
            {
                continue;
            }

            _ = toolCallsById.TryGetValue(tcr.ToolCallId, out var sourceCall);

            var entry = new DeferredEntry(
                tcr.ToolCallId,
                sourceCall?.FunctionName ?? tcr.ToolName ?? string.Empty,
                sourceCall?.FunctionArgs ?? "{}",
                tcr.DeferredAt ?? 0,
                tcr.RunId ?? sourceCall?.RunId,
                tcr.GenerationId ?? sourceCall?.GenerationId
            );

            // Restored entries are parked on arrival. The run that requested them belonged to a
            // process that no longer exists, so its result cannot be folded back into it — it can
            // only return as a child run, which is what parked means.
            if (_delayed.TryReserve(entry, parked: true))
            {
                restoredCount++;
            }

            // Remember the last-loaded deferring run so inputs arriving while the conversation is
            // still parked are attributed to it.
            mostRecentDeferringRun = entry.RunId ?? mostRecentDeferringRun;
            mostRecentDeferringGen = entry.GenerationId ?? mostRecentDeferringGen;
        }

        var suppressedRunId = await LoadSuppressedRunMarkerAsync(ct);
        lock (_spawnSuppressionLock)
        {
            _spawnSuppressedRunId = suppressedRunId;
        }

        // Alongside the suppression marker, and for the same reason: both are guarantees the parked
        // run already made that its continuation must keep. Must precede
        // RecoverOwedContinuationsAsync below, which can run a continuation immediately.
        await RestoreRecoveryBudgetAsync(ct);

        if (restoredCount > 0)
        {
            if (mostRecentDeferringRun != null && mostRecentDeferringGen != null)
            {
                _ = _delayed.TryPark(mostRecentDeferringRun, mostRecentDeferringGen, out _);
            }

            Logger.LogInformation(
                "Restored {Count} deferred tool call(s) from persisted history (suppressed run: {SuppressedRunId})",
                restoredCount,
                suppressedRunId ?? "(none)"
            );
        }

        // Re-queue continuations a previous process committed durably but never ran. Runs after the
        // still-deferred set is rebuilt, because whether a recovered result may continue the
        // conversation depends on nothing else being outstanding.
        await RecoverOwedContinuationsAsync(messages, ct);

        // Reconcile restored block waits so no parked Wait is left hanging: a restorable source
        // (e.g. timer) re-arms for its remaining delay; a non-restorable one resolves as
        // trigger_lost_on_restart. Runs after the deferred set is rebuilt so it sees every entry.
        List<DeferredEntry> restoredWaitEntries =
        [
            .. _delayed.Snapshot().Where(e => e.FunctionName == WaitToolProvider.WaitToolName),
        ];

        if (_triggerRuntime != null)
        {
            if (restoredWaitEntries.Count > 0)
            {
                List<RestoredWait> restoredList =
                [
                    .. restoredWaitEntries.Select(e => new RestoredWait(
                        e.ToolCallId,
                        e.FunctionArgs,
                        e.DeferredAtUnixMs
                    )),
                ];
                await _triggerRuntime.ReconcileRestoredAsync(restoredList, ct);
            }
        }
        else if (restoredWaitEntries.Count > 0)
        {
            // Triggers are disabled in this host (or were rolled back after these waits were
            // persisted) — there is no runtime left to re-arm or fail them. Resolve each restored
            // Wait with a terminal failure now rather than leaving the run parked forever.
            foreach (var entry in restoredWaitEntries)
            {
                // Isolate each entry: ResolveToolCallAsync can throw (e.g. an "already resolved with
                // different content" conflict) for one entry's persisted state without that meaning
                // anything about the rest. Without isolation, a throw on entry k would abort the loop
                // and leave entries k+1... parked forever with no runtime left to ever resolve them.
                try
                {
                    await ResolveToolCallAsync(
                        entry.ToolCallId,
                        JsonSerializer.Serialize(
                            new
                            {
                                status = "failed",
                                reason = "trigger_disabled",
                                waitId = entry.ToolCallId,
                            }
                        ),
                        isError: false,
                        contentBlocks: null,
                        ct
                    );
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        ex,
                        "Failed to resolve restored wait {ToolCallId} during trigger-disabled recovery; continuing",
                        entry.ToolCallId
                    );
                }
            }
        }
    }

    /// <summary>
    /// Re-queues the child runs that a previous process committed to but never ran, so a delayed
    /// result that resolved before a crash still reaches the model.
    /// </summary>
    /// <param name="messages">Restored history.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <para>
    /// The durable record is what makes this possible and what keeps it exact. A resolution that
    /// needs a child run records that run's id at the moment it resolves — at claim time when the
    /// requesting run had already parked, or immediately after commit when it parked mid-resolution
    /// (see <see cref="ResolveClaimedAsync"/>). So a record that <em>names</em> a child run for
    /// which no run was ever started is precisely a continuation this thread is owed, and reusing
    /// that same id means a second crash cannot start it twice: the run row is what marks it begun.
    /// </para>
    /// <para>
    /// A resolution recorded with no child run at all is deliberately <b>not</b> recovered. That
    /// record says the result was folded into a run that was still going, and the continuation was
    /// that run's own next turn — so recovering it here would be indistinguishable from resuming any
    /// interrupted run, which is not this mechanism's job and would take a turn nobody asked for.
    /// </para>
    /// </remarks>
    private async Task RecoverOwedContinuationsAsync(IReadOnlyList<IMessage> messages, CancellationToken ct)
    {
        var runs = await Lifecycle.ListRunLifecycleAsync(ct);
        if (runs.Count == 0)
        {
            return;
        }

        // A child run that has a lifecycle row was started, whatever became of it afterwards.
        // Recovering it again would carry the same result to the provider twice.
        var startedRunIds = new HashSet<string>(runs.Select(r => r.RunId), StringComparer.Ordinal);

        var resolvedResults = new Dictionary<string, ToolCallResultMessage>(StringComparer.Ordinal);
        foreach (var msg in messages)
        {
            if (msg is ToolCallResultMessage tcr && !tcr.IsDeferred && !string.IsNullOrEmpty(tcr.ToolCallId))
            {
                resolvedResults[tcr.ToolCallId] = tcr;
            }
        }

        List<RecoveredContinuation> owed = [];
        foreach (
            var (run, call) in runs.SelectMany(r => r.DeferredToolCalls.Select(d => (Run: r, Call: d)))
                .Where(x => x.Call.IsResolved && x.Call.ChildRunId != null)
                .Where(x => !startedRunIds.Contains(x.Call.ChildRunId!))
                .OrderBy(x => x.Call.ResolvedAt)
                .ThenBy(x => x.Call.Ordinal)
        )
        {
            // History has the last word, as everywhere else in this file: without the resolved
            // result in it there is nothing for a child run to carry, and a placeholder still marked
            // deferred belongs to the ordinary resolve path, not to recovery.
            if (!resolvedResults.TryGetValue(call.ToolCallId, out var result))
            {
                Logger.LogWarning(
                    "Delayed result for tool call {ToolCallId} was recorded as resolved with child run "
                        + "{ChildRunId}, but restored history holds no resolved result for it; skipping",
                    call.ToolCallId,
                    call.ChildRunId
                );
                continue;
            }

            owed.Add(
                new RecoveredContinuation(
                    ToolCallId: call.ToolCallId,
                    ToolName: call.ToolName,
                    RequestingRunId: run.RunId,
                    RequestingGenerationId: call.GenerationId ?? run.GenerationId,
                    ChildRunId: call.ChildRunId!,
                    Result: result
                )
            );
        }

        if (owed.Count == 0)
        {
            return;
        }

        var recovered = _delayed.RecoverCauses(owed);
        if (recovered.Count == 0)
        {
            return;
        }

        Logger.LogInformation(
            "Recovered {Count} delayed tool result continuation(s) that a previous process committed but "
                + "never ran: {ToolCallIds}",
            recovered.Count,
            string.Join(", ", recovered.Select(c => c.ToolCallId))
        );

        ScheduleLoopWake();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs on every recovery — even threads with zero persisted messages — because
    /// <c>notify_waits</c> rows are keyed by thread in their own table, independent of message
    /// history. This is deliberately separate from the block-wait reconcile in
    /// <see cref="OnHistoryRestoredAsync"/>, which legitimately needs restored message history
    /// and therefore does not run for message-less threads.
    /// </remarks>
    protected override async Task OnThreadRecoveredAsync(CancellationToken ct)
    {
        if (_triggerRuntime != null)
        {
            await _triggerRuntime.RestoreNotifyWaitsAsync(ct);
        }
    }

    /// <summary>
    /// Nudges <see cref="RunLoopAsync"/> awake so it drains the causes the coordinator now holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The causes themselves never go on the input channel. That channel is bounded, and a turn
    /// with many deferred tool calls would resolve many results at once — writing one item per
    /// result would block resolution threads behind a full queue, on a path where blocking means
    /// stranding a result that already exists. So the coordinator holds them and this writes a
    /// single content-free wake-up.
    /// </para>
    /// <para>
    /// One wake-up is enough for any number of causes: the loop drains one cause per iteration and
    /// loops straight back to the drain, so it keeps going until the coordinator is empty without
    /// needing to be woken again. <see cref="_wakeScheduled"/> is what makes "one" the actual
    /// count — it is cleared by the loop when it consumes the wake-up, and restored here whenever
    /// the write does not land so a wake-up is never silently lost.
    /// </para>
    /// <para>
    /// The write is bound to <see cref="MultiTurnAgentBase.LifetimeToken"/>, never to the token of
    /// the caller that delivered the result. A resolution arrives on an arbitrary thread whose token
    /// may be cancelled the instant the call returns — a request completing, a webhook handler
    /// timing out — and by then the cause is already committed. Cancelling the wake-up there would
    /// leave the continuation queued and the loop asleep with nothing scheduled to stir it.
    /// </para>
    /// </remarks>
    private void ScheduleLoopWake()
    {
        lock (_wakeLock)
        {
            if (_wakeScheduled)
            {
                return;
            }

            _wakeScheduled = true;
        }

        _ = WriteLoopWakeAsync();
    }

    private async Task WriteLoopWakeAsync()
    {
        // The sentinel's payload is inert: Resume marks it as a wake-up and Messages is empty, so
        // the loop recognises it and contributes nothing to history.
        var wake = new QueuedInput(
            new UserInput([], InputId: null, ParentRunId: null),
            ReceiptId: $"wake:{Guid.NewGuid():N}",
            QueuedAt: DateTimeOffset.UtcNow,
            Resume: new ResumeSentinel(string.Empty, string.Empty)
        );

        try
        {
            await EnqueueRawAsync(wake, LifetimeToken);
        }
        catch (OperationCanceledException)
        {
            // Disposal. Nothing is going to run these causes now, but the flag has to come back
            // regardless: leaving it set would make a wake-up scheduled by a later resolution
            // return early and never write anything at all.
            ResetWakeScheduled();
            Logger.LogDebug(
                "Loop wake-up for {Count} pending delayed tool result(s) was abandoned at shutdown",
                _delayed.PendingCauseCount
            );
        }
        catch (Exception ex)
        {
            ResetWakeScheduled();

            // Not fatal: the loop drains causes at the top of every iteration, so these still run
            // as soon as anything else stirs it. Only an otherwise-idle loop stays asleep, until
            // the next resolution or input arrives.
            Logger.LogWarning(
                ex,
                "Failed to wake the loop for {Count} pending delayed tool result(s); they will run when the loop next stirs",
                _delayed.PendingCauseCount
            );
        }
    }

    private void ResetWakeScheduled()
    {
        lock (_wakeLock)
        {
            _wakeScheduled = false;
        }
    }

    /// <summary>
    /// Injects a notify-mode trigger fire as a fresh user turn through the same internal gate
    /// ResumeSentinel uses. Resume is null and Messages is non-empty, so RunLoopAsync adds it to
    /// history and drives a new run — queued strictly behind any in-flight turn (never interrupting,
    /// per locked decision #1). Supplied to <see cref="TriggerRuntime"/> as its notify delegate.
    /// </summary>
    private async Task EnqueueTriggerNotifyAsync(string payload, bool isError, CancellationToken ct)
    {
        var queued = BuildTriggerNotifyInput(payload, isError);

        try
        {
            await EnqueueRawAsync(queued, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected on shutdown.
        }
        catch (ObjectDisposedException)
        {
            // Loop torn down mid-fire — drop the envelope.
        }
    }

    /// <summary>
    /// Non-blocking counterpart to <see cref="EnqueueTriggerNotifyAsync"/>, supplied to
    /// <see cref="TriggerRuntime"/> as its <c>tryNotify</c> delegate — used ONLY during restart
    /// recovery (<see cref="TriggerRuntime.RestoreNotifyWaitsAsync"/>). Recovery runs before the run
    /// loop starts reading the (bounded) input channel, so restore-time terminal envelopes must never
    /// block on a full channel: enough restored terminal rows could otherwise fill the channel and
    /// deadlock startup (the loop can't drain it until <see cref="RunLoopAsync"/> starts, which
    /// itself may be gated behind this very recovery call completing). This attempts a non-blocking
    /// write and reports whether it was accepted; the runtime only deletes the persisted
    /// <c>notify_waits</c> row when this returns true, so a rejected envelope is naturally retried on
    /// the next recovery instead of being lost.
    /// </summary>
    private bool TryEnqueueTriggerNotify(string payload, bool isError)
    {
        var queued = BuildTriggerNotifyInput(payload, isError);

        try
        {
            return TryEnqueueRaw(queued);
        }
        catch (ObjectDisposedException)
        {
            // Loop torn down mid-restore — nothing to inject into; report not-delivered so the
            // caller retains the row rather than treating a dropped envelope as delivered.
            return false;
        }
    }

    private static QueuedInput BuildTriggerNotifyInput(string payload, bool isError)
    {
        var envelope = new TextMessage { Role = Role.User, Text = $"<trigger>\n{payload}\n</trigger>" };
        var input = new UserInput([envelope], InputId: null, ParentRunId: null);
        return new QueuedInput(
            input,
            ReceiptId: $"notify:{Guid.NewGuid():N}",
            QueuedAt: DateTimeOffset.UtcNow,
            Resume: null,
            Trigger: new TriggerEnvelope(isError)
        );
    }

    private bool ContinuesSuppressedRun(string requestingRunId)
    {
        lock (_spawnSuppressionLock)
        {
            return _spawnSuppressedRunId is not null
                && string.Equals(_spawnSuppressedRunId, requestingRunId, StringComparison.Ordinal);
        }
    }

    private sealed class RunSpawnSuppression(MultiTurnAgentLoop owner) : IDisposable
    {
        private IDisposable? _scope;
        private bool _disposed;

        internal bool IsLatched { get; private set; }

        internal bool LatchIfContinuing(string? requestingRunId) =>
            Latch(requestingRunId is not null && owner.ContinuesSuppressedRun(requestingRunId));

        internal bool LatchIfRequested(IReadOnlyList<QueuedInput> inputs) =>
            Latch(inputs.Any(i => i.Input.SuppressSubAgentSpawning));

        private bool Latch(bool requested)
        {
            if (!requested || IsLatched || _disposed)
            {
                return false;
            }

            IsLatched = true;
            _scope = owner.SubAgentTools?.SuppressSpawning();
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _scope?.Dispose();
            _scope = null;
        }
    }
}
