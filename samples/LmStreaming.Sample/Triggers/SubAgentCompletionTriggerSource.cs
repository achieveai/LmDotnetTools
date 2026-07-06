using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

/// <summary>
/// Fires when a specific background sub-agent completes. Flips that sub-agent's
/// <c>NotifyParentOnCompletion</c> to false at arm time so the completion arrives once (via the
/// trigger envelope), not twice. Restores the flag on dispose if the sub-agent hasn't yet
/// completed — otherwise a cancel/timeout before completion would strand the result (no trigger
/// fire and no automatic relay). Not restorable: an in-process Task can't survive a restart.
/// </summary>
/// <remarks>
/// The loop's <see cref="SubAgentManager"/> is built inside <c>MultiTurnAgentLoop</c>'s
/// constructor, after the sample's trigger registrations are already assembled, so this source
/// can't be handed the manager directly at registration time. It resolves the manager lazily
/// through a <c>Func&lt;SubAgentManager?&gt;</c> accessor, which the sample wires to read the
/// just-constructed loop's <c>SubAgentManager</c> property.
/// </remarks>
public sealed class SubAgentCompletionTriggerSource : ITriggerSource
{
    /// <summary>The registered kind token.</summary>
    public const string KindName = "subagent";

    /// <summary>Human-readable args hint for the tool contract.</summary>
    public const string ArgsSchemaText = "{ agentId: \"<id of a spawned sub-agent>\" }";

    /// <summary>Capabilities: block + notify, no restore.</summary>
    public static TriggerCapabilities Capabilities { get; } =
        new(SupportsBlock: true, SupportsNotify: true, SupportsRestore: false);

    private readonly Func<SubAgentManager?> _managerAccessor;

    public SubAgentCompletionTriggerSource(Func<SubAgentManager?> managerAccessor)
    {
        ArgumentNullException.ThrowIfNull(managerAccessor);
        _managerAccessor = managerAccessor;
    }

    /// <inheritdoc />
    public ValueTask<IArmedTrigger> ArmAsync(
        TriggerArmRequest request, ITriggerEventSink eventSink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventSink);

        var manager = _managerAccessor()
            ?? throw new ArgumentException("subagent waits require a sub-agent-enabled conversation.");

        var agentId = ParseAgentId(request.ArgsJson);
        // Throws ArgumentException (arm-time rejection) if the id is unknown.
        manager.SetNotifyParentOnCompletion(agentId, false);

        var handle = new SubAgentArmedTrigger(request.WaitId, agentId, manager, eventSink);
        return ValueTask.FromResult<IArmedTrigger>(handle);
    }

    private static string ParseAgentId(string argsJson)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"subagent args is not valid JSON: {ex.Message}", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("subagent args must be a JSON object.");
            }

            var id = root.TryGetProperty("agentId", out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("subagent requires an 'agentId'.");
            }

            return id;
        }
    }

    /// <summary>
    /// Per-arm handle. Awaits exactly one <see cref="SubAgentManager.ObserveCompletionAsync"/> call
    /// and fires at most once, either with the sub-agent's result or a
    /// <see cref="SubAgentExecutionException"/>'s message.
    /// </summary>
    private sealed class SubAgentArmedTrigger : IArmedTrigger
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly SubAgentManager _manager;
        private readonly string _agentId;
        private readonly Task _watch;
        private int _completed; // 1 once the sub-agent completed (relay stays suppressed).
        private int _disposed;

        public SubAgentArmedTrigger(string waitId, string agentId, SubAgentManager manager, ITriggerEventSink sink)
        {
            WaitId = waitId;
            _agentId = agentId;
            _manager = manager;
            _watch = RunAsync(sink, _cts.Token);
        }

        public string WaitId { get; }

        private async Task RunAsync(ITriggerEventSink sink, CancellationToken ct)
        {
            // Yield first so the fire is always asynchronous — never synchronous within ArmAsync.
            await Task.Yield();

            string result;
            try
            {
                result = await _manager.ObserveCompletionAsync(_agentId, ct);
            }
            catch (OperationCanceledException)
            {
                return; // cancelled before completion — dispose restores the flag.
            }
            catch (SubAgentExecutionException ex)
            {
                Interlocked.Exchange(ref _completed, 1);
                var errPayload = JsonSerializer.Serialize(new { agentId = _agentId, error = ex.Message });
                await sink.FireAsync(new TriggerFireEvent(errPayload), ct);
                return;
            }

            Interlocked.Exchange(ref _completed, 1);
            var payload = JsonSerializer.Serialize(new { agentId = _agentId, result });
            await sink.FireAsync(new TriggerFireEvent(payload), ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            await _cts.CancelAsync();

            // If the sub-agent never completed under this wait, restore automatic relay so its
            // eventual result is not stranded. If it already completed, the trigger delivered it —
            // leave the flag suppressed.
            if (Volatile.Read(ref _completed) == 0)
            {
                try
                {
                    _manager.SetNotifyParentOnCompletion(_agentId, true);
                }
                catch (ArgumentException)
                {
                    // Sub-agent already gone — nothing to restore.
                }
            }

            // Do NOT await _watch here (same reasoning as ProcessTriggerSource/TimerTriggerSource):
            // disposal is typically invoked from within the runtime's own fire-handling callback,
            // and awaiting our own still-running task would deadlock. Dispose the CTS once it
            // settles instead.
            _ = _watch.ContinueWith(
                _ =>
                {
                    try
                    {
                        _cts.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already disposed — nothing to do.
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
