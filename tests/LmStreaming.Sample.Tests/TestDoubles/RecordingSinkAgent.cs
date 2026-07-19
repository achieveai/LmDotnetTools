using System.Runtime.CompilerServices;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// An <see cref="IMultiTurnAgent"/> that ALSO implements <see cref="ISubAgentContextSink"/> — the
/// role interface the context-discovery injector probes to route a discovery to the opening
/// sub-agent. It records both fallback fan-out sends (<see cref="SendAsync"/> → <see cref="SentMessages"/>)
/// and routed deliveries (<see cref="TryDeliverContextAsync"/> → <see cref="DeliveredMessages"/>),
/// and returns a canned <see cref="SubAgentContextDeliveryResult"/> so a test can drive each branch
/// of the injector's aggregation (Delivered / TargetNotDeliverable / NotOwned) deterministically —
/// without needing the real <c>MultiTurnAgentLoop</c> sink (that lands in the LmMultiTurn half). A
/// test that must decide the result by consult order or throw mid-delivery sets
/// <see cref="DeliverBehavior"/>, a per-call hook that overrides the canned result.
/// </summary>
internal sealed class RecordingSinkAgent : IMultiTurnAgent, ISubAgentContextSink
{
    private readonly List<IMessage> _sent = [];
    private readonly List<IMessage> _delivered = [];
    private readonly Lock _lock = new();

    public RecordingSinkAgent(string threadId, SubAgentContextDeliveryResult cannedResult)
    {
        ThreadId = threadId;
        CannedResult = cannedResult;
    }

    public string ThreadId { get; }

    public string? CurrentRunId { get; set; }

    public bool IsRunning { get; set; } = true;

    /// <summary>Result <see cref="TryDeliverContextAsync"/> returns for every call.</summary>
    public SubAgentContextDeliveryResult CannedResult { get; set; }

    /// <summary>
    /// When set, <see cref="TryDeliverContextAsync"/> THROWS this instead of returning a result —
    /// modelling a misbehaving sink whose failure the injector cannot disambiguate from a partial
    /// delivery (the "ambiguous" outcome).
    /// </summary>
    public Exception? ThrowOnDeliver { get; set; }

    /// <summary>
    /// Optional per-call delivery hook. When set, <see cref="TryDeliverContextAsync"/> invokes it
    /// (after bumping <see cref="DeliverCallCount"/>/<see cref="LastDeliveredAgentId"/>) and uses its
    /// return value instead of <see cref="CannedResult"/>; if the hook THROWS, that throw propagates —
    /// modelling a sink that decides its outcome by consult order or fails mid-delivery. This lets one
    /// double serve both the canned-result and programmable-sink test needs.
    /// </summary>
    public Func<string, IReadOnlyList<IMessage>, SubAgentContextDeliveryResult>? DeliverBehavior { get; set; }

    /// <summary>Number of times <see cref="TryDeliverContextAsync"/> was invoked.</summary>
    public int DeliverCallCount { get; private set; }

    /// <summary>The <c>agentId</c> passed to the most recent <see cref="TryDeliverContextAsync"/>.</summary>
    public string? LastDeliveredAgentId { get; private set; }

    /// <summary>Messages delivered via the routed path (only recorded when the canned result is
    /// <see cref="SubAgentContextDeliveryResult.Delivered"/>).</summary>
    public IReadOnlyList<IMessage> DeliveredMessages
    {
        get
        {
            lock (_lock)
            {
                return [.. _delivered];
            }
        }
    }

    /// <summary>Messages enqueued via the fallback fan-out path (<see cref="SendAsync"/>).</summary>
    public IReadOnlyList<IMessage> SentMessages
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    public Task<SubAgentContextDeliveryResult> TryDeliverContextAsync(
        string agentId,
        IReadOnlyList<IMessage> messages,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        lock (_lock)
        {
            DeliverCallCount++;
            LastDeliveredAgentId = agentId;
            if (ThrowOnDeliver is not null)
            {
                throw ThrowOnDeliver;
            }

            // A per-call behaviour hook lets a test decide the result by consult order or throw
            // mid-delivery (its throw propagates unchanged, modelling the ambiguous sink); with no
            // hook the canned result stands.
            var result = DeliverBehavior is not null ? DeliverBehavior(agentId, messages) : CannedResult;

            if (result == SubAgentContextDeliveryResult.Delivered)
            {
                _delivered.AddRange(messages);
            }

            return Task.FromResult(result);
        }
    }

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        _ = parentRunId;
        _ = ct;
        lock (_lock)
        {
            _sent.AddRange(messages);
        }

        var receiptId = inputId ?? Guid.NewGuid().ToString("N");
        return ValueTask.FromResult(new SendReceipt(receiptId, inputId, DateTimeOffset.UtcNow));
    }

    public async ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        return await SendAsync(messages, inputId, parentRunId, ct);
    }

#pragma warning disable CS1998, IDE0391
    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = userInput;
        _ = ct;
        yield break;
    }

    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = ct;
        yield break;
    }
#pragma warning restore CS1998, IDE0391

    public Task RunAsync(CancellationToken ct = default)
    {
        return Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    public Task StopAsync(TimeSpan? timeout = null)
    {
        _ = timeout;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}
