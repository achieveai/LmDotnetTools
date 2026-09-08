using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

public class AgentCompatibilityTests
{
    [Fact]
    public async Task LegacyAgent_TrySendAsyncThrowsWithoutCallingSendAsync()
    {
        await using var legacy = new LegacyAgent();
        IMultiTurnAgent agent = legacy;
        Func<Task> act = async () => await agent.TrySendAsync([]);

        await act.Should().ThrowAsync<NotSupportedException>();
        legacy.SendCount.Should().Be(0);
    }

    // Intentionally implements only the original agent surface: new optional capabilities
    // must not require consumers to change every existing fake or custom implementation.
    private sealed class LegacyAgent : IMultiTurnAgent
    {
        public int SendCount { get; private set; }
        public string? CurrentRunId => null;
        public string ThreadId => "legacy";
        public bool IsRunning => false;

        public ValueTask<SendReceipt> SendAsync(
            List<IMessage> messages,
            string? inputId = null,
            string? parentRunId = null,
            CancellationToken ct = default
        )
        {
            SendCount++;
            throw new NotSupportedException();
        }

        public IAsyncEnumerable<IMessage> ExecuteRunAsync(UserInput userInput, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<IMessage> SubscribeAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RunAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task StopAsync(TimeSpan? timeout = null) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
