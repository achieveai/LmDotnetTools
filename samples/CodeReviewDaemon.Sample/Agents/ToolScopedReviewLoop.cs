using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using ModelContextProtocol.Client;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// Wraps a <see cref="MultiTurnAgentLoop"/> so the MCP clients opened for its sandbox tools are disposed
/// together with the loop. The loop does not own external clients, so without this each tool-assisted run
/// would leak one MCP connection.
/// <para>
/// It is a TRANSPARENT decorator: everything the review lifecycle needs from the loop underneath — the
/// sub-agent surface (via <see cref="IReviewLoopWrapper"/>) and the shared absolute deadline (via
/// <see cref="IDeadlineBoundedReviewLoop"/>) — is forwarded, so wrapping a loop can never silently drop a
/// capability the executor depends on.
/// </para>
/// </summary>
internal sealed class ToolScopedReviewLoop(IMultiTurnAgent inner, IReadOnlyList<McpClient> ownedClients)
    : IMultiTurnAgent, IReviewLoopWrapper, IDeadlineBoundedReviewLoop
{
    /// <summary>
    /// The wrapped loop. Exposed so the executor — which holds only the <see cref="IMultiTurnAgent"/> this
    /// factory returned — can still reach the live <see cref="MultiTurnAgentLoop"/> underneath for the two
    /// things only it can supply: the <c>SubAgentManager</c> the completion barrier polls, and the spawn
    /// suppression scope the synthesis turn runs in. Both must come from the SAME instance the review is
    /// running on, which is exactly what this pass-through preserves.
    /// </summary>
    public IMultiTurnAgent Inner => inner;

    /// <summary>
    /// Forwards the review attempt's ONE absolute budget to a deadline-bounded inner loop. A no-op when the
    /// inner loop finishes when the provider finishes (the in-process path), but wiring it unconditionally is
    /// what stops a future wrapping of a deadline-bounded loop from silently restarting the budget per turn.
    /// </summary>
    public void UseDeadline(DateTimeOffset deadlineUtc) =>
        (inner as IDeadlineBoundedReviewLoop)?.UseDeadline(deadlineUtc);

    public string? CurrentRunId => inner.CurrentRunId;
    public string ThreadId => inner.ThreadId;
    public bool IsRunning => inner.IsRunning;

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages, string? inputId = null, string? parentRunId = null, CancellationToken ct = default)
        => inner.SendAsync(messages, inputId, parentRunId, ct);

    public ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages, string? inputId = null, string? parentRunId = null, CancellationToken ct = default)
        => inner.TrySendAsync(messages, inputId, parentRunId, ct);

    public IAsyncEnumerable<IMessage> ExecuteRunAsync(UserInput userInput, CancellationToken ct = default)
        => inner.ExecuteRunAsync(userInput, ct);

    public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default)
        => inner.SubscribeAsync(ct);

    public Task RunAsync(CancellationToken ct = default) => inner.RunAsync(ct);

    public Task StopAsync(TimeSpan? timeout = null) => inner.StopAsync(timeout);

    public async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        foreach (var client in ownedClients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
