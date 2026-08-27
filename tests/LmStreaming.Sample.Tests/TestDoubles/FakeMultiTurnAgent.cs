using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A minimal <see cref="IMultiTurnAgent"/> stand-in. It deliberately does NOT declare
/// <c>ISpawnSuppressingAgent</c>, so it also serves as the "host cannot enforce per-turn spawn suppression"
/// fixture — see <see cref="SpawnSuppressingFakeAgent"/> for the capable counterpart.
/// </summary>
internal class FakeMultiTurnAgent : IMultiTurnAgent, IAcceptanceReportingAgent
{
    public FakeMultiTurnAgent(string threadId)
    {
        ThreadId = threadId;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// Declared so the pool will pool it: since #442 the pool refuses an agent that is not
    /// <see cref="IAcceptanceReportingAgent"/>, because the accepted-input ledger has no other source.
    /// The send stubs below report through it in the product's own order — announce, then take the
    /// input — so a host path exercised against this double populates the ledger the way it will in
    /// production.
    /// </para>
    /// <para>
    /// Use <see cref="PooledReportingAgent"/> instead whenever the REPORTING is what is under test:
    /// this class re-implements the accept path, so pinning it would pin the double rather than the
    /// product.
    /// </para>
    /// </remarks>
    public IInputAcceptanceObserver? InputAcceptanceObserver { get; set; }

    public string? CurrentRunId { get; set; }

    public string ThreadId { get; }

    public bool IsRunning { get; set; } = true;

    /// <summary>When true, <see cref="DisposeAsync"/> throws — used to prove a switch tolerates a
    /// failure tearing down the PREVIOUS agent (the new one is already swapped in).</summary>
    public bool ThrowOnDispose { get; set; }

    /// <summary>When true, <see cref="TrySendAsync"/> returns null — simulates the input channel
    /// being full (the controller maps this to a 503).</summary>
    public bool RejectAsQueueFull { get; set; }

    /// <summary>When true, <see cref="TrySendAsync"/> throws — simulates a durable accepted-input
    /// write failure (the controller lets this propagate to a 500).</summary>
    public bool ThrowOnTrySend { get; set; }

    /// <summary>
    /// When true, both send paths throw <see cref="InputAcceptanceRefusedException"/> — the agent was
    /// replaced while the send was reporting its accept, so the observer refused it and nothing was
    /// queued (#442).
    /// </summary>
    /// <remarks>
    /// A switch rather than a real desynchronised pool: reproducing the race would mean parking a
    /// report inside the pool's per-thread lock while a swap runs, which is a window no test can hit
    /// deterministically. What a host test needs from here is the OUTCOME the race produces, and the
    /// product's own refusal is pinned where it lives, in
    /// <c>LmMultiTurn.Tests.InputAcceptanceObserverTests</c> and against the real pool in
    /// <c>MultiTurnAgentPoolHandoffTests.AReportFromAnAgentTheThreadNoLongerHolds_DoesNotMarkThePooledOne</c>.
    /// </remarks>
    public bool RefuseAccepts { get; set; }

    /// <summary>How many inputs reached the enqueue path. Lets a test prove a request was refused
    /// BEFORE anything was queued, rather than merely reported as unsuppressed afterwards.</summary>
    public int SendCount { get; private set; }

    /// <summary>
    /// When set, <see cref="TrySendAsync(List{IMessage}, string?, string?, CancellationToken)"/> signals
    /// <see cref="SendEntered"/> and then parks until this gate is completed. It holds a send inside the
    /// agent — admitted but not yet acknowledged — so a test can drive a SECOND send through the whole
    /// controller path while the first is still in flight, which is the interleave a retry that overlaps
    /// the send it is retrying actually hits.
    /// </summary>
    public TaskCompletionSource? SendGate { get; set; }

    /// <summary>Completes once a gated send has arrived and parked.</summary>
    public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        _ = messages;
        _ = parentRunId;
        _ = ct;

        if (RefuseAccepts)
        {
            // Ahead of SendCount, because a refused send never took the input: counting it would let a
            // test claiming "the turn did not reach the agent" pass against one that did.
            throw new InputAcceptanceRefusedException(ThreadId, inputId ?? "unknown");
        }

        SendCount++;
        var receiptId = inputId ?? Guid.NewGuid().ToString("N");

        // Announced BEFORE the input is taken, exactly as MultiTurnAgentBase's mint sites do: a host
        // that learns of the accept only afterwards has the window this ledger exists to close.
        if (InputAcceptanceObserver?.OnInputAccepted(ThreadId, receiptId, this) == false)
        {
            // Honoured, not ignored: the product refuses the enqueue when the observer says this
            // agent is no longer the conversation's, and a double that queued anyway would let a
            // host test pass over the silent loss (#442).
            throw new InputAcceptanceRefusedException(ThreadId, receiptId);
        }

        return ValueTask.FromResult(new SendReceipt(receiptId, inputId, DateTimeOffset.UtcNow));
    }

