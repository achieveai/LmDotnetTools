using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using CodeReviewDaemon.Sample.Agents;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// Forwards every <see cref="IMultiTurnAgent"/> member to an inner loop, modelling the live path's decorator
/// (<c>ToolScopedReviewLoop</c>) without its MCP-client ownership. The subclasses below differ ONLY in which
/// capability interfaces they declare, which is exactly what sub-agent surface resolution keys off.
/// </summary>
internal abstract class DelegatingLoop(IMultiTurnAgent inner) : IMultiTurnAgent
{
    protected IMultiTurnAgent Wrapped => inner;

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

    public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default) => inner.SubscribeAsync(ct);

    public Task RunAsync(CancellationToken ct = default) => inner.RunAsync(ct);

    public Task StopAsync(TimeSpan? timeout = null) => inner.StopAsync(timeout);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>A decorator that DECLARES what it wraps, so the surface resolves through it.</summary>
internal sealed class WrappingLoop(IMultiTurnAgent inner) : DelegatingLoop(inner), IReviewLoopWrapper
{
    public IMultiTurnAgent Inner => Wrapped;
}

/// <summary>A decorator that declares NOTHING — the executor cannot tell whether it can spawn.</summary>
internal sealed class OpaqueLoop(IMultiTurnAgent inner) : DelegatingLoop(inner);

/// <summary>
/// A decorator whose <see cref="Inner"/> can be re-pointed AFTER construction, so a test can tie the knot a
/// real decorator only produces by accident: a wrapper that reports ITSELF as the loop it wraps, or a pair
/// that report each other. Surface resolution must reject those with a catchable exception — an unguarded
/// recursion would raise StackOverflowException, which cannot be caught and takes the daemon down with it.
/// </summary>
internal sealed class MutableWrappingLoop(IMultiTurnAgent inner) : DelegatingLoop(inner), IReviewLoopWrapper
{
    public IMultiTurnAgent Inner { get; set; } = inner;
}

/// <summary>
/// A decorator that declares BOTH interfaces — the shape that would let an outer surface mask the loop it
/// wraps if resolution short-circuited on the first declaration instead of merging member by member.
/// Its own capabilities default to null ("I add nothing of my own").
/// </summary>
internal sealed class SurfaceDeclaringWrapper(IMultiTurnAgent inner)
    : DelegatingLoop(inner), IReviewLoopWrapper, IReviewLoopSubAgentSurface
{
    public IMultiTurnAgent Inner => Wrapped;

    public IReviewSubAgentCompletionSource? CompletionSource { get; set; }

    public Func<IDisposable>? SuppressSpawning { get; set; }
}
