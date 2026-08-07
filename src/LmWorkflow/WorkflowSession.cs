using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using AchieveAi.LmDotnetTools.LmWorkflow.Collaboration;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Prompts;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmWorkflow;

/// <summary>
///     Starts and hosts a controller-driven workflow: it wires a <see cref="WorkflowRuntime"/> and its
///     <see cref="WorkflowToolProvider"/> into a <see cref="MultiTurnAgentLoop"/>, drives the controller
///     LLM with the objective, and observes the run stream to correlate blocking sub-agent spawns back to
///     the authored tasks they fulfill.
/// </summary>
public static class WorkflowSession
{
    /// <summary>
    ///     The nudge handed to a resumed controller as the initial user message: its full prior conversation
    ///     is restored from the conversation store first, so it only needs to re-read the workflow and continue.
    /// </summary>
    internal const string ResumeObjective =
        "Resume the workflow from its persisted state. Call GetWorkflow to read the current node and its "
        + "ready-to-spawn nextExpectedAction unit(s), then continue driving it to completion.";

    /// <summary>
    ///     Starts a workflow run and returns a handle whose <see cref="WorkflowRunHandle.Completion"/>
    ///     completes when the controller advances into a terminal node (after the observer has recorded all
    ///     preceding sub-agent results).
    /// </summary>
    /// <param name="objective">The objective handed to the controller as the initial user message.</param>
    /// <param name="inputs">Optional inputs merged into the runtime's inputs channel.</param>
    /// <param name="definition">An optional pre-authored definition; when null the controller authors one via SetWorkflow.</param>
    /// <param name="subAgentOptions">The sub-agent templates available to the controller.</param>
    /// <param name="controllerAgent">The controller LLM that authors and drives the workflow.</param>
    /// <param name="threadId">The conversation thread id for the controller loop.</param>
    /// <param name="store">An optional workflow store; when supplied with <paramref name="instanceId"/> the runtime persists a snapshot after every mutation so the run can be resumed.</param>
    /// <param name="instanceId">
    ///     The instance id to persist under; required for persistence to be enabled. It is used as the
    ///     snapshot store correlation key AND written to logs on a persistence failure, so callers MUST supply
    ///     an OPAQUE, non-user-identifying value (not an email / tenant / customer id).
    /// </param>
    /// <param name="conversationStore">An optional conversation store; when supplied the controller's history is persisted under <paramref name="threadId"/> (and recoverable on resume).</param>
    /// <param name="logger">An optional logger; forwarded to the runtime so swallowed best-effort persistence faults are surfaced at Warning.</param>
    /// <param name="schemaValidator">An optional JSON-Schema validator the runtime validates task/terminal outputs with.</param>
    /// <param name="includeAuthoringTool">
    ///     When <c>true</c> (default) the controller loop exposes the <c>SetWorkflow</c> authoring tool.
    ///     Pass <c>false</c> when the controller always receives a pre-authored <paramref name="definition"/>
    ///     and must not be able to author/replace it (e.g. a <c>StartWorkflowAgent</c>-launched controller).
    /// </param>
    /// <param name="controllerMaxTurnsPerRun">
    ///     An optional bound on the controller loop's turns per run; <c>null</c> keeps the loop's default (50).
    /// </param>
    /// <param name="controllerDefaultOptions">
    ///     Optional request defaults (notably <c>ModelId</c>) for the controller loop, so the controller runs
    ///     on a fixed, pre-configured model rather than the provider agent's hardcoded default.
    /// </param>
    /// <param name="usageSink">
    ///     Optional external root usage sink (issue #196). When supplied, the controller loop's own turns AND
    ///     its task sub-agents' usage fold into it, so an isolated workflow run's token spend rolls up into the
    ///     originating conversation's total. Null keeps usage scoped to the controller loop only.
    /// </param>
    /// <param name="ct">A cancellation token bound to the run.</param>
    /// <param name="lifecycleServices">
    ///     Optional lifecycle observation for the controller loop. Any approval gate on it is dropped —
    ///     see <see cref="MultiTurnLifecycleServices.ForObservationOnly"/> for why a workflow controller
    ///     is never gated. Declared after <paramref name="ct"/> so existing positional callers keep
    ///     compiling, matching how <c>WorkflowManager.StartAsync</c> already grew.
    /// </param>
    /// <param name="callerCollaboration">
    ///     The LAUNCHING agent's own collaboration handle (issue #244). When supplied, the controller is
    ///     admitted as a visible node in that hierarchy — at the caller's own delegation depth, because a
    ///     controller is a zero-cost hop — and its delegates land one delegation hop deeper. Null (the
    ///     default) keeps the run outside any collaboration, exactly as before.
    /// </param>
    /// <exception cref="WorkflowCollaborationException">
    ///     Collaboration was requested but the controller could not be admitted (nested launch, agent cap
    ///     reached, or a directory refusal). Thrown before any loop is built.
    /// </exception>
    public static Task<WorkflowRunHandle> StartAsync(
        string objective,
        JsonObject? inputs,
        WorkflowDefinition? definition,
        SubAgentOptions subAgentOptions,
        IStreamingAgent controllerAgent,
        string threadId,
        IWorkflowStore? store = null,
        string? instanceId = null,
        IConversationStore? conversationStore = null,
        ILogger? logger = null,
        IJsonSchemaValidator? schemaValidator = null,
        bool includeAuthoringTool = true,
        int? controllerMaxTurnsPerRun = null,
        GenerateReplyOptions? controllerDefaultOptions = null,

        IUsageSink? usageSink = null,
        CancellationToken ct = default,
        MultiTurnLifecycleServices? lifecycleServices = null,
        AgentCollaborationSetup? callerCollaboration = null

    )
    {
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(subAgentOptions);
        ArgumentNullException.ThrowIfNull(controllerAgent);
        ArgumentException.ThrowIfNullOrEmpty(threadId);

        var runtime = new WorkflowRuntime(schemaValidator, logger);
        if (definition is not null)
        {
            runtime.LoadDefinition(definition);
        }

        if (inputs is not null)
        {
            runtime.MergeInputs(inputs);
        }

        // Admit the controller BEFORE the store is attached (so the very first persisted snapshot already
        // carries the collaboration node) and before the loop is built (so a capacity refusal costs nothing).
        var registration = WorkflowCollaboration.TryAdmitController(
            callerCollaboration,
            workflowId: instanceId ?? threadId,
            definition,
            threadId,
            conversationStore,
            () => runtime.IsComplete
        );
        runtime.AttachCollaboration(registration?.Record);

        try
        {
            // Attach AFTER seeding so the first persisted snapshot (taken at the first controller mutation)
            // already reflects the loaded definition and merged inputs.
            if (store is not null && instanceId is not null)
            {
                runtime.AttachStore(store, instanceId);
            }

            var loop = BuildLoop(
                controllerAgent,
                runtime,
                threadId,
                subAgentOptions,
                conversationStore,
                includeAuthoringTool,
                controllerMaxTurnsPerRun,
                controllerDefaultOptions,

                usageSink,
                logger,
                lifecycleServices,
                registration?.Setup

            );

            // A fresh workflow launch must begin with an EMPTY controller conversation. The controller thread
            // id is workflow-{workflowId} (caller-chosen); if it collides with an earlier run's thread in the
            // shared conversation store, MultiTurnAgentBase.RunAsync would otherwise auto-recover that PRIOR
            // run's messages and the controller would "inherit" a previous workflow conversation. Recovery is
            // reserved for the deliberate ResumeAsync path, which calls RecoverAsync explicitly.
            loop.SuppressHistoryRecovery();

            return Task.FromResult(
                BeginRun(loop, runtime, objective, TryBuildRepairer(subAgentOptions, logger), ct, registration)
            );
        }
        catch
        {
            // The controller was admitted but no handle will exist to dispose, so settle its node here or the
            // caller's hierarchy would permanently lose a capacity permit to a launch that never ran.
            registration?.Finish(succeeded: false);
            throw;
        }
    }