    public async ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        if (SendGate is { } gate)
        {
            _ = SendEntered.TrySetResult();

            // Bounded so a mis-wired fixture fails loudly instead of hanging the run. It is a guard, never
            // a sleep: the wait ends the moment the test opens the gate.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }

        // Ahead of the report, like the real durable write it stands in for: a failure there means
        // nobody accepted the input, so there is nothing to announce and nothing to withdraw.
        if (ThrowOnTrySend)
        {
            throw new InvalidOperationException("Simulated durable accepted-input write failure.");
        }

        if (RejectAsQueueFull)
        {
            // The product reports and then rescinds on a full channel. The net effect on the ledger is
            // the same either way, and modelling the pair here would let a test pass on a rescind that
            // matched the wrong id.
            return null;
        }

        return await SendAsync(messages, inputId, parentRunId, ct);
    }

#pragma warning disable CS1998, IDE0391 // Async iterator lacks 'await' - intentional empty stub using yield break
    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = userInput;
        _ = ct;
        yield break;
    }

    /// <summary>
    /// When true, <see cref="SubscribeAsync"/> parks on its cancellation token instead of completing
    /// immediately - a live agent whose stream stays open, which is what the default (an instantly
    /// finished stream) cannot model.
    /// </summary>
    /// <remarks>
    /// It exists for the interleave a socket-level test otherwise cannot reach. A connection races two
    /// tasks - the outbound subscription pump and the inbound receive pump - and completing EITHER
    /// tears the connection down. With a stream that ends the moment the agent is disposed, the only
    /// interleave a test can produce is "teardown first", so the inbound path's behaviour when a
    /// message arrives DURING a handoff is untestable. Parking the stream pins the other interleave.
    /// </remarks>
    public bool KeepSubscriptionOpen { get; set; }

    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!KeepSubscriptionOpen)
        {
            yield break;
        }

        // Serving from a channel rather than parking on the token keeps the "stays open" behaviour the
        // property promises AND lets StartRun publish into a live stream. With nothing published it
        // still parks, which is what the socket-teardown tests depend on.
        var subscriptionId = Guid.NewGuid();
        var channel = System.Threading.Channels.Channel.CreateUnbounded<IMessage>();
        _subscribers[subscriptionId] = channel;
        try
        {
            await foreach (var published in channel.Reader.ReadAllAsync(ct))
            {
                yield return published;
            }
        }
        finally
        {
            _ = _subscribers.TryRemove(subscriptionId, out _);
        }
    }
