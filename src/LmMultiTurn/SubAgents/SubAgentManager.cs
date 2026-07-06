using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Manages sub-agent lifecycle: spawning, monitoring, resuming, and disposal.
/// Coordinates concurrency and relays completion results back to the parent agent.
/// </summary>
public sealed class SubAgentManager : IAsyncDisposable
{
    private readonly IMultiTurnAgent _parentAgent;
    private readonly string? _parentModelId;
    private readonly IReadOnlyList<FunctionContract> _parentContracts;
    private readonly IDictionary<string, ToolHandler> _parentHandlers;
    private readonly SubAgentOptions _options;
    private readonly MutableSubAgentTemplateSource _source;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, SubAgentState> _agents = new();
    private readonly ConcurrentDictionary<string, string> _namesToIds = new();
    private readonly SemaphoreSlim _concurrencyGate;

    public SubAgentManager(
        IMultiTurnAgent parentAgent,
        IReadOnlyList<FunctionContract> parentContracts,
        IDictionary<string, ToolHandler> parentHandlers,
        SubAgentOptions options,
        MutableSubAgentTemplateSource source,
        ILogger? logger = null,
        string? parentModelId = null)
    {
        ArgumentNullException.ThrowIfNull(parentAgent);
        ArgumentNullException.ThrowIfNull(parentContracts);
        ArgumentNullException.ThrowIfNull(parentHandlers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);

        _parentAgent = parentAgent;
        _parentContracts = parentContracts;
        _parentHandlers = parentHandlers;
        _options = options;
        _source = source;
        _logger = logger ?? NullLogger.Instance;
        // The parent's model, inherited by sub-agents whose template/override sets none (see
        // ResolveSubAgentOptions). Null when the parent has no model (e.g. CLI-backed parents).
        _parentModelId = parentModelId;
        _concurrencyGate = new SemaphoreSlim(
            options.MaxConcurrentSubAgents,
            options.MaxConcurrentSubAgents);
    }

    /// <summary>
    /// Spawn a new sub-agent from a named template.
    /// When <paramref name="runInBackground"/> is false (default), blocks until the
    /// sub-agent's run completes and returns its final answer as the result. When true,
    /// returns immediately with a JSON spawn receipt (agent id) and relays the eventual
    /// result to the parent as an injected message.
    /// </summary>
    public async Task<string> SpawnAsync(
        string templateName,
        string task,
        string? name = null,
        string? model = null,
        bool runInBackground = false,
        string[]? addTools = null,
        string[]? removeTools = null,
        CancellationToken ct = default)
    {
        // Snapshot the live source view so a concurrent TryRegister cannot make the
        // diagnostic Available list inconsistent with the lookup that produced template.
        var templates = _source.Templates;
        if (!templates.TryGetValue(templateName, out var template))
        {
            throw new ArgumentException(
                $"Unknown template '{templateName}'. " +
                $"Available: {string.Join(", ", templates.Keys)}",
                nameof(templateName));
        }

        if (!await _concurrencyGate.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            throw new InvalidOperationException(
                $"Max concurrent sub-agents ({_options.MaxConcurrentSubAgents}) " +
                $"reached. Wait for a sub-agent to complete or increase the limit.");
        }

        var agentId = Guid.NewGuid().ToString("N")[..12];
        SubAgentState state;

        try
        {
            var agent = CreateSubAgent(agentId, template, model, addTools, removeTools, out var store);

            state = new SubAgentState
            {
                AgentId = agentId,
                TemplateName = templateName,
                Task = task,
                Agent = agent,
                Store = store,
                Name = name,
                NotifyParentOnCompletion = runInBackground,
            };

            _agents[agentId] = state;
            if (!string.IsNullOrWhiteSpace(name))
            {
                if (_namesToIds.TryGetValue(name, out var existingId)
                    && existingId != agentId
                    && _agents.ContainsKey(existingId))
                {
                    _logger.LogWarning(
                        "Sub-agent name '{Name}' already maps to agent {ExistingId}; reassigning it "
                            + "to the newly spawned agent {AgentId}. SendMessage by this name will now "
                            + "address the new agent.",
                        name, existingId, agentId);
                }

                _namesToIds[name] = agentId;
            }

            // Start the agent loop in the background
            var cts = state.Cts;
            state.RunTask = agent.RunAsync(cts.Token);

            // Start monitoring BEFORE sending the task to avoid subscribe-after-send race:
            // if SendAsync triggers a fast completion before the monitor subscribes,
            // the RunCompletedMessage would fire with no subscriber listening.
            state.MonitorTask = MonitorSubAgentAsync(state, cts.Token);

            // Send the task as user input (triggers first turn)
            _ = await agent.SendAsync(
                [new TextMessage { Role = Role.User, Text = task }], ct: ct);

            _logger.LogInformation(
                "Spawned sub-agent {AgentId} from template {Template} (background={Background}) with task: {Task}",
                agentId, templateName, runInBackground, Truncate(task, 100));
        }
        catch
        {
            // Release the gate on spawn-time failure (before the monitor takes ownership).
            _ = _concurrencyGate.Release();
            throw;
        }

        // Past this point the monitor owns the concurrency gate; do not release it here.
        if (runInBackground)
        {
            // Nobody awaits the completion on the background path; observe any fault
            // so it never surfaces as an UnobservedTaskException.
            ObserveCompletionFaults(state);

            return JsonSerializer.Serialize(new
            {
                agent_id = agentId,
                name,
                template = templateName,
                status = "spawned",
            });
        }

        // Synchronous: block until the run completes and return its final answer.
        // Parent relay is suppressed (NotifyParentOnCompletion=false) so the result
        // flows back only as this tool result, in the same parent turn.
        return await AwaitCompletionAsync(state, ct);
    }

