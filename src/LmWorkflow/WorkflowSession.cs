using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;

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
    ///     The system prompt that tells the controller how to drive the workflow. The integration test
    ///     scripts this contract directly; a future phase (P6) will expand it into the production prompt.
    /// </summary>
    internal const string ControllerSystemPrompt =
        "You drive a workflow. First author it with SetWorkflow, then loop: call GetWorkflow to read "
        + "the current node and its ready-to-spawn nextExpectedAction unit(s). For each surfaced unit, "
        + "spawn it by calling the Agent tool with subagent_type and prompt taken from the unit, and set "
        + "the Agent tool's 'name' argument to the unit's name verbatim (this is how results are recorded). "
        + "After a node's tasks are done, route on with SetCurrentNode; when entering a terminal node pass "
        + "the final result object to complete the workflow.";

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
    /// <param name="ct">A cancellation token bound to the run.</param>
    public static Task<WorkflowRunHandle> StartAsync(
        string objective,
        JsonObject? inputs,
        WorkflowDefinition? definition,
        SubAgentOptions subAgentOptions,
        IStreamingAgent controllerAgent,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(objective);
        ArgumentNullException.ThrowIfNull(subAgentOptions);
        ArgumentNullException.ThrowIfNull(controllerAgent);
        ArgumentException.ThrowIfNullOrEmpty(threadId);

        var runtime = new WorkflowRuntime();
        if (definition is not null)
        {
            runtime.LoadDefinition(definition);
        }

        if (inputs is not null)
        {
            runtime.MergeInputs(inputs);
        }

        var registry = new FunctionRegistry();
        _ = registry.AddProvider(new WorkflowToolProvider(runtime));

        var loop = new MultiTurnAgentLoop(
            controllerAgent,
            registry,
            threadId,
            systemPrompt: ControllerSystemPrompt,
            subAgentOptions: subAgentOptions
        );

        // Start the controller loop, then drive + observe it from a single ordered consumer.
        _ = loop.RunAsync(ct);
        var objectiveInput = new UserInput([new TextMessage { Text = objective, Role = Role.User }]);
        var driveTask = DriveAndObserveAsync(loop, runtime, objectiveInput, ct);

        return Task.FromResult(new WorkflowRunHandle(runtime, loop, driveTask));
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

    /// <summary>
    ///     Correlates the stream events that matter: an <c>Agent</c> tool call (registers the spawn by the
    ///     runtime-surfaced unit name); its tool result (a blocking answer is validated/recorded, a background
    ///     receipt records the <c>agent_id</c> correlation); and an injected <c>&lt;sub-agent&gt;</c> user
    ///     message (the background completion, validated/recorded by its agent id). Every other message is
    ///     ignored.
    /// </summary>
    private static void Observe(WorkflowRuntime runtime, IMessage message)
    {
        switch (message)
        {
            case ToolCallMessage { FunctionName: "Agent", ToolCallId: { } toolCallId } call:
                var name = TryReadSpawnName(call.FunctionArgs);
                if (name is not null)
                {
                    runtime.RegisterSpawn(toolCallId, name);
                }

                break;

            case ToolCallResultMessage { ToolCallId: { } resultId } result
                when runtime.IsRegisteredSpawn(resultId):
                runtime.ObserveSpawnResult(resultId, result.Result, result.IsError);
                break;

            case TextMessage { Role: Role.User, Text: { } text }
                when text.TrimStart().StartsWith("<sub-agent", StringComparison.Ordinal):
                if (SubAgentResultParser.TryParse(text, out var agentId, out var payload, out var isError))
                {
                    runtime.ObserveInjectedResult(agentId, payload, isError);
                }

                break;

            default:
                break;
        }
    }

    private static string? TryReadSpawnName(string? functionArgs)
    {
        if (string.IsNullOrEmpty(functionArgs))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(functionArgs);
            return doc.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
///     A handle to a running workflow: exposes the backing <see cref="WorkflowRuntime"/> and controller
///     <see cref="Loop"/> (for hosts/tests) plus the run <see cref="Completion"/> and final
///     <see cref="Result"/>. Disposing the handle joins the observer task and disposes the loop.
/// </summary>
public sealed class WorkflowRunHandle : IAsyncDisposable
{
    private readonly Task _driveTask;

    internal WorkflowRunHandle(WorkflowRuntime runtime, MultiTurnAgentLoop loop, Task driveTask)
    {
        Runtime = runtime;
        Loop = loop;
        _driveTask = driveTask;
    }

    /// <summary>The runtime that holds all workflow state.</summary>
    public WorkflowRuntime Runtime { get; }

    /// <summary>The controller loop driving the workflow.</summary>
    public MultiTurnAgentLoop Loop { get; }

    /// <summary>Completes when the workflow reaches a terminal node; faults if the controller run throws.</summary>
    public Task Completion => Runtime.Completion;

    /// <summary>The validated final result captured at completion, or <c>null</c>.</summary>
    public JsonNode? Result => Runtime.Result;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _driveTask.ConfigureAwait(false);
        }
        catch
        {
            // The drive task's failure is already surfaced via Completion; disposal must not throw.
        }

        await Loop.DisposeAsync().ConfigureAwait(false);
    }
}