    /// <summary>
    ///     Resumes a previously-persisted workflow: loads its latest snapshot, rebuilds the runtime with
    ///     orphaned in-flight tasks reset, restores the controller's conversation history, and continues the
    ///     run. The returned handle behaves exactly like a freshly started one.
    /// </summary>
    /// <param name="instanceId">
    ///     The instance id whose snapshot is loaded and re-persisted under. It is used as the snapshot store
    ///     correlation key AND written to logs on a persistence failure, so callers MUST supply an OPAQUE,
    ///     non-user-identifying value (not an email / tenant / customer id).
    /// </param>
    /// <param name="store">The workflow store holding the snapshot.</param>
    /// <param name="subAgentOptions">The sub-agent templates available to the resumed controller.</param>
    /// <param name="controllerAgent">The controller LLM that continues driving the workflow.</param>
    /// <param name="threadId">The same conversation thread id the run was started under.</param>
    /// <param name="conversationStore">The conversation store the controller history was persisted to; when supplied, history is recovered before driving.</param>
    /// <param name="logger">An optional logger; forwarded to the runtime so swallowed best-effort persistence faults are surfaced at Warning.</param>
    /// <param name="schemaValidator">An optional JSON-Schema validator the runtime validates task/terminal outputs with.</param>
    /// <param name="ct">A cancellation token bound to the run.</param>
    /// <param name="lifecycleServices">
    ///     Optional lifecycle observation for the resumed controller loop. Any approval gate on it is
    ///     dropped — see <see cref="MultiTurnLifecycleServices.ForObservationOnly"/>.
    /// </param>
    /// <param name="callerCollaboration">
    ///     The live agent's collaboration handle to re-admit the resumed controller under (issue #244). The
    ///     original launcher no longer exists after a restart, so the resumed node reacquires a capacity lease
    ///     under the CURRENT hierarchy — visibly failing when the configured cap cannot admit it — while
    ///     reusing the role and description captured in the snapshot verbatim. Null keeps the resumed run
    ///     outside any collaboration, which is also what a pre-#244 snapshot (no persisted node) yields.
    /// </param>
    /// <exception cref="InvalidOperationException">No snapshot exists for <paramref name="instanceId"/>.</exception>
    /// <exception cref="WorkflowCollaborationException">
    ///     Collaboration was requested but the resumed controller could not reacquire capacity.
    /// </exception>
    public static async Task<WorkflowRunHandle> ResumeAsync(
        string instanceId,
        IWorkflowStore store,
        SubAgentOptions subAgentOptions,
        IStreamingAgent controllerAgent,
        string threadId,
        IConversationStore? conversationStore = null,
        ILogger? logger = null,
        IJsonSchemaValidator? schemaValidator = null,
        CancellationToken ct = default,
        MultiTurnLifecycleServices? lifecycleServices = null,
        AgentCollaborationSetup? callerCollaboration = null
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subAgentOptions);
        ArgumentNullException.ThrowIfNull(controllerAgent);
        ArgumentException.ThrowIfNullOrEmpty(threadId);

