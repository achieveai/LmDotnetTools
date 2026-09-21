using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Agents;

/// <summary>
///     The ambient actor a conversation's shared tools read to tell the root agent from a sub-agent
///     (bug 19). What matters is the flow discipline: the value must reach a run loop started inside a
///     scope, must not reach anything started outside one, and concurrent runs must not see each
///     other's actor — otherwise the todo board's root-only clear guard either refuses the root or
///     permits the sub-agent.
/// </summary>
public class AgentActorScopeTests
{
    [Fact]
    public void NoScope_ReadsAsTheRootAgent()
    {
        AgentActorScope.Current.Should().BeNull();
    }

    [Fact]
    public void Begin_CarriesBothTheIdentifierAndTheDisplayName()
    {
        using var scope = AgentActorScope.Begin("agent-7", "researcher");

        AgentActorScope.Current!.AgentId.Should().Be("agent-7");
        AgentActorScope.Current.DisplayName.Should().Be("researcher");
    }

    [Fact]
    public void NestedScope_RestoresToItsParent_NotToTheRoot()
    {
        // A sub-agent that spawns a sub-agent. Mutation that must go red: clearing to null on dispose.
        using var outer = AgentActorScope.Begin("agent-1");

        using (AgentActorScope.Begin("agent-2"))
        {
            AgentActorScope.Current!.AgentId.Should().Be("agent-2");
        }

        AgentActorScope.Current!.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public async Task Value_FlowsIntoAnAsyncRunStartedInsideTheScope()
    {
        // This is exactly how SubAgentManager uses it: the scope wraps the awaited run loop, and the
        // tool call happens several awaits deep inside it.
        string? seenDeepInside = null;

        static async Task<string?> RunLoopAsync()
        {
            await Task.Yield();
            await Task.Delay(1);
            return AgentActorScope.Current?.AgentId;
        }

        using (AgentActorScope.Begin("agent-7"))
        {
            seenDeepInside = await RunLoopAsync();
        }

        seenDeepInside.Should().Be("agent-7");
    }

    [Fact]
    public async Task Value_DoesNotLeakIntoWorkStartedAfterTheScopeClosed()
    {
        using (AgentActorScope.Begin("agent-7"))
        {
            await Task.Yield();
        }

        var after = await Task.Run(() => AgentActorScope.Current?.AgentId);
        after.Should().BeNull();
    }

    [Fact]
    public async Task ConcurrentRuns_EachSeeTheirOwnActor()
    {
        // Per async flow, not per thread: two sub-agent loops sharing the pool must not read each
        // other's identity, or the board would refuse or permit the wrong caller.
        static async Task<string?> RunAsAsync(string agentId)
        {
            using var scope = AgentActorScope.Begin(agentId);
            await Task.Delay(5);
            return AgentActorScope.Current?.AgentId;
        }

        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => Task.Run(() => RunAsAsync($"agent-{i}"))));

        results.Should().Equal(Enumerable.Range(1, 8).Select(i => $"agent-{i}"));
        AgentActorScope.Current.Should().BeNull();
    }

    [Fact]
    public void Begin_RejectsABlankIdentifier()
    {
        // A blank actor would read as "some sub-agent" with nothing to name in the refusal.
        var begin = () => AgentActorScope.Begin("  ");

        begin.Should().Throw<ArgumentException>();
    }
}
