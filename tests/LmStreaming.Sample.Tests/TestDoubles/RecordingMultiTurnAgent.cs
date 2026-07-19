using System.Runtime.CompilerServices;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// An <see cref="IMultiTurnAgent"/> test double that records every message passed to
/// <see cref="SendAsync"/>, so tests can assert exactly what the context-discovery injector (and
/// the chain above it) enqueued onto the thread. <see cref="ThrowOnSend"/> simulates a thread
/// whose send fails, to exercise per-thread error isolation.
/// </summary>
internal sealed class RecordingMultiTurnAgent : IMultiTurnAgent
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
        CancellationToken ct = default)
    {
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("send failed");
        }

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
