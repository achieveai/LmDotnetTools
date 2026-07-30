using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Agents;

public sealed class ReviewLoopSubAgentSurfaceTests
{
    [Fact]
    public void A_wrapper_that_declares_an_empty_surface_cannot_mask_the_loop_it_wraps()
    {
        // Task 5 (fix round 2) — a decorator may declare the surface AND wrap another loop. Taking the
        // outermost declaration wholesale would let a decorator whose members are null hide a live loop's
        // real capabilities: the executor would see a declared (therefore trusted) surface, skip the
        // fail-fast, and then run neither the completion barrier nor the synthesis spawn suppression.
        // Resolution merges member by member instead: the wrapper's own non-null member wins, and what it
        // leaves null falls through to the loop underneath.
        var innerSource = new StubCompletionSource();
        var inner = new FakeMultiTurnAgent("run-1")
        {
            CompletionSource = innerSource,
            SuppressSpawning = () => new NoopScope(),
        };
        var ownSource = new StubCompletionSource();
        var wrapper = new SurfaceDeclaringWrapper(inner) { CompletionSource = ownSource };

        var surface = ReviewLoopSubAgentSurface.Resolve(wrapper);

        surface.Should().NotBeNull();
        surface!.CompletionSource.Should().BeSameAs(
            ownSource, "a wrapper that declares a capability of its own overrides the loop it wraps");
        surface.SuppressSpawning.Should().BeSameAs(
            inner.SuppressSpawning, "a capability the wrapper does NOT declare must not be lost");
    }

    /// <summary>
    /// Task 5 (fix round 3) — a wrapper that reports itself as its own inner loop must fail with a CATCHABLE
    /// exception. Unguarded recursion would raise StackOverflowException, which .NET does not let anyone
    /// catch: one malformed decorator would kill the daemon process instead of failing one review.
    /// </summary>
    [Fact]
    public void A_wrapper_that_wraps_itself_fails_instead_of_recursing()
    {
        var loop = new MutableWrappingLoop(new FakeMultiTurnAgent("run-1"));
        loop.Inner = loop;

        var act = () => ReviewLoopSubAgentSurface.Resolve(loop);

        act.Should().Throw<InvalidOperationException>().WithMessage("*its own inner loop*");
    }

    /// <summary>
    /// The same hazard one step removed: two decorators that wrap each other. No single hop looks wrong, so
    /// only a depth bound catches it.
    /// </summary>
    [Fact]
    public void Mutually_wrapping_decorators_fail_instead_of_recursing()
    {
        var first = new MutableWrappingLoop(new FakeMultiTurnAgent("run-1"));
        var second = new MutableWrappingLoop(new FakeMultiTurnAgent("run-2"));
        first.Inner = second;
        second.Inner = first;

        var act = () => ReviewLoopSubAgentSurface.Resolve(first);

        act.Should().Throw<InvalidOperationException>().WithMessage("*exceeded*levels*");
    }

    private sealed class NoopScope : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class StubCompletionSource : IReviewSubAgentCompletionSource
    {
        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run, string parentThreadId, CancellationToken ct) =>
            Task.FromResult(new ReviewSubAgentTreeSnapshot([]));
    }
}
