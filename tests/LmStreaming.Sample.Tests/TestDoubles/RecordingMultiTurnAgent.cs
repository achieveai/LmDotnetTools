using System.Runtime.CompilerServices;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// An <see cref="IMultiTurnAgent"/> test double that records every message passed to
/// <see cref="SendAsync"/>, so tests can assert exactly what the context-discovery injector (and
/// the chain above it) enqueued onto the thread. <see cref="ThrowOnSend"/> simulates a thread
/// whose send fails, to exercise per-thread error isolation.
/// </summary>
internal sealed class RecordingMultiTurnAgent : IMultiTurnAgent, IAcceptanceReportingAgent
{
    private readonly List<IMessage> _sent = [];
    private readonly Lock _lock = new();

    public RecordingMultiTurnAgent(string threadId)
    {
        ThreadId = threadId;
    }

    public string ThreadId { get; }

    public string? CurrentRunId { get; set; }

    public bool IsRunning { get; set; } = true;

    public bool ThrowOnSend { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Since #442 the pool refuses to pool an agent that is not
    /// <see cref="IAcceptanceReportingAgent"/>, because the accepted-input ledger has no other
    /// source. This double therefore reports like the product does: from the place the receipt id is
    /// minted, BEFORE the input is taken.
    /// </remarks>
    public IInputAcceptanceObserver? InputAcceptanceObserver { get; set; }

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

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    )
    {
        // Before the report, standing in for every way a real send fails ahead of minting an
        // acceptance (a disposed agent, a failed durable accepted-input write): nothing is announced,
        // so nothing has to be withdrawn.
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("send failed");
        }

        var receiptId = inputId ?? Guid.NewGuid().ToString("N");
        if (InputAcceptanceObserver?.OnInputAccepted(ThreadId, receiptId, this) == false)
        {
            // Honoured, not ignored: the product refuses the enqueue when the observer says this
            // agent is no longer the conversation's, and a double that queued anyway would let a
            // host test pass over the silent loss (#442).
            throw new InputAcceptanceRefusedException(ThreadId, receiptId);
        }

        lock (_lock)
        {
            _sent.AddRange(messages);
        }

        return ValueTask.FromResult(new SendReceipt(receiptId, inputId, DateTimeOffset.UtcNow));
    }

    public async ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    )
    {
        return await SendAsync(messages, inputId, parentRunId, ct);
    }

#pragma warning disable CS1998, IDE0391
    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        _ = userInput;
        _ = ct;
        yield break;
    }

    public async IAsyncEnumerable<IMessage> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
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
