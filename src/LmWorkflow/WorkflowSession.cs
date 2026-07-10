using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
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
    ///     and must not be able to author/replace it (e.g. a <c>StartWorkflow</c>-launched controller).
    /// </param>
    /// <param name="controllerMaxTurnsPerRun">
    ///     An optional bound on the controller loop's turns per run; <c>null</c> keeps the loop's default (50).
    /// </param>
    /// <param name="controllerDefaultOptions">
    ///     Optional request defaults (notably <c>ModelId</c>) for the controller loop, so the controller runs
    ///     on a fixed, pre-configured model rather than the provider agent's hardcoded default.
    /// </param>
    /// <param name="ct">A cancellation token bound to the run.</param>
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
        CancellationToken ct = default
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
            controllerDefaultOptions
        );
        return Task.FromResult(BeginRun(loop, runtime, objective, ct));
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
    /// <exception cref="InvalidOperationException">No snapshot exists for <paramref name="instanceId"/>.</exception>
    public static async Task<WorkflowRunHandle> ResumeAsync(
        string instanceId,
        IWorkflowStore store,
        SubAgentOptions subAgentOptions,
        IStreamingAgent controllerAgent,
        string threadId,
        IConversationStore? conversationStore = null,
        ILogger? logger = null,
        IJsonSchemaValidator? schemaValidator = null,
        CancellationToken ct = default
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
        runtime.AttachStore(store, instanceId);

        var loop = BuildLoop(controllerAgent, runtime, threadId, subAgentOptions, conversationStore);

        // Restore the controller's prior conversation BEFORE driving so it continues with full context.
        // Doing it explicitly here also marks recovery complete so RunAsync does not re-recover.
        if (conversationStore is not null)
        {
            _ = await loop.RecoverAsync(ct).ConfigureAwait(false);
        }

        return BeginRun(loop, runtime, ResumeObjective, ct);
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
        GenerateReplyOptions? controllerDefaultOptions = null
    )
    {
        var registry = new FunctionRegistry();
        _ = registry.AddProvider(new WorkflowToolProvider(runtime, includeSetWorkflow: includeAuthoringTool));

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
            subAgentOptions: subAgentOptions
        );
    }

    /// <summary>Starts the loop, drives + observes it from a single ordered consumer, and wraps a handle.</summary>
    private static WorkflowRunHandle BeginRun(
        MultiTurnAgentLoop loop,
        WorkflowRuntime runtime,
        string initialMessage,
        CancellationToken ct
    )
    {
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
        var driveTask = DriveAndObserveAsync(loop, runtime, input, ct);

        return new WorkflowRunHandle(runtime, loop, runTask, driveTask);
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
        CancellationToken ct
    )
    {
        try
        {
            await foreach (var message in loop.ExecuteRunAsync(objectiveInput, ct).ConfigureAwait(false))
            {
                Observe(runtime, message);
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

    internal WorkflowRunHandle(
        WorkflowRuntime runtime,
        MultiTurnAgentLoop loop,
        Task runTask,
        Task driveTask
    )
    {
        Runtime = runtime;
        Loop = loop;
        _runTask = runTask;
        _driveTask = driveTask;
    }

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

        // Flush any pending best-effort snapshot saves (serialized in capture order; faults are swallowed and
        // logged) before the handle goes away.
        await Runtime.DrainPersistAsync().ConfigureAwait(false);
    }
}
