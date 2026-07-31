using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;

namespace CodeReviewDaemon.Sample.Tests.Agents;

/// <summary>
/// <see cref="ToolScopedReviewLoop"/> exists to own the MCP clients opened for a tool-assisted run, but the
/// executor only ever sees the wrapper — so everything the review lifecycle depends on must survive the
/// wrapping. These tests pin the two capabilities that would fail SILENTLY if it did not forward them: the
/// sub-agent surface the completion barrier and the synthesis-turn spawn suppression are read from, and the
/// ONE absolute deadline collect/barrier/synthesis share.
/// </summary>
public sealed class ToolScopedReviewLoopTests
{
    [Fact]
    public void UseDeadline_reaches_a_deadline_bounded_inner_loop()
    {
        // ReviewAgent pushes the shared deadline with `(_agent as IDeadlineBoundedReviewLoop)?.UseDeadline`,
        // so a wrapper that does not implement the interface drops it SILENTLY — collect, barrier and
        // synthesis would each get a fresh budget. Go through the same cast the executor's agent does.
        var inner = new FakeMultiTurnAgent("run-1");
        var sut = new ToolScopedReviewLoop(inner, []);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);

        sut.Should().BeAssignableTo<IDeadlineBoundedReviewLoop>().Which.UseDeadline(deadline);

        inner.Deadlines.Should().Equal(
            [deadline], "wrapping a deadline-bounded loop must not restart its budget per turn");
    }

    [Fact]
    public void The_sub_agent_surface_resolves_through_the_wrapper()
    {
        // The executor holds only what the loop factory returned. If resolution stopped at the wrapper it
        // would see NO surface and skip both the barrier and the suppression.
        var inner = new FakeMultiTurnAgent("run-1") { SuppressSpawning = () => new NoopScope() };
        var sut = new ToolScopedReviewLoop(inner, []);

        var surface = ReviewLoopSubAgentSurface.Resolve(sut);

        surface.Should().BeSameAs(inner);
        surface!.SuppressSpawning.Should().NotBeNull();
    }

    private sealed class NoopScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