    /// <summary>
    /// Continue an existing sub-agent identified by its id or caller-supplied name.
    /// A running agent receives the message in its current run; a finished agent is
    /// restarted. When <paramref name="runInBackground"/> is false (default), blocks
    /// until the (re)started run completes and returns its final answer; when true,
    /// returns a JSON receipt immediately and relays the result to the parent.
    /// </summary>
    public async Task<string> SendMessageAsync(
        string target,
        string prompt,
        bool runInBackground = false,
        CancellationToken ct = default)
    {
        var agentId = ResolveAgentId(target);
        var state = _agents[agentId];

        var wasRunning = state.Status == SubAgentStatus.Running;
        state.NotifyParentOnCompletion = runInBackground;

        // A finished completion is replaced so this run can be awaited fresh; a pending
        // one (running agent) is kept so existing waiters observe the next resolution.
        state.ResetCompletionIfFinished();

        if (wasRunning)
        {
            // Inject the message into the currently running agent.
            _ = await state.Agent.SendAsync(
                [new TextMessage { Role = Role.User, Text = prompt }], ct: ct);

            _logger.LogInformation(
                "Sent message to running sub-agent {AgentId}: {Message}",
                agentId, Truncate(prompt, 100));
        }
        else
        {
            await RestartRunAsync(state, prompt, ct);
        }

        if (runInBackground)
        {
            ObserveCompletionFaults(state);

            return JsonSerializer.Serialize(new
            {
                agent_id = agentId,
                name = state.Name,
                status = wasRunning ? "message_sent" : "resumed",
            });
        }

        return await AwaitCompletionAsync(state, ct);
    }

    /// <summary>
    /// Resolves a caller-supplied target (agent id or name) to a concrete agent id.
    /// </summary>
    private string ResolveAgentId(string target)
    {
        if (_agents.ContainsKey(target))
        {
            return target;
        }

        if (_namesToIds.TryGetValue(target, out var id) && _agents.ContainsKey(id))
        {
            return id;
        }

        throw new ArgumentException(
            $"Unknown sub-agent '{target}'. Provide a valid agent id or name.",
            nameof(target));
    }

