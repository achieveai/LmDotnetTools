using AchieveAi.LmDotnetTools.LmMultiTurn;
using Xunit;

namespace LmMultiTurn.Tests;

public class MultiTurnAgentStartupReadinessTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Readiness_waits_for_startup_and_resets_on_restart()
    {
        await using var agent = new GatedAgent("readiness-restart");
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.WaitUntilReadyAsync());

        var first = agent.RunAsync();
        await agent.StartupEntered.WaitAsync(Bound);
        Assert.False(agent.WaitUntilReadyAsync().IsCompleted);
        agent.ReleaseStartup();
        await agent.WaitUntilReadyAsync().WaitAsync(Bound);
        await agent.StopAsync(Bound);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        agent.ResetStartup();
        var second = agent.RunAsync();
        await agent.StartupEntered.WaitAsync(Bound);
        Assert.False(agent.WaitUntilReadyAsync().IsCompleted);
        agent.ReleaseStartup();
        await agent.WaitUntilReadyAsync().WaitAsync(Bound);
        await agent.StopAsync(Bound);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task Readiness_reports_failure_from_startup_hook()
    {
        var agent = new GatedAgent("readiness-fault");
        var run = agent.RunAsync();
        await agent.StartupEntered.WaitAsync(Bound);
        agent.FailStartup(new InvalidOperationException("injected startup failure"));

        var readinessFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.WaitUntilReadyAsync().WaitAsync(Bound)
        );
        Assert.Equal("injected startup failure", readinessFailure.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);

        // The base StopAsync currently relays a failed run; disposal of this
        // deliberately faulted instance can therefore report the same failure.
        try
        {
            await agent.DisposeAsync();
        }
        catch (InvalidOperationException) { }
    }

    [Fact]
    public async Task Readiness_rejects_loop_that_exits_during_startup()
    {
        await using var agent = new GatedAgent("readiness-short-loop", exitImmediately: true);
        var run = agent.RunAsync();
        await agent.StartupEntered.WaitAsync(Bound);
        agent.ReleaseStartup();

        await run.WaitAsync(Bound);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            agent.WaitUntilReadyAsync().WaitAsync(Bound)
        );
        Assert.Contains("exited before startup", error.Message);
    }

    private sealed class GatedAgent(string threadId, bool exitImmediately = false) : MultiTurnAgentBase(threadId)
    {
        private TaskCompletionSource _entered = NewSignal();
        private TaskCompletionSource _release = NewSignal();

        public Task StartupEntered => _entered.Task;

        public void ReleaseStartup() => _release.TrySetResult();

        public void FailStartup(Exception error) => _release.TrySetException(error);

        public void ResetStartup()
        {
            _entered = NewSignal();
            _release = NewSignal();
        }

        protected override async Task OnBeforeRunAsync()
        {
            _entered.TrySetResult();
            await _release.Task;
        }

        protected override Task RunLoopAsync(CancellationToken ct) =>
            exitImmediately ? Task.CompletedTask : Task.Delay(Timeout.InfiniteTimeSpan, ct);

        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
