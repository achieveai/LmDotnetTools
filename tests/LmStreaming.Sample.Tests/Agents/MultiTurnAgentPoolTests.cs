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

    private static MultiTurnAgentPool CreatePool()
    {
        return new MultiTurnAgentPool(
            (threadId, _, _) => new FakeMultiTurnAgent(threadId),
            NullLogger<MultiTurnAgentPool>.Instance);
    }
}
