namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A REAL agent with a publish hook, for tests that need something to subscribe to.
/// </summary>
/// <remarks>
/// It inherits the production fan-out, so <see cref="MultiTurnAgentBase.SubscribeAsync"/>, the bounded
/// per-subscriber channel and the drop-the-slow-subscriber path are all the shipping implementations. A
/// hand-written fake that ended its enumeration on command would prove the fake works, not that a
/// subscriber survives the real drop.
/// </remarks>
/// <param name="threadId">The conversation the agent belongs to.</param>
/// <param name="outputChannelCapacity">Per-subscriber channel capacity; lower it to force a drop.</param>
internal sealed class PublishingAgent(string threadId, int outputChannelCapacity = 1000)
    : MultiTurnAgentBase(threadId, outputChannelCapacity: outputChannelCapacity)
{
    public ValueTask PublishAsync(IMessage message) => PublishToAllAsync(message, CancellationToken.None);

    protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;
}