#pragma warning restore CS1998, IDE0391

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        Guid,
        System.Threading.Channels.Channel<IMessage>
    > _subscribers = new();

    /// <summary>The run id of the most recently COMPLETED run - mirrors <c>MultiTurnAgentBase</c>.</summary>
    public string? LatestRunId { get; private set; }

    /// <summary>
    /// Puts the agent on <paramref name="runId"/> and publishes the <see cref="RunAssignmentMessage"/>
    /// echoing <paramref name="inputIds"/>, exactly as every real agent loop does when a run picks
    /// queued input up. That echo is the evidence the pool's accepted-input ledger retires on, so a
    /// test that sets <see cref="CurrentRunId"/> directly pins a state the product never produces.
    /// </summary>
    public void StartRun(string runId, params string[] inputIds)
    {
        CurrentRunId = runId;
        IsRunning = true;
        Publish(
            new RunAssignmentMessage
            {
                Assignment = new RunAssignment(runId, Guid.NewGuid().ToString("N"), [.. inputIds]),
                ThreadId = ThreadId,
            }
        );
    }

    /// <summary>
    /// Ends the current run the way <c>MultiTurnAgentBase.CompleteRunAsync</c> does: the id moves to
    /// <see cref="LatestRunId"/> and <see cref="CurrentRunId"/> goes back to null. A test that leaves
    /// <see cref="CurrentRunId"/> set after a run "finished" is pinning an impossible state.
    /// </summary>
    public void CompleteRun()
    {
        LatestRunId = CurrentRunId;
        CurrentRunId = null;
    }

    private void Publish(IMessage message)
    {
        foreach (var subscriber in _subscribers.Values)
        {
            _ = subscriber.Writer.TryWrite(message);
        }
    }

    /// <summary>
    /// Replaces the default park-until-cancelled run loop. Set it when a test needs the run task to do
    /// something the pool has to wait for.
    /// </summary>
    /// <remarks>
    /// The pairing with <see cref="StopAsync"/> below is the point rather than an oversight: nothing in
    /// <see cref="IMultiTurnAgent"/> obliges a stop to drain the run, and this fake's stop does not. An
    /// agent shaped like that is what proves the pool waits for the task IT started
    /// (<c>AgentEntry.RunTask</c>) instead of leaning on the agent's teardown to have done it.
    /// </remarks>
    public Func<CancellationToken, Task>? RunBehavior { get; set; }

    public Task RunAsync(CancellationToken ct = default)
    {
        return RunBehavior is null ? Task.Delay(Timeout.InfiniteTimeSpan, ct) : RunBehavior(ct);
    }

    public Task StopAsync(TimeSpan? timeout = null)
    {
        _ = timeout;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("Simulated dispose failure for the previous agent.");
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A fake that CAN carry per-turn sub-agent spawn suppression. Declaring
/// <see cref="ISpawnSuppressingAgent"/> plus <see cref="EnforcesSpawnSuppression"/> is the capability signal
/// the controller gates on, and <see cref="LastInput"/> records the <see cref="UserInput"/> it received so a
/// test can prove the flag actually reached the agent rather than merely being echoed back.
/// </summary>
internal sealed class SpawnSuppressingFakeAgent(string threadId)
    : FakeMultiTurnAgent(threadId), ISpawnSuppressingAgent
{
    /// <summary>The last input handed to the capability-aware send path (null until one arrives).</summary>
    public UserInput? LastInput { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// Settable so a test can build the "declares the interface but cannot keep the promise" fixture — the
    /// case the controller must refuse before it enqueues anything.
    /// </remarks>
    public bool EnforcesSpawnSuppression { get; set; } = true;

    /// <summary>
    /// When false the agent claims the capability but its receipt does not confirm enforcement for the
    /// input — the shape of an implementation that accepts a particular flag and then ignores it. The host
    /// must relay the RECEIPT, so it must not turn that into a promise.
    /// </summary>
    public bool ConfirmsSuppressionOnReceipt { get; set; } = true;

    public async ValueTask<SendReceipt?> TrySendAsync(UserInput input, CancellationToken ct = default)
    {
        LastInput = input;
        var receipt = await TrySendAsync(input.Messages, input.InputId, input.ParentRunId, ct);
        return receipt is null
            ? null
            : receipt with
            {
                SpawningSuppressed = input.SuppressSubAgentSpawning && ConfirmsSuppressionOnReceipt,
            };
    }
}
