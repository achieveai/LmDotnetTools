using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace CodeReviewDaemon.Sample.Agents;

/// <summary>
/// The narrow sub-agent capability a review loop exposes to the stage executor: the completion source the
/// barrier polls, and the scope that suppresses NEW spawns while the synthesis turn runs. Both must come
/// from the SAME live instance the review is running on, which is why they are read off the loop rather
/// than resolved from a registry.
/// <para>
/// Declaring this interface is a POSITIVE statement — "this loop's spawn surface is exactly these two
/// members, and a <c>null</c> member means it genuinely has none". A loop that does NOT declare it is
/// UNKNOWN, not safe: the executor cannot tell whether it can spawn, so it fails fast rather than silently
/// skipping both the barrier and the suppression (which would let the synthesis turn run while children are
/// still writing, and let it fan out again afterwards).
/// </para>
/// </summary>
internal interface IReviewLoopSubAgentSurface
{
    /// <summary>The completion source for this loop's own children, or <c>null</c> when it has no
    /// in-process sub-agent manager (the barrier then falls back to the injected out-of-process source).</summary>
    IReviewSubAgentCompletionSource? CompletionSource { get; }

    /// <summary>Opens a scope in which the loop refuses to start NEW sub-agents, or <c>null</c> when there
    /// is no in-process spawn surface to suppress.</summary>
    Func<IDisposable>? SuppressSpawning { get; }
}

/// <summary>
/// A decorator that forwards <see cref="IMultiTurnAgent"/> to an inner loop. Implemented by
/// <see cref="ToolScopedReviewLoop"/> so capability resolution can see PAST the wrapper without knowing the
/// concrete wrapper type — a new decorator only has to implement this to keep the barrier working.
/// </summary>
internal interface IReviewLoopWrapper
{
    /// <summary>The wrapped loop.</summary>
    IMultiTurnAgent Inner { get; }
}

/// <summary>
/// Resolves the <see cref="IReviewLoopSubAgentSurface"/> of a review loop, unwrapping decorators on the way.
/// </summary>
internal static class ReviewLoopSubAgentSurface
{
    /// <summary>
    /// Returns <paramref name="agent"/>'s sub-agent surface, or <c>null</c> when the agent neither declares
    /// one nor is (or wraps) a live <see cref="MultiTurnAgentLoop"/>. <c>null</c> means UNKNOWN — the caller
    /// must decide whether the run was allowed to spawn, not assume it was not.
    /// </summary>
    public static IReviewLoopSubAgentSurface? Resolve(IMultiTurnAgent agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        return agent switch
        {
            IReviewLoopSubAgentSurface declared => declared,
            MultiTurnAgentLoop loop => new LiveLoopSurface(loop),
            IReviewLoopWrapper wrapper => Resolve(wrapper.Inner),
            _ => null,
        };
    }

    /// <summary>Adapts the SDK's live loop, which predates this interface, onto it.</summary>
    private sealed class LiveLoopSurface(MultiTurnAgentLoop loop) : IReviewLoopSubAgentSurface
    {
        private IReviewSubAgentCompletionSource? _completionSource;

        /// <summary>Cached: the barrier polls one source repeatedly, and a fresh adapter per read would
        /// discard nothing but is pure churn on a hot path.</summary>
        public IReviewSubAgentCompletionSource? CompletionSource =>
            _completionSource ??= loop.SubAgentManager is { } manager
                ? new InProcessReviewSubAgentCompletionSource(manager)
                : null;

        public Func<IDisposable>? SuppressSpawning =>
            loop.SubAgentTools is { } tools ? tools.SuppressSpawning : null;
    }
}