        var snapshot =
            await store.LoadAsync(instanceId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Cannot resume: no persisted workflow snapshot found for instance '{instanceId}'."
            );

        // Rebuild the runtime (orphaned in-flight tasks reset) and keep persisting under the same id.
        var runtime = WorkflowRuntime.FromSnapshot(snapshot, schemaValidator, logger);

        // Reacquire the collaboration lease BEFORE the run continues, so a resume the configured cap cannot
        // admit fails here rather than running unbounded. A pre-#244 snapshot carries no node record, so the
        // controller is admitted with freshly derived metadata instead of resurrecting nothing.
        var registration = WorkflowCollaboration.TryAdmitController(
            callerCollaboration,
            instanceId,
            snapshot.Definition,
            threadId,
            conversationStore,
            () => runtime.IsComplete,
            snapshot.Collaboration
        );
        runtime.AttachCollaboration(registration?.Record);

        try
        {
            runtime.AttachStore(store, instanceId);

            var loop = BuildLoop(
                controllerAgent, runtime, threadId, subAgentOptions, conversationStore,
                logger: logger, lifecycleServices: lifecycleServices, collaboration: registration?.Setup);


            // Restore the controller's prior conversation BEFORE driving so it continues with full context.
            // Doing it explicitly here also marks recovery complete so RunAsync does not re-recover.
            if (conversationStore is not null)
            {
                _ = await loop.RecoverAsync(ct).ConfigureAwait(false);
            }

            return BeginRun(
                loop, runtime, ResumeObjective, TryBuildRepairer(subAgentOptions, logger), ct, registration
            );
        }
        catch
        {
            // See StartAsync: a reacquired lease with no handle to dispose would strand a capacity permit.
            registration?.Finish(succeeded: false);
            throw;
        }
    }

    /// <summary>Builds the controller loop over a fresh registry carrying the workflow tools.</summary>
    private static MultiTurnAgentLoop BuildLoop(
        IStreamingAgent controllerAgent,
        WorkflowRuntime runtime,
        string threadId,
        SubAgentOptions subAgentOptions,
        IConversationStore? conversationStore,
        bool includeAuthoringTool = true,
        int? maxTurnsPerRun = null,
        GenerateReplyOptions? controllerDefaultOptions = null,

        IUsageSink? usageSink = null,
        ILogger? logger = null,
        MultiTurnLifecycleServices? lifecycleServices = null,
        AgentCollaborationSetup? collaboration = null

    )
    {
        var registry = new FunctionRegistry();
        _ = registry.AddProvider(new WorkflowToolProvider(runtime, includeSetWorkflow: includeAuthoringTool));

        // Wire the self-correcting spawn-name gate (Option A). The controller correlates delegate results to
        // workflow units by EXACT name only, so a mis-named Agent spawn would run and be silently discarded,
        // leaving the unit pending and provoking a re-spawn loop. This rejects it up front at the Agent-tool
        // boundary with an actionable correction (the ready unit name(s)) so the controller re-issues the exact
        // name. Runtime backstop to the ControllerSystemPrompt guidance; covers StartAsync AND ResumeAsync.
        // The metadata resolver rides the same exact-name correlation: a delegate's collaboration role and
        // description come from the authored task/node labels, so the controller cannot relabel what the
        // directory advertises to the rest of the collaboration.
        subAgentOptions = subAgentOptions with
        {
            SpawnNameGate = runtime.DescribeSpawnNameRejection,
            SpawnModelSelectionResolver = runtime.ResolveSpawnModelSelection,
            SpawnMetadataResolver = runtime.ResolveSpawnMetadata,
        };

        return new MultiTurnAgentLoop(
            controllerAgent,
            registry,
            threadId,
            systemPrompt: ControllerSystemPrompt.Default,
            // Pin the controller's model (and any other request defaults) so it never falls back to the
            // provider agent's hardcoded default model.
            defaultOptions: controllerDefaultOptions,
            // Fall back to MultiTurnAgentLoop's own default (50) when the caller does not bound it.
            maxTurnsPerRun: maxTurnsPerRun ?? 50,
            store: conversationStore,
            subAgentOptions: subAgentOptions,

            // Give the isolated controller loop (and its SubAgentManager) a logger so their structured logs —
            // notably the tool-transparency merge and per-delegate inherited-tool set — reach the same sink as
            // the rest of the workflow. The loop wants ILogger<MultiTurnAgentLoop>; adapt the workflow's
            // non-generic logger (category preserved).
            logger: logger is null ? null : new ControllerLoopLogger<MultiTurnAgentLoop>(logger),
            // Fold the isolated controller loop's own turns AND its task sub-agents' usage into the
            // originating conversation's root sink when one is supplied (#196).
            externalUsageSink: usageSink,
            lifecycleServices: MultiTurnLifecycleServices.ForObservationOnly(lifecycleServices),
            // Only the controller's workflow control-plane tools are exempt from approval. Delegates can
            // inherit the launching host's domain tools, so they must retain that host approval gate.
            subAgentLifecycleServices: lifecycleServices,
            // The controller is ALREADY registered (WorkflowCollaboration took its capacity lease and owns
            // its endpoints), so the loop's own self-registration finds it and no-ops. Handing the setup down
            // is what lets the controller's SubAgentManager admit delegates one delegation hop deeper.
            collaboration: collaboration,
            // A workflow controller has no browser socket of its own, so deferred browser-hosted tools
            // would be unresolvable here. Delegates still receive eligible external tools through the
            // existing inheritable-tool snapshot.
            includeAskUserQuestionTool: false,
            includeNotifyClientTool: false
        );
    }

    /// <summary>
    ///     Adapts the workflow's non-generic <see cref="ILogger"/> to the <see cref="ILogger{T}"/> the
    ///     controller loop's ctor expects, so the loop's and its SubAgentManager's structured logs flow to the
    ///     same sink. The wrapped logger's category is preserved (it is already a workflow-category logger).
    /// </summary>
    private sealed class ControllerLoopLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => inner.Log(logLevel, eventId, state, exception, formatter);
    }

    /// <summary>
    ///     Builds the best-effort JSON-repair helper for a run, or <c>null</c> when repair is not wired
    ///     (auto-on-when-wired). Repair is enabled only when the host supplied BOTH a tier model resolver and a
    ///     tier agent factory (the cheap-tier ladder) AND the lowest tier resolves to a concrete model — the
    ///     same lowest-available-tier wiring the plain spawn path uses. Any construction fault degrades to
    ///     "repair disabled for this run" rather than failing the workflow.
    /// </summary>
    private static WorkflowJsonRepairer? TryBuildRepairer(SubAgentOptions subAgentOptions, ILogger? logger)
    {
        if (
            subAgentOptions.TierModelResolver is not { } resolveTier
            || subAgentOptions.TierAgentFactory is not { } buildTierAgent
        )
        {
            return null;
        }

        var model = resolveTier(0);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        try
        {
            return new WorkflowJsonRepairer(buildTierAgent(model), model, logger);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not build the cheap-tier JSON repair agent; repair disabled for this run");
            return null;
        }
    }

    /// <summary>Starts the loop, drives + observes it from a single ordered consumer, and wraps a handle.</summary>
    private static WorkflowRunHandle BeginRun(
        MultiTurnAgentLoop loop,
        WorkflowRuntime runtime,
        string initialMessage,
        WorkflowJsonRepairer? repairer,
        CancellationToken ct,
        WorkflowControllerRegistration? registration
    )
    {
        // Bind the loop to the controller's collaboration endpoint BEFORE the run starts, so a peer message
        // that arrives during the very first turn is delivered rather than refused as "not running".
        registration?.AttachLoop(loop);

        // DriveAndObserveAsync below is that single ordered consumer. Declare it BEFORE the loop can execute
        // any tool so a transition can wait for the observer to catch up instead of routing off stale state.
        runtime.AttachOrderedObserver();

        var runTask = loop.RunAsync(ct);

        // The controller pump runs on its own task. If it does NOT run to completion — it FAULTS, or it
        // propagates an OperationCanceledException while nothing was cancelled (which the async state machine
        // surfaces as a CANCELED task, not a faulted one) — before the drive enumeration observes a run
        // completion, the consumer awaiting Completion would otherwise hang forever. Fault Completion with the
        // pump's exception so the wait resolves. SignalFailure uses TrySetException (first-wins), so a normal
        // completion or a cancellation already signalled by the drive is unaffected. NotOnRanToCompletion
        // covers both the faulted and canceled antecedent states.
        _ = runTask.ContinueWith(
            t =>
                runtime.SignalFailure(
                    t.Exception?.GetBaseException()
                        ?? new OperationCanceledException("The controller run pump was cancelled.")
                ),
            CancellationToken.None,
            TaskContinuationOptions.NotOnRanToCompletion,
            TaskScheduler.Default
        );

        var input = new UserInput([new TextMessage { Text = initialMessage, Role = Role.User }]);
        var driveTask = DriveAndObserveAsync(loop, runtime, input, repairer, ct);

        return new WorkflowRunHandle(runtime, loop, runTask, driveTask, registration);
    }

    /// <summary>
    ///     Enumerates the controller run as the single ordered consumer of its stream: each message is
    ///     observed in publish order (so a sub-agent result is recorded before any later transition is
    ///     reached) and, when the enumeration drains, the runtime is signalled complete.
    /// </summary>
    private static async Task DriveAndObserveAsync(
        MultiTurnAgentLoop loop,
        WorkflowRuntime runtime,
        UserInput objectiveInput,
        WorkflowJsonRepairer? repairer,
        CancellationToken ct
    )
    {
        try
        {
            await foreach (var message in loop.ExecuteRunAsync(objectiveInput, ct).ConfigureAwait(false))
            {
                // Whether a recovery frame ends the stream is a property of its REASON, not of its type.
                // `ReplayTruncated` only says the run's already-published prefix was withheld from this
                // subscription; the live tail still follows on it, so the drive loop keeps consuming —
                // and skips the frame, which is a control signal about the subscription rather than
                // workflow content the runtime should observe. Any other reason means this consumer was
                // dropped and receives nothing further: draining out of the loop from there would signal
                // completion and report a truncated workflow as a successful one, so fail it explicitly.
                if (message is StreamRecoveryMessage recovery)
                {
                    if (recovery.Reason == StreamRecoveryReason.ReplayTruncated)
                    {
                        continue;
                    }

                    runtime.SignalFailure(
                        new InvalidOperationException(
                            $"The workflow controller's message stream was severed ({recovery.Reason}) before the run completed."
                        )
                    );
                    return;
                }

                // Before a schema'd sub-agent result is recorded, give a cheap LLM one chance to rewrite an
                // invalid reply into schema-valid JSON (auto-on only when the cheap tier is wired). This is the
                // only genuinely async, lock-free point upstream of the runtime's synchronous, Monitor-locked
                // recording, so the repair must happen HERE, not inside Observe.
                var observed =
                    repairer is null
                        ? message
                        : await MaybeRepairSpawnResultAsync(runtime, repairer, message, ct).ConfigureAwait(false);
                Observe(runtime, observed);
            }

            runtime.SignalCompletion();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (runtime.IsComplete)
            {
                runtime.SignalCompletion();
            }
            else
            {
                runtime.SignalFailure(new OperationCanceledException(ct));
            }
        }
        catch (Exception ex)
        {
            runtime.SignalFailure(ex);
        }
    }

    /// <summary>Delegates stream-event correlation to the runtime's own observer entry point.</summary>
    private static void Observe(WorkflowRuntime runtime, IMessage message) => runtime.ObserveMessage(message);

    /// <summary>
    ///     Best-effort JSON repair for a single stream message, returning the message the runtime should
    ///     observe. A message is a repair candidate ONLY when it is a successful (non-error) tool result for a
    ///     correlated, schema'd spawn whose text does not already satisfy the schema; everything else passes
    ///     through untouched. On a candidate, the cheap repair agent is asked to rewrite the text, and the
    ///     rewrite is substituted ONLY when it now validates — so repair can turn a would-be failure into a
    ///     recorded success but can never regress the deterministic failure path (a null / still-invalid
    ///     rewrite yields the original message, which the runtime records exactly as before).
    /// </summary>
    internal static async Task<IMessage> MaybeRepairSpawnResultAsync(
        WorkflowRuntime runtime,
        WorkflowJsonRepairer repairer,
        IMessage message,
        CancellationToken ct
    )
    {
        if (message is not ToolCallResultMessage { IsError: false, ToolCallId: { } toolCallId } result)
        {
            return message;
        }

        var check = runtime.CheckSpawnResult(toolCallId, result.Result);
        if (!check.HasSchema || check.IsValid)
        {
            // Not a schema'd spawn, or the reply already validates — nothing to repair.
            return message;
        }

        var repaired = await repairer.TryRepairAsync(result.Result, check.SchemaJson!, ct).ConfigureAwait(false);
        if (repaired is null || !runtime.CheckSpawnResult(toolCallId, repaired).IsValid)
        {
            // Repair produced nothing usable, or a rewrite that STILL does not satisfy the schema: keep the
            // original so the runtime's retry/terminal-failure policy runs unchanged.
            return message;
        }

        return result with { Result = repaired };
    }
}

