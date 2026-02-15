namespace LmStreaming.Sample.Tests.TestDoubles;

internal sealed class FakeMultiTurnAgent : IMultiTurnAgent
{
    public FakeMultiTurnAgent(string threadId)
    {
        ThreadId = threadId;
    }

    public string? CurrentRunId { get; set; }

    public string ThreadId { get; }

    public bool IsRunning { get; set; } = true;

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        _ = messages;
        _ = parentRunId;
        _ = ct;

        var receiptId = inputId ?? Guid.NewGuid().ToString("N");
        return ValueTask.FromResult(new SendReceipt(receiptId, inputId, DateTimeOffset.UtcNow));
    }

#pragma warning disable CS1998 // Async method lacks 'await' - intentional stub using yield break
    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = userInput;
        _ = ct;
        yield break;
    }

    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = ct;
        yield break;
    }
#pragma warning restore CS1998

    public Task RunAsync(CancellationToken ct = default)
    {
        _ = ct;
        return Task.CompletedTask;
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
