using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Tests for the one thing <see cref="MultiTurnAgentLoop"/> itself owns in a collaboration:
/// publishing the agent it runs, and being reachable afterwards.
/// </summary>
public class MultiTurnAgentLoopCollaborationTests
{
    private readonly Mock<IStreamingAgent> _providerMock = new();

    [Fact]
    public async Task ALoopWithoutACollaboration_PublishesNothing()
    {
        await using var loop = CreateLoop(collaboration: null);

        loop.Collaboration.Should().BeNull();
    }

    [Fact]
    public async Task ALoopWithACollaboration_PublishesTheAgentItRuns()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());

        await using var loop = CreateLoop(setup);

        loop.Collaboration.Should().BeSameAs(setup);
        var entry = setup.Directory.FindById(setup.AgentId);
        entry.Should().NotBeNull();
        entry!.Name.Should().Be(setup.Name);
        entry.Status.Should().Be(AgentCollaborationStatuses.Running);
        entry.Kind.Should().Be(AgentKind.Root);
    }

    [Fact]
    public async Task ALoopForAnAlreadyPublishedAgent_LeavesTheExistingEntryAlone()
    {
        // A spawned sub-agent is registered by its parent's manager, which had to know the child's
        // endpoint before the child's loop existed. Re-registering here would either fail or, worse,
        // replace a working endpoint with one pointing at a loop nobody drives.
        var root = CreateRegisteredRoot();
        var childContext = root.Context.CreateChild(
            "agent-child", AgentKind.SubAgent, "worker", "Already published by its parent.");
        var existing = new NoopEndpoint();

        _ = root.Directory.TryAcquireCapacity(childContext.AgentId);
        _ = root.Directory.TryRegister(
            childContext, "child", AgentCollaborationStatuses.Queued, existing);

        await using var loop = CreateLoop(root.ForChild(childContext, "child"));

        root.Directory.Count.Should().Be(2);
        root.Directory.GetWriteEndpoint(childContext.AgentId).Should().BeSameAs(existing);
        root.Directory.FindById(childContext.AgentId)!.Status
            .Should().Be(AgentCollaborationStatuses.Queued);
    }

    [Fact]
    public async Task APublishedLoop_IsReachableThroughTheDirectoryItRegisteredWith()
    {
        // Registration is only worth anything if the endpoint it published actually delivers, so this
        // asserts the round trip rather than the presence of a row.
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        await using var loop = CreateLoop(setup);
        var (_, peer) = RegisterPeer(setup, "asker");

        var dispatch = new AgentCollaborationMessenger(peer).Send(
            setup.AgentId, "Are you there?", AgentMessageType.Question);
        dispatch.Result.Succeeded.Should().BeTrue();
        await dispatch.Delivery.WaitAsync(TimeSpan.FromSeconds(10));

        setup.Bundle.Ledger.Find(dispatch.Result.MessageId!)!.State
            .Should().Be(AgentMessageDeliveryState.Delivered);
    }

    private MultiTurnAgentLoop CreateLoop(AgentCollaborationSetup? collaboration) =>
        new(
            _providerMock.Object,
            new FunctionRegistry(),
            "test-thread",
            collaboration: collaboration);

    private static AgentCollaborationSetup CreateRegisteredRoot()
    {
        var setup = AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions());
        _ = setup.Directory.TryRegister(
            setup.Context, setup.Name, AgentCollaborationStatuses.Running);
        return setup;
    }

    private static (NoopEndpoint Endpoint, AgentCollaborationSetup Setup) RegisterPeer(
        AgentCollaborationSetup root,
        string name)
    {
        var context = root.Context.CreateChild(
            $"agent-{name}", AgentKind.SubAgent, $"{name} role", $"Stands in for {name}.");
        var endpoint = new NoopEndpoint();

        _ = root.Directory.TryAcquireCapacity(context.AgentId);
        _ = root.Directory.TryRegister(
            context, name, AgentCollaborationStatuses.Running, endpoint);

        return (endpoint, root.ForChild(context, name));
    }

    /// <summary>An endpoint that exists only to be identified, never to be exercised.</summary>
    private sealed class NoopEndpoint : IAgentWriteEndpoint
    {
        public ValueTask<AgentDeliveryOutcome> DeliverAsync(
            AgentMessage message,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered));
    }
}