/// <summary>
///     A handle to a running workflow: exposes the controller <see cref="Loop"/>, the run
///     <see cref="Completion"/>, and READ-ONLY host views of the workflow state
///     (<see cref="Result"/>/<see cref="Outputs"/>/<see cref="State"/>/<see cref="Notes"/>/
///     <see cref="IsComplete"/>/<see cref="CurrentNodeId"/>). The mutable <see cref="Runtime"/> is kept
///     internal so a host cannot bypass the controller and drive a transition itself — the V1 invariant is
///     that the controller decides every transition. Disposing the handle joins the observer task and
///     disposes the loop.
/// </summary>
public sealed class WorkflowRunHandle : IAsyncDisposable
{
    private readonly Task _runTask;
    private readonly Task _driveTask;
    private readonly WorkflowControllerRegistration? _collaboration;

    internal WorkflowRunHandle(
        WorkflowRuntime runtime,
        MultiTurnAgentLoop loop,
        Task runTask,
        Task driveTask,
        WorkflowControllerRegistration? collaboration = null
    )
    {
        Runtime = runtime;
        Loop = loop;
        _runTask = runTask;
        _driveTask = driveTask;
        _collaboration = collaboration;
    }

    /// <summary>
    ///     This run's controller node in the launching hierarchy's collaboration, or <c>null</c> when the run
    ///     is not part of one. Persisted into the workflow snapshot so a resume can reacquire under the same
    ///     identity and with the same trusted role/description.
    /// </summary>
    public CollaborationNodeRecord? CollaborationNode => _collaboration?.Record;

