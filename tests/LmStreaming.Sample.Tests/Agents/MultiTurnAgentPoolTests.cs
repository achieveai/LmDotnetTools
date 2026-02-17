using LmStreaming.Sample.Tests.TestDoubles;

namespace LmStreaming.Sample.Tests.Agents;

public class MultiTurnAgentPoolTests
{
    [Fact]
    public async Task IsRunInProgress_ReturnsFalse_WhenAgentDoesNotExist()
    {
        await using var pool = CreatePool();
        pool.IsRunInProgress("missing-thread").Should().BeFalse();
    }

    [Fact]
    public async Task IsRunInProgress_ReturnsTrue_WhenCurrentRunIdIsSet()
    {
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent("thread-1", mode);
        agent.CurrentRunId = "run_123";
        agent.IsRunning = true;

        pool.IsRunInProgress("thread-1").Should().BeTrue();
    }

    [Fact]
    public async Task IsRunInProgress_ReturnsFalse_WhenCurrentRunIdIsNull()
    {
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent("thread-2", mode);
        agent.CurrentRunId = null;

        pool.IsRunInProgress("thread-2").Should().BeFalse();
    }

    [Fact]
    public async Task IsRunInProgress_ReturnsFalse_WhenRunStateIsStale()
    {
        await using var pool = CreatePool();
        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = (FakeMultiTurnAgent)pool.GetOrCreateAgent("thread-stale", mode);
        agent.CurrentRunId = "run_stale";
        agent.IsRunning = false;

        pool.IsRunInProgress("thread-stale").Should().BeFalse();

        var state = pool.GetRunStateInfo("thread-stale");
        state.IsStale.Should().BeTrue();
        state.CurrentRunId.Should().Be("run_stale");
    }

    private static MultiTurnAgentPool CreatePool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) => new FakeMultiTurnAgent(threadId),
            NullLogger<MultiTurnAgentPool>.Instance);
    }
}
