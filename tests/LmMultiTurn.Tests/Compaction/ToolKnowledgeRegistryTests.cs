using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

public sealed class ToolKnowledgeRegistryTests
{
    [Theory]
    [InlineData("Read", ToolKind.Resource)]
    [InlineData("Glob", ToolKind.Resource)]
    [InlineData("Grep", ToolKind.Resource)]
    [InlineData("Skill", ToolKind.Resource)]
    [InlineData("WebFetch", ToolKind.Resource)]
    [InlineData("WebSearch", ToolKind.Resource)]
    [InlineData("list-tasks", ToolKind.Resource)]
    [InlineData("get-task", ToolKind.Resource)]
    [InlineData("Bash", ToolKind.Shell)]
    [InlineData("PowerShell", ToolKind.Shell)]
    [InlineData("Write", ToolKind.Mutation)]
    [InlineData("Edit", ToolKind.Mutation)]
    [InlineData("MultiEdit", ToolKind.Mutation)]
    [InlineData("NotebookEdit", ToolKind.Mutation)]
    [InlineData("SendMessage", ToolKind.Collab)]
    [InlineData("Agent", ToolKind.Collab)]
    [InlineData("SomethingNobodyRegistered", ToolKind.Shell)]
    public void Default_KnowsTheBuiltInTools_AndTreatsUnknownAsShell(string tool, ToolKind kind) =>
        ToolKnowledgeRegistry.Default.Resolve(tool).Kind.Should().Be(kind);

    [Fact]
    public void IdentityKey_IsTheToolPlusItsIdentityArgumentsInDeclaredOrder()
    {
        var registry = ToolKnowledgeRegistry.Default;

        registry.IdentityKey("Read", """{"file_path":"a.md","limit":10}""").Should().Be("Read|file_path=a.md");
        registry
            .IdentityKey("Grep", """{"path":"src","pattern":"foo","glob":"*.cs"}""")
            .Should()
            .Be("Grep|pattern=foo|path=src|glob=*.cs");
        registry.IdentityKey("Skill", """{"skill":"brainstorming"}""").Should().Be("Skill|skill=brainstorming");
    }

    [Fact]
    public void IdentityKey_IsNullForShellMutationCollab_AndForMalformedArgs()
    {
        var registry = ToolKnowledgeRegistry.Default;

        registry.IdentityKey("Bash", """{"command":"ls"}""").Should().BeNull();
        registry.IdentityKey("Write", """{"file_path":"a.md"}""").Should().BeNull();
        registry.IdentityKey("Read", "not json").Should().BeNull();
    }

    [Fact]
    public void IdentityKey_WithNoIdentityArguments_IsTheToolNameAlone() =>
        ToolKnowledgeRegistry.Default.IdentityKey("list-tasks", "{}").Should().Be("list-tasks");

    [Fact]
    public void Merge_OverridesAndAddsEntries_CaseInsensitively()
    {
        var registry = ToolKnowledgeRegistry.Merge(
            new Dictionary<string, ToolKnowledgeEntry>
            {
                ["bash"] = new() { Kind = ToolKind.Resource, Identity = ["command"] },
                ["mcp__db__query"] = new() { Kind = ToolKind.Resource, Identity = ["sql"] },
            }
        );

        registry.Resolve("Bash").Kind.Should().Be(ToolKind.Resource);
        registry.IdentityKey("mcp__db__query", """{"sql":"select 1"}""").Should().Be("mcp__db__query|sql=select 1");
        registry.Resolve("Read").Kind.Should().Be(ToolKind.Resource);
    }

    [Fact]
    public void Merge_WithNoOverrides_IsTheDefaultRegistry() =>
        ToolKnowledgeRegistry.Merge(null).Should().BeSameAs(ToolKnowledgeRegistry.Default);
}