    /// <summary>The runtime that holds all workflow state. Internal so hosts cannot bypass the controller.</summary>
    internal WorkflowRuntime Runtime { get; }

    /// <summary>The controller loop driving the workflow.</summary>
    public MultiTurnAgentLoop Loop { get; }

    /// <summary>Completes when the workflow reaches a terminal node; faults if the controller run throws.</summary>
    public Task Completion => Runtime.Completion;

    /// <summary>The validated final result captured at completion, or <c>null</c> (a deep copy).</summary>
    public JsonNode? Result => Runtime.Result;

    /// <summary>The per-node task outputs channel (a deep copy; mutating it does not change runtime state).</summary>
    public JsonObject Outputs => Runtime.Outputs;

    /// <summary>The mutable state channel (a deep copy; mutating it does not change runtime state).</summary>
    public JsonObject State => Runtime.State;

    /// <summary>The scoped notes channel (a deep copy; mutating it does not change runtime state).</summary>
    public JsonObject Notes => Runtime.Notes;

    /// <summary>Whether the workflow has advanced into a terminal node.</summary>
    public bool IsComplete => Runtime.IsComplete;

    /// <summary>The id of the node the controller is currently positioned on, or <c>null</c>.</summary>
    public string? CurrentNodeId => Runtime.CurrentNodeId;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Dispose the loop FIRST: it stops the controller pump and completes its output channels, which
        // releases the drive enumeration even if the pump faulted WITHOUT publishing a run completion (the
        // hang the pump continuation in BeginRun guards against by faulting Completion). Then observe BOTH
        // background tasks so neither fault goes unobserved — each is already surfaced via Completion
        // (SignalFailure / SignalCompletion).
        await Loop.DisposeAsync().ConfigureAwait(false);