    /// <summary>
    /// Restarts a finished (completed/error/stopped) sub-agent's run with a new message.
    /// On success the new monitor owns the concurrency gate; on failure the gate is released.
    /// </summary>
    private async Task RestartRunAsync(
        SubAgentState state,
        string prompt,
        CancellationToken ct)
    {
        if (!await _concurrencyGate.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            throw new InvalidOperationException(
                $"Max concurrent sub-agents ({_options.MaxConcurrentSubAgents}) " +
                $"reached. Cannot resume agent '{state.AgentId}'.");
        }

        try
        {
            // Recover conversation history if a store is available
            if (state.Store != null
                && state.Agent is MultiTurnAgentBase agentBase)
            {
                _ = await agentBase.RecoverAsync();
            }

            // Cancel and dispose the old CTS to prevent double-monitor bugs:
            // the old monitor's closure captured the old CTS, and both monitors
            // would receive RunCompletedMessage causing double Release/Decrement.
            await state.Cts.CancelAsync();

            // Observe the old RunTask to avoid unobserved exceptions
            // (must cancel first so the task receives the cancellation signal)
            if (state.RunTask != null)
            {
                try { await state.RunTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Old RunTask faulted for sub-agent {AgentId}", state.AgentId);
                }
            }

            if (state.MonitorTask != null)
            {
                try { await state.MonitorTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Old MonitorTask faulted for sub-agent {AgentId}", state.AgentId);
                }
            }

            state.Cts.Dispose();

            // Create new CTS and start the loop again
            state.Cts = new CancellationTokenSource();
            var cts = state.Cts;

            state.RunTask = state.Agent.RunAsync(cts.Token);

            // Re-subscribe BEFORE sending to avoid subscribe-after-send race
            state.MonitorTask = MonitorSubAgentAsync(state, cts.Token);

            _ = await state.Agent.SendAsync(
                [new TextMessage { Role = Role.User, Text = prompt }], ct: ct);

            state.Status = SubAgentStatus.Running;

            _logger.LogInformation(
                "Resumed sub-agent {AgentId} with message: {Message}",
                state.AgentId, Truncate(prompt, 100));
        }
        catch
        {
            _ = _concurrencyGate.Release();
            throw;
        }
    }

