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
