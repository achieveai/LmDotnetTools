using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.DualLayer;

/// <summary>
/// The executor loop is a long-lived background task. A planner call is served by a run on that
/// loop, so when the loop itself ends, nothing will ever complete the call: it must fail then, not
/// wait for a run that cannot come.
/// </summary>
public class AgentDelegatedToolExecutorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly DelegatedToolCall Call = new("read_file", """{"path":"a.md"}""", "why", "p1");

    /// <summary>A loop whose run is <paramref name="run"/> and whose runs never complete.</summary>
    private static IMultiTurnAgent LoopWhoseRunIs(Task run)
    {
        var mock = new Mock<IMultiTurnAgent>();
        _ = mock.Setup(a => a.ThreadId).Returns("thread-1::executor");
        _ = mock.Setup(a => a.RunAsync(It.IsAny<CancellationToken>())).Returns(run);
        _ = mock.Setup(a => a.ExecuteRunAsync(It.IsAny<UserInput>(), It.IsAny<CancellationToken>()))
            .Returns((UserInput _, CancellationToken ct) => NeverCompletes(ct));
        return mock.Object;
    }

    private static async IAsyncEnumerable<IMessage> NeverCompletes([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    [Fact]
    public async Task A_call_in_flight_fails_when_the_executor_loop_dies_under_it()
    {
        var loopRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var executor = new AgentDelegatedToolExecutor(LoopWhoseRunIs(loopRun.Task));
        using var cts = new CancellationTokenSource(Timeout);

        var call = executor.ExecuteAsync(Call, cts.Token);
        loopRun.SetException(new InvalidOperationException("provider client closed"));

        var pending = () => call;
        (await pending.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("the executor loop stopped before the call completed")
            .WithInnerException<InvalidOperationException>()
            .WithMessage("provider client closed");
    }

    [Fact]
    public async Task A_call_after_the_loop_has_died_fails_at_once_with_the_loops_own_failure()
    {
        await using var executor = new AgentDelegatedToolExecutor(
            LoopWhoseRunIs(Task.FromException(new InvalidOperationException("never started")))
        );
        using var cts = new CancellationTokenSource(Timeout);

        // The first call may observe the death either before or during its wait; both end it.
        var first = () => executor.ExecuteAsync(Call, cts.Token);
        (await first.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("the executor loop*")
            .WithInnerException<InvalidOperationException>()
            .WithMessage("never started");

        // From then on the loop is known to be gone, and no call waits on the gate for it.
        var next = () => executor.ExecuteAsync(Call, cts.Token);
        (await next.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("the executor loop is not running")
            .WithInnerException<InvalidOperationException>()
            .WithMessage("never started");
    }
}
