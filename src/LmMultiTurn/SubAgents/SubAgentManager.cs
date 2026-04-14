using System.Collections.Concurrent;
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
    private readonly IReadOnlyList<FunctionContract> _parentContracts;
    private readonly IDictionary<string, Func<string, Task<string>>> _parentHandlers;
    private readonly IDictionary<string, Func<string, Task<ToolCallResult>>>? _parentMultiModalHandlers;
    private readonly SubAgentOptions _options;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, SubAgentState> _agents = new();
    private readonly SemaphoreSlim _concurrencyGate;

    public SubAgentManager(
        IMultiTurnAgent parentAgent,
        IReadOnlyList<FunctionContract> parentContracts,
        IDictionary<string, Func<string, Task<string>>> parentHandlers,
        IDictionary<string, Func<string, Task<ToolCallResult>>>? parentMultiModalHandlers,
        SubAgentOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(parentAgent);
        ArgumentNullException.ThrowIfNull(parentContracts);
        ArgumentNullException.ThrowIfNull(parentHandlers);
        ArgumentNullException.ThrowIfNull(options);

        _parentAgent = parentAgent;
        _parentContracts = parentContracts;
        _parentHandlers = parentHandlers;
        _parentMultiModalHandlers = parentMultiModalHandlers;
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _concurrencyGate = new SemaphoreSlim(
            options.MaxConcurrentSubAgents,
            options.MaxConcurrentSubAgents);
    }

    /// <summary>
    /// Spawn a new sub-agent from a named template.
    /// </summary>
    public async Task<string> SpawnAsync(
        string templateName,
        string task,
        string[]? addTools = null,
        string[]? removeTools = null,
        CancellationToken ct = default)
    {
        if (!_options.Templates.TryGetValue(templateName, out var template))
        {
            throw new ArgumentException(
                $"Unknown template '{templateName}'. " +
                $"Available: {string.Join(", ", _options.Templates.Keys)}",
                nameof(templateName));
        }

        if (!await _concurrencyGate.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            throw new InvalidOperationException(
                $"Max concurrent sub-agents ({_options.MaxConcurrentSubAgents}) " +
                $"reached. Wait for a sub-agent to complete or increase the limit.");
        }

        var agentId = Guid.NewGuid().ToString("N")[..12];

        try
        {
            var agent = CreateSubAgent(agentId, template, addTools, removeTools, out var store);

            var state = new SubAgentState
            {
                AgentId = agentId,
                TemplateName = templateName,
                Task = task,
                Agent = agent,
                Store = store,
            };

            _agents[agentId] = state;

            // Start the agent loop in the background
            var cts = state.Cts;
            state.RunTask = agent.RunAsync(cts.Token);

            // Start monitoring BEFORE sending the task to avoid subscribe-after-send race:
            // if SendAsync triggers a fast completion before the monitor subscribes,
            // the RunCompletedMessage would fire with no subscriber listening.
            state.MonitorTask = MonitorSubAgentAsync(state, cts.Token);

            // Send the task as user input (triggers first turn)
            await agent.SendAsync(
                [new TextMessage { Role = Role.User, Text = task }], ct: ct);

            _logger.LogInformation(
                "Spawned sub-agent {AgentId} from template {Template} with task: {Task}",
                agentId, templateName, Truncate(task, 100));

            return JsonSerializer.Serialize(new
            {
                agent_id = agentId,
                template = templateName,
                status = "spawned",
            });
        }
        catch
        {
            // Release the gate on failure
            _concurrencyGate.Release();
            throw;
        }
    }

    /// <summary>
    /// Resume or send a new message to an existing sub-agent.
    /// </summary>
    public async Task<string> ResumeAsync(
        string agentId,
        string newMessage,
        CancellationToken ct = default)
    {
        if (!_agents.TryGetValue(agentId, out var state))
        {
            throw new ArgumentException(
                $"Unknown agent ID '{agentId}'.", nameof(agentId));
        }

        if (state.Status == SubAgentStatus.Running)
        {
            // Inject message into the currently running agent
            await state.Agent.SendAsync(
                [new TextMessage { Role = Role.User, Text = newMessage }], ct: ct);

            return JsonSerializer.Serialize(new
            {
                agent_id = agentId,
                status = "message_sent",
            });
        }

        // Agent is completed/error/stopped - need to resume
        if (!await _concurrencyGate.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            throw new InvalidOperationException(
                $"Max concurrent sub-agents ({_options.MaxConcurrentSubAgents}) " +
                $"reached. Cannot resume agent '{agentId}'.");
        }

        try
        {
            // Recover conversation history if a store is available
            if (state.Store != null
                && state.Agent is MultiTurnAgentBase agentBase)
            {
                await agentBase.RecoverAsync();
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

            await state.Agent.SendAsync(
                [new TextMessage { Role = Role.User, Text = newMessage }], ct: ct);

            state.Status = SubAgentStatus.Running;

            _logger.LogInformation(
                "Resumed sub-agent {AgentId} with message: {Message}",
                agentId, Truncate(newMessage, 100));

            return JsonSerializer.Serialize(new
            {
                agent_id = agentId,
                status = "resumed",
            });
        }
        catch
        {
            _concurrencyGate.Release();
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
    /// Get the list of available template names.
    /// </summary>
    public IReadOnlyList<string> GetTemplateNames() =>
        _options.Templates.Keys.ToList().AsReadOnly();

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
        _concurrencyGate.Dispose();
    }

    /// <summary>
    /// Creates a MultiTurnAgentLoop configured for a sub-agent with filtered tools.
    /// </summary>
    private IMultiTurnAgent CreateSubAgent(
        string agentId,
        SubAgentTemplate template,
        string[]? addTools,
        string[]? removeTools,
        out IConversationStore? store)
    {
        var providerAgent = template.AgentFactory();

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

            Func<string, Task<ToolCallResult>>? mmHandler = null;
            _parentMultiModalHandlers?.TryGetValue(contract.Name, out mmHandler);

            registry.AddFunction(contract, handler, mmHandler, "ParentTools");
        }

        return new MultiTurnAgentLoop(
            providerAgent,
            registry,
            threadId: $"subagent-{agentId}",
            systemPrompt: template.SystemPrompt,
            defaultOptions: template.DefaultOptions,
            maxTurnsPerRun: template.MaxTurnsPerRun,
            store: store,
            logger: _logger is NullLogger ? null : new SubAgentLoopLoggerAdapter(_logger));
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
                result.Add(tool);
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
                result.Remove(tool);
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
        var completionHandled = false;

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
                        state.TurnBuffer.TryDequeue(out _);
                    }
                }

                // Track last assistant text for completion result
                if (msg is TextMessage tm
                    && tm.Role == Role.Assistant
                    && !tm.IsThinking)
                {
                    lastTextContent = tm.Text;
                }

                if (msg is RunCompletedMessage rcm)
                {
                    state.LastResult = lastTextContent;
                    await HandleRunCompletionAsync(state, rcm, lastTextContent);
                    completionHandled = true;
                    lastTextContent = null;
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
            // Guard against semaphore slot leaks: if HandleRunCompletionAsync
            // was never called (stream ended without RunCompletedMessage,
            // cancellation, or exception), release the semaphore here.
            if (!completionHandled)
            {
                _concurrencyGate.Release();
            }
        }
    }

    /// <summary>
    /// Handles a sub-agent run completion by updating state and notifying the parent.
    /// </summary>
    private async Task HandleRunCompletionAsync(
        SubAgentState state,
        RunCompletedMessage rcm,
        string? lastTextContent)
    {
        try
        {
            if (rcm.IsError)
            {
                state.Status = SubAgentStatus.Error;

                var errorText =
                    $"<sub-agent name=\"{state.TemplateName}\" " +
                    $"id=\"{state.AgentId}\">\n" +
                    $"[Error] Task: {state.Task}\n" +
                    $"Error: {rcm.ErrorMessage}\n" +
                    $"</sub-agent>";

                await SendToParentAsync(state, errorText);
            }
            else
            {
                state.Status = SubAgentStatus.Completed;

                var resultText =
                    $"<sub-agent name=\"{state.TemplateName}\" " +
                    $"id=\"{state.AgentId}\">\n" +
                    $"[Completed] Task: {state.Task}\n" +
                    $"Result: {lastTextContent ?? "(no text response)"}\n" +
                    $"</sub-agent>";

                await SendToParentAsync(state, resultText);
            }
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    private async Task SendToParentAsync(SubAgentState state, string text)
    {
        try
        {
            await _parentAgent.SendAsync(
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
        switch (msg)
        {
            case TextMessage tm when tm.Role == Role.Assistant && !tm.IsThinking:
                return new SubAgentTurnSummary
                {
                    MessageType = "text",
                    TextPreview = Truncate(tm.Text, 100),
                };

            case ToolCallMessage tc:
                return new SubAgentTurnSummary
                {
                    MessageType = "tool_call",
                    ToolName = tc.FunctionName,
                    ToolArgsPreview = Truncate(tc.FunctionArgs, 80),
                };

            case ToolCallResultMessage tcr:
                return new SubAgentTurnSummary
                {
                    MessageType = "tool_result",
                    ToolName = tcr.ToolName,
                    TextPreview = Truncate(tcr.Result, 100),
                };

            default:
                return null;
        }
    }

    private static string? Truncate(string? text, int maxLength)
    {
        if (text == null)
        {
            return null;
        }

        return text.Length <= maxLength
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
            where TState : notnull =>
            inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) =>
            inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
