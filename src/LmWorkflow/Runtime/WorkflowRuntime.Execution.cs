using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Binding;
using AchieveAi.LmDotnetTools.LmWorkflow.Model;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

public sealed partial class WorkflowRuntime
{
    private readonly SemaphoreSlim _automaticDrive = new(1, 1);
    private readonly Dictionary<string, string> _automaticSessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _automaticDeadlines = new(StringComparer.Ordinal);

    /// <summary>Host policy for new task occurrences. Existing saved deadlines cannot be extended.</summary>
    public TimeSpan? AutomaticInvocationTimeout { get; set; }

    /// <summary>Binds an authored session name to the host's existing parent identity before its first use.</summary>
    public void BindAutomaticSession(string name, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_lock)
        {
            if (
                _automaticSessions.TryGetValue(name, out var bound)
                && !string.Equals(bound, sessionId, StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException("A pinned parent session cannot be replaced.");
            }
            _automaticSessions[name] = sessionId;
        }
        Persist(Snapshot());
    }

    /// <summary>
    /// Drives a strict authored workflow using the existing task coordinator and snapshot store.
    /// Every transition and invocation waits for its durable checkpoint. Unknown results stay in flight.
    /// The caller owns exclusive admission of this instance across processes.
    /// After a persistence fault, discard this runtime and reload the last acknowledged snapshot. Never
    /// reset its failed save chain: live state may contain an action result that storage did not acknowledge.
    /// </summary>
    public async Task<WorkflowInvocationStatus> RunAutomaticAsync(
        IWorkflowStore store,
        string instanceId,
        IWorkflowTaskInvoker invoker,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        await _automaticDrive.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var definition = Definition ?? throw new InvalidOperationException("No workflow definition is loaded.");
            if (!definition.StrictContracts)
            {
                throw new InvalidOperationException("Automatic execution requires strict workflow contracts.");
            }

            ValidateAutomaticInput(Inputs, definition.InputSchema);
            if (_instanceId is not null && !string.Equals(_instanceId, instanceId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("An automatic runtime cannot change its instance identity.");
            }
            AttachStore(store, instanceId);
            Persist(Snapshot());
            await DrainPersistAsync().ConfigureAwait(false);

            while (!IsComplete)
            {
                ct.ThrowIfCancellationRequested();
                var node = definition.Nodes.Single(n => n.Id == CurrentNodeId);
                if (Step >= definition.MaxStepBudget)
                {
                    if (string.IsNullOrEmpty(definition.OnBudgetExhausted))
                    {
                        return WorkflowInvocationStatus.Failed;
                    }
                    AdvanceTo(node.Id, definition.OnBudgetExhausted, null);
                }
                else if (node is StartNode start)
                {
                    AdvanceTo(node.Id, start.Next.Single(), null);
                }
                else if (node is ConditionalNode branch)
                {
                    var context = BuildContext(null, null, null);
                    var target = branch.Else;
                    foreach (var candidate in branch.Branches)
                    {
                        var condition =
                            candidate.StructuredCondition
                            ?? throw new InvalidOperationException("Automatic branches require structured conditions.");
                        if (ConditionEvaluator.Evaluate(condition, context, ConditionEvaluationPolicy.StrictWorkflow))
                        {
                            target = candidate.To;
                            break;
                        }
                    }
                    AdvanceTo(node.Id, target, null);
                }
                else if (node is ProceduralNode procedure)
                {
                    var status = await ExecuteAutomaticNodeAsync(procedure, invoker, ct).ConfigureAwait(false);
                    if (status != WorkflowInvocationStatus.Completed)
                    {
                        return status;
                    }
                    AdvanceTo(node.Id, procedure.Next.Single(), null);
                }
                else
                {
                    throw new InvalidOperationException("Unsupported automatic workflow node.");
                }

                await DrainPersistAsync().ConfigureAwait(false);
            }

            _ = _completion.TrySetResult();
            return WorkflowInvocationStatus.Completed;
        }
        finally
        {
            _ = _automaticDrive.Release();
        }
    }