        try
        {
            await _driveTask.ConfigureAwait(false);
        }
        catch
        {
            // The drive task's failure is already surfaced via Completion; disposal must not throw.
        }

        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch
        {
            // The pump fault is surfaced via Completion (SignalFailure); disposal must not throw.
        }

        // Settle the collaboration node once the loop it points at is gone: record the terminal status, retain
        // the entry so the finished hierarchy stays inspectable, and return the capacity permit exactly once. A
        // run that never reached a terminal node settles as an error, which is what it was.
        //
        // Ordered BEFORE the snapshot drain deliberately. Retention and the permit are independent: a retained
        // entry is an inspectable record, not a live routing target, and holding the permit across a store
        // flush of unbounded duration would let one slow teardown freeze collaboration capacity for its whole
        // hierarchy. The drain cannot change IsComplete, so settling here reports exactly what settling after
        // it would have. This mirrors the sub-agent rule that the lease is returned when the agent stops
        // existing, ahead of a potentially slow dispose — and differs from an ordinary sub-agent's COMPLETION
        // only because a finished controller is not restartable by a later message: it really is gone.
        _collaboration?.Finish(Runtime.IsComplete);

        // Flush any pending best-effort snapshot saves (serialized in capture order; faults are swallowed and
        // logged) before the handle goes away.
        await Runtime.DrainPersistAsync().ConfigureAwait(false);
    }
}
