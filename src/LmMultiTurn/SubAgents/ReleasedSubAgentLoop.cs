using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Stands in for a finished sub-agent's loop after an idle release (#830), so the manager keeps the
/// sub-agent registered (its status, result, name and thread id) without keeping the loop's memory.
/// Sends and subscriptions fail with <see cref="ObjectDisposedException"/>, as they do on a disposed
/// loop, and stopping or disposing it does nothing. The next message to the sub-agent rebuilds a real
/// loop from its transcript (see <see cref="SubAgentManager.ReleaseFinishedAgentsAsync"/>).
/// </summary>
internal sealed class ReleasedSubAgentLoop(string threadId) : IMultiTurnAgent
{
    public string? CurrentRunId => null;

    public string ThreadId { get; } = threadId;

    public bool IsRunning => false;

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    ) => throw Released();

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default
    ) => throw Released();

    public IAsyncEnumerable<IMessage> ExecuteRunAsync(UserInput userInput, CancellationToken ct = default) =>
        FailOnFirstMoveAsync();

    public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default) => FailOnFirstMoveAsync();

    public IAsyncEnumerable<IMessage> SubscribeAsync(SubscribeOptions options, CancellationToken ct = default) =>
        FailOnFirstMoveAsync();

    public Task RunAsync(CancellationToken ct = default) => Task.FromException(Released());

    public Task StopAsync(TimeSpan? timeout = null) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private ObjectDisposedException Released() =>
        new(nameof(ReleasedSubAgentLoop), $"Sub-agent thread '{ThreadId}' was released while idle.");

    // Lazy, like the loop's own iterators: a subscriber learns at its first MoveNextAsync, which is where
    // the manager's observation seam already treats a disposed instance as the end of its stream.
    private async IAsyncEnumerable<IMessage> FailOnFirstMoveAsync()
    {
        await Task.FromException(Released());
        yield break;
    }
}