    private async Task<WorkflowInvocationStatus> ExecuteAutomaticNodeAsync(
        ProceduralNode node,
        IWorkflowTaskInvoker invoker,
        CancellationToken ct
    )
    {
        if (node.TaskList is not { Count: 1 } tasks || tasks[0].ForEach is not null || tasks[0].Parallel)
        {
            throw new InvalidOperationException("Automatic nodes require one sequential task.");
        }

        var task = tasks[0];
        _ = ComposeNextExpectedAction();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var occurrence = Snapshot().Tasks.Single(t => t.NodeId == node.Id && t.Visit == Visits[node.Id]);
            if (occurrence.Status == WorkflowTaskStatus.Validated)
            {
                return WorkflowInvocationStatus.Completed;
            }
            if (occurrence.Status == WorkflowTaskStatus.Failed)
            {
                return WorkflowInvocationStatus.Failed;
            }

            var input =
                TypedInputBinder.Resolve(task.Input ?? new JsonObject(), BuildContext(null, null, null))
                ?? throw new InvalidOperationException("Task input cannot be null.");
            ValidateAutomaticInput(input, task.InputSchema);

            string? sessionId = null;
            if (task.Delegate == DelegateKind.Agent)
            {
                var sessionName = task.Session ?? node.Id;
                lock (_lock)
                {
                    if (!_automaticSessions.TryGetValue(sessionName, out sessionId))
                    {
                        sessionId = Guid.NewGuid().ToString("N");
                        _automaticSessions.Add(sessionName, sessionId);
                    }
                }
            }

            var invocationId = occurrence.ToolCallId ?? $"{_instanceId}/{occurrence.Name}/{occurrence.Attempts}";
            DateTimeOffset? deadline = null;
            lock (_lock)
            {
                if (_automaticDeadlines.TryGetValue(occurrence.Name, out var pinned))
                {
                    deadline = pinned;
                }
                else if (AutomaticInvocationTimeout is { } timeout)
                {
                    if (timeout <= TimeSpan.Zero)
                    {
                        throw new InvalidOperationException("Invocation timeout must be positive.");
                    }

                    if (occurrence.Status == WorkflowTaskStatus.InFlight)
                    {
                        throw new InvalidDataException("An active invocation is missing its durable deadline.");
                    }

                    deadline = DateTimeOffset.UtcNow + timeout;
                    _automaticDeadlines.Add(occurrence.Name, deadline.Value);
                }
            }
            var invocation = new WorkflowInvocation
            {
                InstanceId = _instanceId!,
                InvocationId = invocationId,
                UnitName = occurrence.Name,
                Task = task with
                {
                    PromptTemplate = TemplateRenderer.RenderStrict(task.PromptTemplate, BuildContext(null, null, null)),
                },
                Input = input,
                SessionId = sessionId,
                IsCorrection = occurrence.Attempts > 0,
                Attempt = occurrence.Attempts + 1,
                ValidationError = occurrence.LastError,
                DeadlineUtc = deadline,
            };

            WorkflowInvocationResult answer;
            if (occurrence.Status == WorkflowTaskStatus.InFlight)
            {
                answer = await invoker.ReconcileAsync(invocation, ct).ConfigureAwait(false);
            }
            else
            {
                // The stable occurrence and parent binding must be recoverable before any external effect.
                RegisterSpawn(invocationId, occurrence.Name);
                Persist(Snapshot());
                await DrainPersistAsync().ConfigureAwait(false);
                answer = await invoker.InvokeAsync(invocation, ct).ConfigureAwait(false);
            }

            if (answer.Status == WorkflowInvocationStatus.Unknown)
            {
                return WorkflowInvocationStatus.Unknown;
            }

            var invalidScript =
                task.Delegate == DelegateKind.Script
                && !CheckSpawnResult(invocationId, answer.Output ?? string.Empty).IsValid;
            ObserveResult(
                invocationId,
                answer.Output ?? string.Empty,
                answer.Status == WorkflowInvocationStatus.Failed || invalidScript
            );
            await DrainPersistAsync().ConfigureAwait(false);

            // Only an agent's final format can be corrected. Script or transport failure never replays actions.
            if (answer.Status == WorkflowInvocationStatus.Failed || invalidScript)
            {
                return WorkflowInvocationStatus.Failed;
            }
        }
    }

    private void ValidateAutomaticInput(JsonNode input, JsonNode? schema)
    {
        if (
            schema is not null
            && !_schemaValidator.ValidateDetailed(input.ToJsonString(), schema.ToJsonString()).IsValid
        )
        {
            throw new InvalidOperationException("Workflow input did not match its declared schema.");
        }
    }
}
