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
    [InlineData("subagent-agent-3", "agent-3")]
    [InlineData("subagent-1a2b3c4d-agent-7", "1a2b3c4d-agent-7")]
    [InlineData("plain-thread", "plain-thread")]
    public void AgentIdFromThreadId_StripsTheSubAgentPrefix_ElseKeepsTheThreadId(string threadId, string expected)
    {
        AgentExecutionRef.AgentIdFromThreadId(threadId).Should().Be(expected);
    }
}
