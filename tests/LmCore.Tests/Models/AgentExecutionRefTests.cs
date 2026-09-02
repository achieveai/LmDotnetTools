using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;

/// <summary>
///     Pins the attribution key every per-agent rollup groups by (#681; spec 679 §4.3, decision Q5): the
///     execution id of a usage record is the emitting execution, which the ledger encodes as
///     <see cref="UsageRecord.ParentExecutionId" /> for everything but the root itself.
/// </summary>
public class AgentExecutionRefTests
{
    private static UsageRecord Record(string root, string? parentExecutionId, UsageExecutionKind kind) =>
        new()
        {
            LogicalCallId = "a",
            ProviderAttemptId = "a",
            RootConversationId = root,
            ParentExecutionId = parentExecutionId,
            ExecutionKind = kind,
            RequestedModel = "m",
        };

    [Fact]
    public void ExecutionIdOf_RootRecord_IsTheRootConversation()
    {
        AgentExecutionRef.ExecutionIdOf(Record("root-1", null, UsageExecutionKind.Primary)).Should().Be("root-1");
    }

    [Theory]
    [InlineData(UsageExecutionKind.SubAgent)]
    [InlineData(UsageExecutionKind.WorkflowController)]
    [InlineData(UsageExecutionKind.WorkflowTask)]
    [InlineData(UsageExecutionKind.Continuation)]
    [InlineData(UsageExecutionKind.Compaction)]
    public void ExecutionIdOf_DescendantRecord_IsTheEmittingExecution(UsageExecutionKind kind)
    {
        AgentExecutionRef.ExecutionIdOf(Record("root-1", "subagent-agent-1", kind)).Should().Be("subagent-agent-1");
    }

    [Fact]
    public void Root_BuildsTheRootIdentity()
    {
        var root = AgentExecutionRef.Root("root-1");

        root.RootThreadId.Should().Be("root-1");
        root.ThreadId.Should().Be("root-1");
        root.AgentId.Should().Be(AgentExecutionRef.RootAgentId);
        root.ParentAgentId.Should().BeNull();
        root.ExecutionKind.Should().Be(UsageExecutionKind.Primary);
    }

    [Theory]
    // #705 production shape: subagent-{12 hex scope}-{ordinal agent id}.
    [InlineData("subagent-a1b2c3d4e5f6-agent-3", "agent-3")]
    [InlineData("subagent-000000000000-agent-12", "agent-12")]
    // Unscoped and pre-#705 ids: everything after the prefix is the agent id.
    [InlineData("subagent-agent-3", "agent-3")]
    [InlineData("subagent-custom", "custom")]
    [InlineData("subagent-1a2b3c4d-agent-7", "1a2b3c4d-agent-7")]
    // Not a scope: 12 characters but not all hex, so the whole tail is the agent id.
    [InlineData("subagent-a1b2c3d4e5fg-agent-3", "a1b2c3d4e5fg-agent-3")]
    // Not a sub-agent thread at all.
    [InlineData("plain-thread", "plain-thread")]
    [InlineData("root-1", "root-1")]
    public void AgentIdFromThreadId_ReadsTheScopedAgentId_ElseKeepsTheThreadId(string threadId, string expected)
    {
        AgentExecutionRef.AgentIdFromThreadId(threadId).Should().Be(expected);
    }

    [Fact]
    public void AgentIdFromThreadId_RoundTripsTheIdSubAgentThreadIdsMints()
    {
        var agentId = SubAgentThreadIds.AgentIdFor(3);

        AgentExecutionRef.AgentIdFromThreadId(SubAgentThreadIds.For("root-1", agentId)).Should().Be(agentId);
    }
}
