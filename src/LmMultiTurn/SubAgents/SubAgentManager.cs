using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AchieveAi.LmDotnetTools.LmCore.Agents;
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
    private int _disposeStarted;

    /// <summary>
    /// Test-only seam: when set, <see cref="CreateSubAgentAsync"/> returns this factory's
    /// <see cref="IMultiTurnAgent"/> instead of building a real <see cref="MultiTurnAgentLoop"/>,
    /// so a unit test can substitute a fake agent (e.g. one whose <c>SubscribeAsync</c> throws a
    /// non-cancellation exception) while still going through the real <see cref="SpawnAsync"/>/
    /// <see cref="MonitorSubAgentAsync"/> plumbing (real gate acquisition, real monitor task).
    /// That is needed to exercise <see cref="MonitorSubAgentAsync"/>'s defensive terminal
    /// <c>catch (Exception)</c> path: every exception a real turn execution raises is already
    /// caught by <see cref="MultiTurnAgentLoop"/>'s own per-run try/catch and surfaces as a normal
    /// <c>RunCompletedMessage(IsError: true)</c>, which the (already-correct) error branch of
    /// <see cref="HandleRunCompletionAsync"/> resolves - so that path can't organically reach the
    /// monitor's own terminal catch. Null (the default) preserves normal production behavior;
    /// production code never sets this.
    /// </summary>
    internal Func<string, SubAgentTemplate, IMultiTurnAgent>? TestAgentFactoryOverride { get; set; }

    /// <summary>
    /// Test-only companion to <see cref="TestAgentFactoryOverride"/>: when set, supplies the OWNED
    /// provider agent returned alongside the fake loop, so a unit test can exercise the real
    /// owned-provider disposal lifecycle (completion disposal, pending-message deferral, restart
    /// recreation) that the plain <see cref="TestAgentFactoryOverride"/> path — which returns a null
    /// owned provider — cannot. Null (the default) keeps the fake loop's owned provider null.
    /// </summary>
    internal Func<string, SubAgentTemplate, IStreamingAgent?>? TestOwnedProviderOverride { get; set; }

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

        // One independent release-guard instance for this gate-acquisition epoch (see
        // GateReleaseGuard) - shared between this method's own failure cleanup below and the
        // monitor task started further down, so whichever notices the run's end first is the
        // one that actually releases the slot.
        var gateGuard = new GateReleaseGuard();

        var agentId = Guid.NewGuid().ToString("N")[..12];
        SubAgentState? state = null;

        try
        {
            var (agent, store, ownedProviderAgent) = await CreateSubAgentAsync(
                agentId,
                template,
                model,
                addTools,
                removeTools
            );

            state = new SubAgentState
            {
                AgentId = agentId,
                TemplateName = templateName,
                Task = task,
                Agent = agent,
                Template = template,
                ModelOverride = model,
                AddTools = addTools,
                RemoveTools = removeTools,
                Store = store,
                Name = name,
                NotifyParentOnCompletion = runInBackground,
            };
            state.SetOwnedProviderAgent(ownedProviderAgent);

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
            state.MonitorTask = MonitorSubAgentAsync(state, gateGuard, cts.Token);

            // Send the task as user input (triggers first turn)
            _ = await agent.SendAsync(
                [new TextMessage { Role = Role.User, Text = task }], ct: ct);

            _logger.LogInformation(
                "Spawned sub-agent {AgentId} from template {Template} (background={Background}) with task: {Task}",
                agentId, templateName, runInBackground, Truncate(task, 100));
        }
        catch
        {
            if (state == null)
            {
                // Failed before a SubAgentState existed (e.g. CreateSubAgent threw): the
                // monitor never started, so this guard is the only path that will ever
                // release the slot.
                gateGuard.ReleaseOnce(_concurrencyGate);
            }
            else
            {
                // Failed after the state was registered - possibly after the monitor already
                // started (e.g. agent.SendAsync threw). Roll back the partial registration so a
                // failed spawn never lingers in Peek/SendMessage lookups, then cancel + observe
                // any started run/monitor tasks so they don't leak as orphaned background work.
                await CleanupFailedSpawnAsync(agentId, name, state, gateGuard);
            }

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
    /// Rolls back a spawn that failed after its <see cref="SubAgentState"/> was registered
    /// (possibly after the monitor already started, e.g. because <c>agent.SendAsync</c> threw):
    /// removes the partial registration from <see cref="_agents"/>/<see cref="_namesToIds"/>,
    /// cancels the sub-agent's <see cref="SubAgentState.Cts"/>, and awaits its
    /// <see cref="SubAgentState.RunTask"/>/<see cref="SubAgentState.MonitorTask"/> (if started)
    /// so neither leaks as an orphaned, unobserved background task. The concurrency slot itself
    /// is released via <paramref name="gateGuard"/> - the same, per-epoch
    /// <see cref="GateReleaseGuard"/> instance the (possibly already-started) monitor holds - so
    /// this is a no-op if the monitor's own completion/finally path already released it first.
    /// </summary>
    private async Task CleanupFailedSpawnAsync(
        string agentId,
        string? name,
        SubAgentState state,
        GateReleaseGuard gateGuard)
    {
        _ = _agents.TryRemove(agentId, out _);
        if (!string.IsNullOrWhiteSpace(name)
            && _namesToIds.TryGetValue(name, out var mappedId)
            && mappedId == agentId)
        {
            _ = _namesToIds.TryRemove(name, out _);
        }

        try
        {
            await state.Cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down by a racing path; nothing to cancel.
        }

        if (state.RunTask != null)
        {
            try { await state.RunTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RunTask faulted during spawn cleanup for sub-agent {AgentId}", agentId);
            }
        }

        if (state.MonitorTask != null)
        {
            try { await state.MonitorTask; }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MonitorTask faulted during spawn cleanup for sub-agent {AgentId}", agentId);
            }
        }

        try { await state.Agent.DisposeAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent dispose failed during spawn cleanup for sub-agent {AgentId}", agentId);
        }

        try { await state.DisposeOwnedProviderAgentAsync(); }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Provider dispose failed during spawn cleanup for sub-agent {AgentId}",
                agentId
            );
        }

        state.Cts.Dispose();
        if (state.Store is IAsyncDisposable disposableStore)
        {
            try { await disposableStore.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Store dispose failed during spawn cleanup for sub-agent {AgentId}", agentId);
            }
        }

        gateGuard.ReleaseOnce(_concurrencyGate);
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

        // Decide how to continue this sub-agent atomically against a concurrent terminal completion and
        // any other concurrent continuation. An admitted "inject into the running loop" holds a send
        // lease that a terminal owned-provider disposal awaits, so a send can never race the provider
        // being disposed; a finished run is restarted with a fresh provider by exactly ONE caller (any
        // others await that restart and then inject into the re-armed loop). See
        // SubAgentState.BeginContinuation.
        bool wasRunning;
        while (true)
        {
            var decision = state.BeginContinuation(runInBackground);

            if (decision.Mode == ContinuationMode.Inject)
            {
                // A finished completion is replaced so this run can be awaited fresh; a pending one
                // (running agent) is kept so existing waiters observe the next resolution.
                state.ResetCompletionIfFinished();

                try
                {
                    // Inject the message into the currently running agent while holding the send lease.
                    // Link the caller token with the run's lifecycle token so a terminal owned-provider
                    // disposal can unblock this send if it wedges — otherwise the lease (which the
                    // disposal waits to drain) could be held forever on a caller token that never cancels.
                    using var linkedCts = state.LinkLifecycleToken(ct);
                    _ = await state.Agent.SendAsync(
                        [new TextMessage { Role = Role.User, Text = prompt }], ct: linkedCts.Token);
                }
                finally
                {
                    state.EndInjectLease();
                }

                _logger.LogInformation(
                    "Sent message to running sub-agent {AgentId}: {Message}",
                    agentId, Truncate(prompt, 100));

                wasRunning = true;
                break;
            }

            if (decision.Mode == ContinuationMode.Restart)
            {
                state.ResetCompletionIfFinished();

                try
                {
                    await RestartRunAsync(state, prompt, ct);
                }
                finally
                {
                    state.EndRestart();
                }

                wasRunning = false;
                break;
            }

            // AwaitRestart: another caller owns the in-flight restart. Wait for it to finish, then
            // re-evaluate — the restart flips the loop back to Running, so the retry injects into it.
            await decision.RestartCompleted!.WaitAsync(ct);
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

        // One independent release-guard instance for this gate-acquisition epoch (see
        // GateReleaseGuard): the previous epoch (the original spawn or an earlier restart)
        // already released its own slot via its OWN guard instance when that run finished (or
        // will do so once its still-in-flight monitor task, awaited below, actually exits) - a
        // fresh instance here, rather than resetting a shared flag in place, means this new
        // epoch's release can never be conflated with (or spuriously consumed by) that old,
        // independent one.
        var gateGuard = new GateReleaseGuard();

        try
        {
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

            // Rebuild the provider pipeline when the previous run's owned provider was disposed at
            // completion OR its terminal disposal FAILED (poisoned): in both cases the provider must not
            // be reused — a failed disposal may have left it partially torn down.
            if (state.HasDisposedOwnedProviderAgent || state.OwnedProviderTerminalDisposeFailed)
            {
                var previousAgent = state.Agent;
                var previousStore = state.Store;
                var (replacementAgent, replacementStore, replacementOwnedProviderAgent) = await CreateSubAgentAsync(
                    state.AgentId,
                    state.Template,
                    state.ModelOverride,
                    state.AddTools,
                    state.RemoveTools
                );

                try { await previousAgent.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Completed agent dispose failed before restart for sub-agent {AgentId}",
                        state.AgentId
                    );
                }

                if (
                    previousStore is IAsyncDisposable disposablePreviousStore
                    && !ReferenceEquals(previousStore, replacementStore)
                )
                {
                    try { await disposablePreviousStore.DisposeAsync(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Completed store dispose failed before restart for sub-agent {AgentId}",
                            state.AgentId
                        );
                    }
                }

                // If the previous terminal disposal FAILED (poisoned), retry disposing that provider
                // before swapping in the replacement so the partially-disposed instance isn't leaked.
                // The disposal guard reset to Idle on the earlier failure, so this genuinely retries;
                // when it had been cleanly disposed the flag is false and this block is skipped.
                if (state.OwnedProviderTerminalDisposeFailed)
                {
                    try { await state.DisposeOwnedProviderAgentAsync(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Retry dispose of poisoned owned provider failed before restart for sub-agent {AgentId}",
                            state.AgentId
                        );
                    }
                }

                state.Agent = replacementAgent;
                state.Store = replacementStore;
                state.SetOwnedProviderAgent(replacementOwnedProviderAgent);
            }

            // Recover conversation history after replacing a completed owned-provider loop, so a
            // continuation uses the fresh provider pipeline while retaining persisted context.
            if (state.Store != null
                && state.Agent is MultiTurnAgentBase agentBase)
            {
                _ = await agentBase.RecoverAsync();
            }

            // Create new CTS and start the loop again
            state.Cts = new CancellationTokenSource();
            var cts = state.Cts;

            // Re-arm the lifecycle cancellation and open a new run generation for this epoch BEFORE the
            // restarted loop can report completion, so (a) injects into the new run link a fresh token
            // and (b) the Running publish below is generation-guarded against a fast completion.
            state.ResetLifecycleCts();
            var runGeneration = state.BeginRunGeneration();

            state.RunTask = state.Agent.RunAsync(cts.Token);

            // Re-subscribe BEFORE sending to avoid subscribe-after-send race
            state.MonitorTask = MonitorSubAgentAsync(state, gateGuard, cts.Token);

            _ = await state.Agent.SendAsync(
                [new TextMessage { Role = Role.User, Text = prompt }], ct: ct);

            // Publish Running as the final step of the restart transition, but skip it if the restarted
            // run already completed-and-disposed (a fast run can finish before this line executes):
            // resurrecting a terminal run to Running would let the next continuation inject through a
            // provider that terminal handling has already disposed.
            _ = state.TryArmRunning(runGeneration);

            _logger.LogInformation(
                "Resumed sub-agent {AgentId} with message: {Message}",
                state.AgentId, Truncate(prompt, 100));
        }
        catch
        {
            // Cancel + observe any run/monitor tasks started before the failure (e.g.
            // agent.SendAsync threw after the new monitor was already subscribed) so they
            // don't leak as orphaned background work. Unlike a failed SpawnAsync, this agent
            // stays registered in _agents/_namesToIds: it is a pre-existing sub-agent whose
            // restart attempt failed, not a fresh, partially-registered one to roll back.
            try
            {
                await state.Cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down by a racing path; nothing to cancel.
            }

            if (state.RunTask != null)
            {
                try { await state.RunTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RunTask faulted during restart cleanup for sub-agent {AgentId}", state.AgentId);
                }
            }

            if (state.MonitorTask != null)
            {
                try { await state.MonitorTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MonitorTask faulted during restart cleanup for sub-agent {AgentId}", state.AgentId);
                }
            }

            try { await state.Agent.DisposeAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent dispose failed during restart cleanup for sub-agent {AgentId}", state.AgentId);
            }

            try { await state.DisposeOwnedProviderAgentAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provider dispose failed during restart cleanup for sub-agent {AgentId}",
                    state.AgentId
                );
            }

            // Idempotent: a no-op if the (now-observed) monitor's own finally already
            // released the slot first (both hold the SAME gateGuard instance created above).
            gateGuard.ReleaseOnce(_concurrencyGate);
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
    /// Observes a sub-agent's completion by id, returning its final text (or throwing its
    /// <see cref="SubAgentExecutionException"/> on failure). Used by the sample-app
    /// SubAgentCompletionTriggerSource so a Wait can observe a background sub-agent.
    /// <para>
    /// Observation is NON-DESTRUCTIVE: if the caller's <paramref name="ct"/> fires, this stops
    /// observing (throws <see cref="OperationCanceledException"/>) but leaves the sub-agent's own
    /// run untouched — unlike <see cref="AwaitCompletionAsync"/> (the synchronous-spawn path), which
    /// cancels the sub-agent when the parent turn is abandoned. A trigger observing a
    /// fire-and-forget background sub-agent must leave it running so its automatic relay can resume
    /// if the wait is cancelled.
    /// </para>
    /// </summary>
    public Task<string> ObserveCompletionAsync(string agentId, CancellationToken ct)
    {
        if (!_agents.TryGetValue(agentId, out var state))
        {
            throw new ArgumentException($"Unknown agent ID '{agentId}'.", nameof(agentId));
        }

        // Await the completion latch directly. On caller-cancel this throws without touching
        // state.Cts, so the sub-agent's run + monitor keep going and its relay resumes.
        return state.Completion.Task.WaitAsync(ct);
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
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

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

            try { await state.DisposeOwnedProviderAgentAsync(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Provider dispose failed for sub-agent {AgentId}", state.AgentId);
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
    private async Task<(IMultiTurnAgent Agent, IConversationStore? Store, IStreamingAgent? OwnedProviderAgent)> CreateSubAgentAsync(
        string agentId,
        SubAgentTemplate template,
        string? modelOverride,
        string[]? addTools,
        string[]? removeTools)
    {
        if (TestAgentFactoryOverride != null)
        {
            return (
                TestAgentFactoryOverride(agentId, template),
                null,
                TestOwnedProviderOverride?.Invoke(agentId, template));
        }

        // Resolve the sub-agent's options with model inheritance (override > template > parent).
        var defaultOptions = ResolveSubAgentOptions(template.DefaultOptions, modelOverride, _parentModelId);
        IStreamingAgent providerAgent;
        IStreamingAgent? ownedProviderAgent = null;
        IConversationStore? store = null;

        try
        {
            if (template.CharacteristicsAgentFactory is { } characteristicsFactory)
            {
                var modelId = string.IsNullOrWhiteSpace(defaultOptions?.ModelId)
                    ? null
                    : defaultOptions.ModelId;
                var provider = characteristicsFactory(
                    new SubAgentCharacteristics(modelId, template.Effort)
                    {
                        IsModelExplicitlySelected =
                            !string.IsNullOrWhiteSpace(modelOverride)
                            || template.IsModelExplicitlySelected,
                        IsModelTierResolved = template.IsModelTierResolved,
                    });
                providerAgent = provider.Agent;
                ownedProviderAgent = provider.OwnsAgent ? provider.Agent : null;

                if (provider.UseParentModel && defaultOptions is not null)
                {
                    defaultOptions = defaultOptions with { ModelId = _parentModelId ?? string.Empty };
                }

                if (provider.ExtraProperties.Count > 0)
                {
                    var requestExtraProperties =
                        defaultOptions?.ExtraProperties
                        ?? ImmutableDictionary<string, object?>.Empty;
                    defaultOptions = (defaultOptions ?? new GenerateReplyOptions()) with
                    {
                        // Template/request values intentionally win over generated reasoning metadata.
                        ExtraProperties = provider.ExtraProperties.SetItems(requestExtraProperties),
                    };
                }
            }
            else
            {
                providerAgent = template.AgentFactory();
            }

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

            return (
                new MultiTurnAgentLoop(
                    providerAgent,
                    registry,
                    threadId: $"subagent-{agentId}",
                    systemPrompt: template.SystemPrompt,
                    defaultOptions: defaultOptions,
                    maxTurnsPerRun: template.MaxTurnsPerRun,
                    store: store,
                    logger: _logger is NullLogger ? null : new SubAgentLoopLoggerAdapter(_logger)
                ),
                store,
                ownedProviderAgent
            );
        }
        catch
        {
            // Roll back partial construction. Attempt each cleanup INDEPENDENTLY so a failure in one
            // (e.g. store disposal throwing) does not skip the other (provider disposal), and let the
            // ORIGINAL construction exception propagate — cleanup failures are logged, never rethrown,
            // so they can't mask the real cause.
            if (store is IAsyncDisposable disposableStore)
            {
                try { await disposableStore.DisposeAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Store dispose failed while rolling back sub-agent {AgentId} construction",
                        agentId
                    );
                }
            }

            try { await DisposeProviderAgentAsync(ownedProviderAgent); }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Provider dispose failed while rolling back sub-agent {AgentId} construction",
                    agentId
                );
            }

            throw;
        }
    }

    /// <summary>
    /// Disposes a provider agent regardless of whether it exposes async or synchronous disposal.
    /// No-op when <paramref name="provider"/> is null or implements neither disposal interface.
    /// </summary>
    private static async ValueTask DisposeProviderAgentAsync(IStreamingAgent? provider)
    {
        if (provider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (provider is IDisposable disposable)
        {
            disposable.Dispose();
        }
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
    /// <param name="state">The sub-agent's state.</param>
    /// <param name="gateGuard">
    /// The <see cref="GateReleaseGuard"/> for the gate-acquisition epoch this monitor was
    /// started for (created by <c>SpawnAsync</c> or <c>RestartRunAsync</c> right after their
    /// respective <c>_concurrencyGate.WaitAsync</c> succeeded). Passed explicitly, rather than
    /// read from a shared field on <paramref name="state"/>, so a later restart's own guard can
    /// never be conflated with this monitor's - see <see cref="GateReleaseGuard"/>.
    /// </param>
    /// <param name="ct">Cancellation token for this run's lifetime.</param>
    private async Task MonitorSubAgentAsync(
        SubAgentState state,
        GateReleaseGuard gateGuard,
        CancellationToken ct)
    {
        string? lastTextContent = null;

        // The concurrency slot is released exactly once per gate-acquisition epoch via
        // gateGuard.ReleaseOnce (an Interlocked-guarded no-op past the first call). A single
        // monitor can observe multiple RunCompletedMessages - a background sub-agent continued
        // in place via SendMessage runs again under the same monitor - but the slot was
        // acquired once (at spawn/restart), so it must be released once: on the first
        // completion, and never again. Releasing per-completion would over-release the
        // semaphore (eventually SemaphoreFullException) and break the concurrency limit. The
        // same gateGuard instance is also held by SpawnAsync/RestartRunAsync's own failure
        // cleanup, so whichever path notices termination first is the one that actually
        // releases it.

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

                    // Release the slot BEFORE the (possibly slow/backpressured) parent relay in
                    // HandleRunCompletionAsync — but ONLY for a TERMINAL completion. A nonterminal
                    // (HasPendingMessages) completion keeps the SAME loop/provider busy processing queued
                    // work, so releasing its permit now would let another sub-agent start while this one
                    // is still active, exceeding MaxConcurrentSubAgents. The permit is held until the run
                    // truly ends: the terminal completion here, or the monitor's finally if the stream
                    // ends first. Idempotent, so that fallback release is a safe no-op afterward.
                    if (!rcm.HasPendingMessages)
                    {
                        gateGuard.ReleaseOnce(_concurrencyGate);
                    }

                    await HandleRunCompletionAsync(state, rcm, lastTextContent);
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

            // Fault the completion latch: the run ended here without ever producing a
            // RunCompletedMessage, so nothing else will resolve it, and an
            // AwaitCompletionAsync/ObserveCompletionAsync waiter would otherwise hang
            // forever. TryCompleteWithException is a no-op if already resolved.
            _ = state.TryCompleteWithException(ex);
        }
        finally
        {
            // Fallback: if no completion was ever handled (the stream ended, was
            // cancelled, or faulted before any RunCompletedMessage), the slot still
            // needs releasing. ReleaseOnce is idempotent, so this is a no-op when
            // a completion already released it.
            gateGuard.ReleaseOnce(_concurrencyGate);
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
        // A run that still has queued messages is NOT terminal: another run will follow and reuse the
        // same loop/provider, so neither flip the sub-agent terminal nor dispose its owned provider
        // here — the final completion (HasPendingMessages == false) resolves and, if owned, disposes.
        if (rcm.HasPendingMessages)
        {
            return;
        }

        // Transition out of Running BEFORE disposing the owned provider, atomically against a
        // concurrent SendMessageAsync (see SubAgentState.BeginContinuation). This blocks new inject
        // admissions and waits for any in-flight admitted send to finish, so the disposal below can
        // never overlap a send through the provider; a racing continuation then observes the finished
        // status and takes the restart path (which recreates a fresh provider).
        await state.BeginTerminalDisposalAsync(rcm.IsError);

        // The concurrency slot is released by the monitor (via its GateReleaseGuard), exactly
        // once per gate-acquisition epoch — not here, because a single monitor may handle
        // several completions when a background sub-agent is continued in place via SendMessage.
        // An explicit/tier provider is scoped to a single completed run. Dispose it before any
        // completion relay can block; a later continuation recreates its loop and provider through
        // the same characteristics factory, while borrowed parent/template agents remain untouched.
        // EndTerminalDisposal clears the terminating flag so a later restart's re-arm admits injects.
        try
        {
            try { await state.DisposeOwnedProviderAgentAsync(); }
            catch (Exception ex)
            {
                // Poison the run's provider: a continuation must rebuild a fresh one rather than reuse
                // this partially-disposed instance (the restart path retries disposing it). Clearing the
                // terminating flag below still lets a restart proceed — but against a fresh provider.
                state.MarkOwnedProviderTerminalDisposeFailed();
                _logger.LogWarning(
                    ex,
                    "Provider dispose failed at completion for sub-agent {AgentId}",
                    state.AgentId
                );
            }
        }
        finally
        {
            state.EndTerminalDisposal();
        }

        if (rcm.IsError)
        {
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
            // Deliver the completion as a typed notification, not a plain user turn: the parent LLM
            // reads it as an async response to the sub-agent spawn, and the UI renders a pill. The raw
            // <sub-agent …> block stays in Detail so downstream parsers (e.g. LmWorkflow) still find it.
            _ = await _parentAgent.SendAsync(
                [NotifyMessage.Create(
                    NotifyKinds.SubAgentCompletion,
                    detail: text,
                    sourceToolName: "Agent",
                    sourceToolCallId: state.AgentId,
                    label: state.TemplateName)]);
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
