namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A REAL <see cref="MultiTurnAgentBase"/> for the pool tests, so the accept path under test is the
/// product's own — the receipt id minted inside <c>SendAsync</c>/<c>TrySendAsync</c>, reported through
/// the product's own <see cref="IInputAcceptanceObserver"/> wiring.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FakeMultiTurnAgent"/> cannot stand in here. It is not
/// <see cref="MultiTurnAgentBase"/>-derived, so it reports nothing — which makes it the right double
/// for the OTHER half of the ledger (a pooled agent whose acceptances only the host knows about) and
/// the wrong one for this half. Using it here would pin a fake's re-implementation of the accept path
/// rather than the accept path.
/// </para>
/// <para>
/// Its run loop does not drain by default, which is the state the whole ledger exists for: an input
/// accepted and not yet picked up leaves <c>CurrentRunId</c> null, so the pool's
/// <c>IsEntryInProgress</c> reads the entry as idle and only the ledger can say otherwise. Set
/// <see cref="DrainInputs"/> to let a run actually take the queued input and publish the
/// <c>RunAssignmentMessage</c> that names it — the evidence the ledger retires on.
/// </para>
/// </remarks>
internal sealed class PooledReportingAgent : MultiTurnAgentBase
{
    public PooledReportingAgent(string threadId, int inputChannelCapacity = 100)
        : base(threadId, inputChannelCapacity: inputChannelCapacity) { }

    /// <summary>
    /// When true the run loop drains queued inputs and starts a run naming them. Default false parks
    /// the loop, holding every accepted input in the channel unstarted.
    /// </summary>
    public bool DrainInputs { get; set; }

    /// <summary>Completes once the run loop has started a run for a drained batch.</summary>
    public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Held closed until <see cref="OpenDrainGate"/>, so a caller can observe the accepted-but-unstarted
    /// state before a run is allowed to take it. Without this a retirement test would pass equally well
    /// against an agent that reported nothing at all: an empty ledger is also "not busy".
    /// </summary>
    private readonly TaskCompletionSource _drainGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Lets the run loop proceed to drain. Only meaningful with <see cref="DrainInputs"/> set.</summary>
    public void OpenDrainGate() => _drainGate.TrySetResult();

    /// <summary>
    /// How many inputs are sitting in the channel. Read directly rather than drained, so a test can
    /// prove an enqueue did NOT happen — the difference between a refused send and a silently
    /// accepted one is invisible from the receipt alone.
    /// </summary>
    public int QueuedInputCount => InputReader.Count;

    protected override async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!await InputReader.WaitToReadAsync(ct))
            {
                break;
            }

            if (!DrainInputs)
            {
                // Park WITHOUT reading: the input must stay in the channel, unstarted, because that
                // is the state a handoff has to be refused in. Ends when the entry is disposed.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                break;
            }

            await _drainGate.Task.WaitAsync(ct);

            if (!TryDrainInputs(out var batch) || batch.Count == 0)
            {
                continue;
            }

            var assignment = await StartRunAsync(batch, ct: ct);

            // What the pool's watcher retires on. StartRunAsync only RETURNS the assignment; every
            // product loop publishes it itself (MultiTurnAgentLoop, CopilotAgentLoop), so a stand-in
            // that skipped this would leave ids stranded until the grace expired - and would pin the
            // grace rather than the evidence.
            await PublishToAllAsync(new RunAssignmentMessage { Assignment = assignment, ThreadId = ThreadId }, ct);

            _ = RunStarted.TrySetResult();
            await CompleteRunAsync(assignment.RunId, assignment.GenerationId, false, null, 0, ct: ct);
        }
    }
}