    /// <summary>
    /// Check the status and recent activity of a sub-agent.
    /// </summary>
    public string Peek(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var state))
        {
            throw new ArgumentException(
                $"Unknown agent ID '{agentId}'.", nameof(agentId));
        }

        // Get the last 3 turns from the buffer
        var recentTurns = state.TurnBuffer
            .ToArray()
            .TakeLast(3)
            .Select(t => new
            {
                type = t.MessageType,
                tool = t.ToolName,
                tool_args = t.ToolArgsPreview,
                text = t.TextPreview,
                time = t.Timestamp.ToString("o"),
            })
            .ToArray();

        return JsonSerializer.Serialize(new
        {
            agent_id = agentId,
            name = state.Name,
            status = state.Status.ToString().ToLowerInvariant(),
            template = state.TemplateName,
            task = state.Task,
            recent_turns = recentTurns,
            last_result = state.LastResult,
            send_to_parent_failed = state.SendToParentFailed,
            send_to_parent_error = state.SendToParentError,
        });
    }

    /// <summary>
    /// Awaits a sub-agent's completion by id, returning its final text (or throwing its
    /// <see cref="SubAgentExecutionException"/> on failure). Used by the sample-app
    /// SubAgentCompletionTriggerSource so a Wait can observe a sub-agent the same way the internal
    /// synchronous path does. If the caller's <paramref name="ct"/> fires, the sub-agent is cancelled
    /// (identical to the internal synchronous wait).
    /// </summary>
    public Task<string> ObserveCompletionAsync(string agentId, CancellationToken ct)
    {
        if (!_agents.TryGetValue(agentId, out var state))
        {
            throw new ArgumentException($"Unknown agent ID '{agentId}'.", nameof(agentId));
        }

        return AwaitCompletionAsync(state, ct);
    }

    /// <summary>
    /// Sets whether a specific sub-agent's completion is automatically relayed to the parent. A
    /// trigger source waiting on this sub-agent flips it to <c>false</c> at arm time (so the result
    /// arrives once, via the trigger envelope, not twice) and MUST restore it to <c>true</c> if the
    /// wait is cancelled before completion.
    /// </summary>
    public void SetNotifyParentOnCompletion(string agentId, bool value)
    {
        if (!_agents.TryGetValue(agentId, out var state))
        {
            throw new ArgumentException($"Unknown agent ID '{agentId}'.", nameof(agentId));
        }

        state.NotifyParentOnCompletion = value;
    }

    /// <summary>
    /// Get the list of available template names.
    /// </summary>
    public IReadOnlyList<string> GetTemplateNames()
    {
        return _source.Templates.Keys.ToList().AsReadOnly();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (_, state) in _agents)
        {
            // Each step is isolated to prevent cascading failures:
            // if StopAsync throws, we still await tasks, dispose the agent, etc.
            try { await state.Cts.CancelAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CancelAsync failed for sub-agent {AgentId}", state.AgentId);
            }

            try { await state.Agent.StopAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "StopAsync failed for sub-agent {AgentId}", state.AgentId);
            }

            // Await background tasks to ensure clean shutdown
            if (state.RunTask != null)
            {
                try { await state.RunTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RunTask faulted for sub-agent {AgentId}", state.AgentId);
                }
            }

            if (state.MonitorTask != null)
            {
                try { await state.MonitorTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MonitorTask faulted for sub-agent {AgentId}", state.AgentId);
                }
            }

            try { await state.Agent.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DisposeAsync failed for sub-agent {AgentId}", state.AgentId);
            }

            try { state.Cts.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "CTS dispose failed for sub-agent {AgentId}", state.AgentId);
            }

            if (state.Store is IAsyncDisposable disposableStore)
            {
                try { await disposableStore.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Store dispose failed for sub-agent {AgentId}", state.AgentId);
                }
            }
        }

        _agents.Clear();
        _namesToIds.Clear();
        _concurrencyGate.Dispose();
    }

    /// <summary>
    /// Creates a MultiTurnAgentLoop configured for a sub-agent with filtered tools.
    /// </summary>
    private IMultiTurnAgent CreateSubAgent(
        string agentId,
        SubAgentTemplate template,
        string? modelOverride,
        string[]? addTools,
        string[]? removeTools,
        out IConversationStore? store)
    {
        var providerAgent = template.AgentFactory();

        // Resolve the sub-agent's options with model inheritance (override > template > parent).
        var defaultOptions = ResolveSubAgentOptions(template.DefaultOptions, modelOverride, _parentModelId);

        // Determine conversation store
        var storeFactory =
            template.ConversationStoreFactory
            ?? _options.DefaultConversationStoreFactory;
        store = storeFactory?.Invoke($"subagent-{agentId}");

        // Build a fresh FunctionRegistry with filtered parent tools
        var registry = new FunctionRegistry();
        var enabledSet = BuildEnabledToolSet(
            template.EnabledTools, addTools, removeTools);

        foreach (var contract in _parentContracts)
        {
            if (enabledSet != null && !enabledSet.Contains(contract.Name))
            {
                continue;
            }

            if (!_parentHandlers.TryGetValue(contract.Name, out var handler))
            {
                continue;
            }

            _ = registry.AddFunction(contract, handler, "ParentTools");
        }

        return new MultiTurnAgentLoop(
            providerAgent,
            registry,
            threadId: $"subagent-{agentId}",
            systemPrompt: template.SystemPrompt,
            defaultOptions: defaultOptions,
            maxTurnsPerRun: template.MaxTurnsPerRun,
            store: store,
            logger: _logger is NullLogger ? null : new SubAgentLoopLoggerAdapter(_logger));
    }

    /// <summary>
    /// Resolves the sub-agent's <see cref="GenerateReplyOptions"/> with model inheritance: an explicit
    /// per-spawn <paramref name="modelOverride"/> wins, else the template's own
    /// <see cref="GenerateReplyOptions.ModelId"/>, else the PARENT agent's model
    /// (<paramref name="parentModelId"/>). The parent fallback stops a template that sets no model
    /// (e.g. the built-in sub-agents) from letting the provider agent use its hardcoded default model,
    /// which often isn't valid on the parent's backend (observed: a sub-agent sending
    /// <c>claude-3-sonnet-20240229</c> to a backend that only serves the parent's model → HTTP 400
    /// <c>model_not_supported</c>). Any other template option fields are preserved. Returns null only
    /// when no model is available anywhere AND the template carried no options, so the previous
    /// "inherit the provider's own defaults" behavior is unchanged when there is genuinely nothing to set.
    /// </summary>
    internal static GenerateReplyOptions? ResolveSubAgentOptions(
        GenerateReplyOptions? templateDefaults,
        string? modelOverride,
        string? parentModelId)
    {
        var templateModel = templateDefaults?.ModelId;
        var model = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride
            : !string.IsNullOrWhiteSpace(templateModel)
                ? templateModel
                : parentModelId;

        return string.IsNullOrWhiteSpace(model)
            ? templateDefaults
            : (templateDefaults ?? new GenerateReplyOptions()) with { ModelId = model };
    }

    /// <summary>
    /// Builds the effective set of enabled tool names from template filter + overrides.
    /// Returns null when all tools should be available (no filtering).
    /// </summary>
    internal static HashSet<string>? BuildEnabledToolSet(
        IReadOnlyList<string>? templateEnabledTools,
        string[]? addTools,
        string[]? removeTools)
    {
        if (templateEnabledTools == null
            && addTools == null
            && removeTools == null)
        {
            return null; // No filtering
        }

        HashSet<string>? result = null;

        if (templateEnabledTools != null)
        {
            result = [.. templateEnabledTools];
        }

        if (addTools != null)
        {
            result ??= [];
            foreach (var tool in addTools)
            {
                _ = result.Add(tool);
            }
        }

        if (removeTools != null)
        {
            if (result == null)
            {
                // removeTools without a base set: cannot remove from "all tools"
                // since we don't know the full tool list here. Treat as error.
                throw new InvalidOperationException(
                    "Cannot specify removeTools without enabledTools or addTools. " +
                    "Use template EnabledTools to define the base set, then remove from it.");
            }

            foreach (var tool in removeTools)
            {
                _ = result.Remove(tool);
            }
        }

        return result;
    }

    /// <summary>
    /// Monitors a sub-agent's output, buffering turn summaries and detecting completion.
    /// Relays completion/error results back to the parent agent.
    /// </summary>
    private async Task MonitorSubAgentAsync(
        SubAgentState state,
        CancellationToken ct)
    {
        string? lastTextContent = null;
        var gateReleased = false;

        // Release the concurrency slot exactly once per monitor. A single monitor can
        // observe multiple RunCompletedMessages — a background sub-agent continued in
        // place via SendMessage runs again under the same monitor — but the slot was
        // acquired once (at spawn/restart), so it must be released once: on the first
        // completion, and never again. Releasing per-completion would over-release the
        // semaphore (eventually SemaphoreFullException) and break the concurrency limit.
        void ReleaseGateOnce()
        {
            if (!gateReleased)
            {
                gateReleased = true;
                _ = _concurrencyGate.Release();
            }
        }

        // Subscribers receive raw streaming deltas: the publishing middleware runs
        // upstream of the joiner, so the consolidated TextMessage never reaches here —
        // only TextUpdateMessage deltas do. Reconstruct the sub-agent's final answer by
        // accumulating deltas per generation and keeping the latest generation's text.
        var textBuilder = new StringBuilder();
        string? textGenerationId = null;

        try
        {
            await foreach (var msg in state.Agent.SubscribeAsync(ct))
            {
                var summary = CreateTurnSummary(msg);
                if (summary != null)
                {
                    state.TurnBuffer.Enqueue(summary);
                    while (state.TurnBuffer.Count > 10)
                    {
                        _ = state.TurnBuffer.TryDequeue(out _);
                    }
                }

                // Track the last assistant text for the completion result. Subscribers
                // receive raw deltas, so accumulate TextUpdateMessage deltas per
                // generation; a consolidated TextMessage (non-streaming mock) is taken as-is.
                if (msg is TextUpdateMessage tu
                    && tu.Role == Role.Assistant
                    && !tu.IsThinking)
                {
                    // A new generation resets the accumulator so we keep only the most
                    // recent assistant message, not earlier turns' text.
                    if (!string.Equals(textGenerationId, tu.GenerationId, StringComparison.Ordinal))
                    {
                        textGenerationId = tu.GenerationId;
                        _ = textBuilder.Clear();
                    }

                    _ = textBuilder.Append(tu.Text);
                    lastTextContent = textBuilder.ToString();
                }
                else if (msg is TextMessage tm
                    && tm.Role == Role.Assistant
                    && !tm.IsThinking)
                {
                    textGenerationId = tm.GenerationId;
                    _ = textBuilder.Clear().Append(tm.Text);
                    lastTextContent = tm.Text;
                }

                if (msg is RunCompletedMessage rcm)
                {
                    state.LastResult = lastTextContent;
                    await HandleRunCompletionAsync(state, rcm, lastTextContent);
                    ReleaseGateOnce();
                    lastTextContent = null;
                    textGenerationId = null;
                    _ = textBuilder.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected during shutdown
        }
        catch (ChannelClosedException)
        {
            // Subscription channel closed - expected during disposal
        }
        catch (Exception ex)
        {
            state.Status = SubAgentStatus.Error;
            state.SendToParentError = $"Monitor failed: {ex.Message}";
            _logger.LogError(
                ex,
                "Error monitoring sub-agent {AgentId}",
                state.AgentId);
        }
        finally
        {
            // Fallback: if no completion was ever handled (the stream ended, was
            // cancelled, or faulted before any RunCompletedMessage), the slot still
            // needs releasing. ReleaseGateOnce is idempotent, so this is a no-op when
            // a completion already released it.
            ReleaseGateOnce();
        }
    }

    /// <summary>
    /// Handles a sub-agent run completion: resolves the synchronous completion signal
    /// and, for background spawns/continuations, relays the result to the parent.
    /// </summary>
    private async Task HandleRunCompletionAsync(
        SubAgentState state,
        RunCompletedMessage rcm,
        string? lastTextContent)
    {
        // The concurrency slot is released by the monitor (ReleaseGateOnce), exactly once
        // per monitor lifetime — not here, because a single monitor may handle several
        // completions when a background sub-agent is continued in place via SendMessage.
        if (rcm.IsError)
        {
            state.Status = SubAgentStatus.Error;

            var errorText =
                $"<sub-agent name=\"{state.TemplateName}\" " +
                $"id=\"{state.AgentId}\">\n" +
                $"[Error] Task: {state.Task}\n" +
                $"Error: {rcm.ErrorMessage}\n" +
                $"</sub-agent>";

            // Fault the synchronous waiter (if any); a background spawn observes
            // this fault via ObserveCompletionFaults so it is never unobserved.
            _ = state.TryCompleteWithException(
                new SubAgentExecutionException(
                    state.AgentId, state.TemplateName, rcm.ErrorMessage));

            if (state.NotifyParentOnCompletion)
            {
                await SendToParentAsync(state, errorText);
            }
        }
        else
        {
            state.Status = SubAgentStatus.Completed;

            var result = lastTextContent ?? "(no text response)";

            var resultText =
                $"<sub-agent name=\"{state.TemplateName}\" " +
                $"id=\"{state.AgentId}\">\n" +
                $"[Completed] Task: {state.Task}\n" +
                $"Result: {result}\n" +
                $"</sub-agent>";

            _ = state.TryCompleteWithResult(result);

            if (state.NotifyParentOnCompletion)
            {
                await SendToParentAsync(state, resultText);
            }
        }
    }

    /// <summary>
    /// Observes faults on a completion the caller will not await (background path),
    /// so a faulted run never surfaces as an UnobservedTaskException during GC.
    /// </summary>
    private static void ObserveCompletionFaults(SubAgentState state)
    {
        _ = state.Completion.Task.ContinueWith(
            static t => { _ = t.Exception; },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Awaits a synchronous sub-agent run's completion. If the parent abandons the wait
    /// (its cancellation token fires), the sub-agent is cancelled too so it stops promptly
    /// and frees its concurrency slot instead of running on, orphaned.
    /// The wait has no independent timeout ceiling: it is bounded only by the caller's
    /// <paramref name="ct"/> (the parent turn's lifetime), while runaway sub-agent runs are
    /// independently bounded by the template's MaxTurnsPerRun.
    /// </summary>
    private static async Task<string> AwaitCompletionAsync(SubAgentState state, CancellationToken ct)
    {
        try
        {
            return await state.Completion.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try
            {
                await state.Cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The sub-agent was already torn down (e.g. concurrent disposal); nothing to cancel.
            }

            throw;
        }
    }

    private async Task SendToParentAsync(SubAgentState state, string text)
    {
        try
        {
            _ = await _parentAgent.SendAsync(
                [new TextMessage { Role = Role.User, Text = text }]);
        }
        catch (Exception ex)
        {
            state.SendToParentFailed = true;
            state.SendToParentError = ex.Message;
            _logger.LogError(
                ex, "Failed to send sub-agent result to parent");
        }
    }

    /// <summary>
    /// Creates a lightweight turn summary from a message for the peek buffer.
    /// Returns null for internal/control messages that should be skipped.
    /// </summary>
    private static SubAgentTurnSummary? CreateTurnSummary(IMessage msg)
    {
        return msg switch
        {
            TextMessage tm when tm.Role == Role.Assistant && !tm.IsThinking => new SubAgentTurnSummary
            {
                MessageType = "text",
                TextPreview = Truncate(tm.Text, 100),
            },
            ToolCallMessage tc => new SubAgentTurnSummary
            {
                MessageType = "tool_call",
                ToolName = tc.FunctionName,
                ToolArgsPreview = Truncate(tc.FunctionArgs, 80),
            },
            ToolCallResultMessage tcr => new SubAgentTurnSummary
            {
                MessageType = "tool_result",
                ToolName = tcr.ToolName,
                TextPreview = Truncate(tcr.Result, 100),
            },
            _ => null,
        };
    }

    private static string? Truncate(string? text, int maxLength)
    {
        return text == null
            ? null
            : text.Length <= maxLength
            ? text
            : text[..maxLength] + "...";
    }

    /// <summary>
    /// Adapts non-generic ILogger to ILogger&lt;MultiTurnAgentLoop&gt;
    /// so the sub-agent loop receives a properly typed logger.
    /// </summary>
    private sealed class SubAgentLoopLoggerAdapter(ILogger inner) : ILogger<MultiTurnAgentLoop>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return inner.BeginScope(state);
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return inner.IsEnabled(logLevel);
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
