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
/// Resolves the <see cref="IReviewLoopSubAgentSurface"/> of a review loop — and, through
/// <see cref="ReviewLoopSubAgentSurface.ResolveCapability{T}"/>, any other capability a loop in the same
/// chain declares — unwrapping decorators on the way.
/// </summary>
internal static class ReviewLoopSubAgentSurface
{
    /// <summary>
    /// Returns <paramref name="agent"/>'s sub-agent surface, or <c>null</c> when the agent neither declares
    /// one nor is (or wraps) a live <see cref="MultiTurnAgentLoop"/>. <c>null</c> means UNKNOWN — the caller
    /// must decide whether the run was allowed to spawn, not assume it was not.
    /// <para>
    /// A decorator that BOTH declares the interface and wraps another loop is merged member-wise rather than
    /// short-circuited: its own non-null members win (it is entitled to override), but a member it leaves
    /// null falls through to what it wraps. Preferring the outer surface wholesale would let a decorator with
    /// two null members MASK a live loop underneath — silently skipping the barrier and the suppression while
    /// still looking like a declared, and therefore trusted, surface.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The decorator chain is cyclic or nested past <see cref="MaxWrapperDepth"/>. A wrapper that returns
    /// itself (or two that return each other) would otherwise recurse until the stack overflows, and a
    /// StackOverflowException cannot be caught — it kills the daemon process instead of failing this one
    /// review. Thrown so the executor's existing fail-fast handles it like any other unusable surface.
    /// </exception>
    public static IReviewLoopSubAgentSurface? Resolve(IMultiTurnAgent agent) => Resolve(agent, depth: 0);

    /// <summary>How deep a decorator chain may nest before it is treated as malformed.</summary>
    private const int MaxWrapperDepth = 32;

    /// <summary>
    /// Returns the first loop in <paramref name="agent"/>'s decorator chain that implements
    /// <typeparamref name="T"/>, or <c>null</c> when none does.
    /// <para>
    /// Used for capabilities the executor PROBES for rather than merges — resumable-turn checkpointing today.
    /// Resolving through the chain, instead of having each decorator re-declare the interface and forward, is
    /// what keeps the probe honest: a wrapper that declared the capability would answer "yes" even when the
    /// loop underneath cannot supply it, turning a fail-fast into a silently non-resumable review. The
    /// converse — a wrapper that simply forgets to forward — is the reason this is not a plain cast.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The decorator chain is cyclic or nested past <see cref="MaxWrapperDepth"/> (see
    /// <see cref="Resolve(IMultiTurnAgent)"/>).
    /// </exception>
    public static T? ResolveCapability<T>(IMultiTurnAgent agent)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(agent);

        var current = agent;
        for (var depth = 0; depth <= MaxWrapperDepth; depth++)
        {
            if (current is T capability)
            {
                return capability;
            }

            if (current is not IReviewLoopWrapper wrapper)
            {
                return null;
            }

            if (ReferenceEquals(wrapper.Inner, current))
            {
                throw new InvalidOperationException(
                    $"Review loop wrapper '{current.GetType().Name}' reports itself as its own inner loop, "
                        + $"so its '{typeof(T).Name}' capability cannot be resolved.");
            }

            current = wrapper.Inner;
        }

        throw new InvalidOperationException(
            $"Review loop decorator chain exceeded {MaxWrapperDepth} levels while resolving the "
                + $"'{typeof(T).Name}' capability of '{agent.GetType().Name}'; the wrappers are probably cyclic.");
    }

    private static IReviewLoopSubAgentSurface? Resolve(IMultiTurnAgent agent, int depth)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (depth > MaxWrapperDepth)
        {
            throw new InvalidOperationException(
                $"Review loop decorator chain exceeded {MaxWrapperDepth} levels while resolving the "
                    + $"sub-agent surface of '{agent.GetType().Name}'; the wrappers are probably cyclic.");
        }

        var declared = agent switch
        {
            IReviewLoopSubAgentSurface surface => surface,
            MultiTurnAgentLoop loop => new LiveLoopSurface(loop),
            _ => null,
        };

        IReviewLoopSubAgentSurface? wrapped = null;
        if (agent is IReviewLoopWrapper wrapper)
        {
            // A wrapper handing back the very agent it decorates is the tightest cycle and the easiest one
            // to write by accident, so it is named directly rather than left to the depth limit.
            if (ReferenceEquals(wrapper.Inner, agent))
            {
                throw new InvalidOperationException(
                    $"Review loop wrapper '{agent.GetType().Name}' reports itself as its own inner loop, "
                        + "so its sub-agent surface cannot be resolved.");
            }

            wrapped = Resolve(wrapper.Inner, depth + 1);
        }

        return (declared, wrapped) switch
        {
            (null, _) => wrapped,
            (_, null) => declared,
            _ => new MergedSurface(declared, wrapped),
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

    /// <summary>A decorator's own surface laid over the surface of what it wraps, member by member.</summary>
    private sealed class MergedSurface(
        IReviewLoopSubAgentSurface outer,
        IReviewLoopSubAgentSurface inner) : IReviewLoopSubAgentSurface
    {
        public IReviewSubAgentCompletionSource? CompletionSource =>
            outer.CompletionSource ?? inner.CompletionSource;

        public Func<IDisposable>? SuppressSpawning => outer.SuppressSpawning ?? inner.SuppressSpawning;
    }
}
