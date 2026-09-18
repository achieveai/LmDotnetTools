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

        registry.IdentityKey("Read", """{"file_path":"a.md","limit":10}""").Should().Be("Read|file_path=s:a.md");
        registry
            .IdentityKey("Grep", """{"path":"src","pattern":"foo","glob":"*.cs"}""")
            .Should()
            .Be("Grep|pattern=s:foo|path=s:src|glob=s:*.cs");
        registry.IdentityKey("Skill", """{"skill":"brainstorming"}""").Should().Be("Skill|skill=s:brainstorming");
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
        registry.IdentityKey("mcp__db__query", """{"sql":"select 1"}""").Should().Be("mcp__db__query|sql=s:select 1");
        registry.Resolve("Read").Kind.Should().Be(ToolKind.Resource);
    }

    [Fact]
    public void Merge_WithNoOverrides_IsTheDefaultRegistry() =>
        ToolKnowledgeRegistry.Merge(null).Should().BeSameAs(ToolKnowledgeRegistry.Default);

    [Theory]
    [InlineData("{\"file_path\":null}")]
    [InlineData("{\"file_path\":{\"path\":\"a.txt\"}}")]
    [InlineData("{\"file_path\":[\"a.txt\"]}")]
    [InlineData("{}")]
    [InlineData("{\"other\":\"a.txt\"}")]
    public void IdentityKey_IsNull_WhenANamedArgumentIsAbsentOrNotAScalarString(string argsJson)
    {
        // These all used to fold to "Read|file_path=", so a read of 5 and a read of nothing compared
        // equal and either could supersede the other. A row with no identity is never deduplicated,
        // which is the safe direction: absence is not an identity.
        ToolKnowledgeRegistry.Default.IdentityKey("Read", argsJson).Should().BeNull();
    }

    [Fact]
    public void IdentityKey_DistinguishesScalarsThatPrintTheSame()
    {
        // The string "5" and the number 5 are different arguments and must not share a key.
        var registry = ToolKnowledgeRegistry.Merge(
            new Dictionary<string, ToolKnowledgeEntry>
            {
                ["Page"] = new() { Kind = ToolKind.Resource, Identity = ["n"] },
            }
        );

        var asString = registry.IdentityKey("Page", "{\"n\":\"5\"}");
        var asNumber = registry.IdentityKey("Page", "{\"n\":5}");
        var asBool = registry.IdentityKey("Page", "{\"n\":true}");

        asString.Should().NotBeNull();
        asNumber.Should().NotBeNull();
        asBool.Should().NotBeNull();
        new[] { asString, asNumber, asBool }.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void IdentityKey_SeparatorsInsideAValue_CannotImpersonateAnotherArgument()
    {
        // Without escaping, {a:"x|b=y"} and {a:"x", b:"y"} both render Tool|a=x|b=y, so a caller who
        // controls one argument's text could make an unrelated result look like the same resource.
        var registry = ToolKnowledgeRegistry.Merge(
            new Dictionary<string, ToolKnowledgeEntry>
            {
                ["Pair"] = new() { Kind = ToolKind.Resource, Identity = ["a", "b"] },
            }
        );

        var smuggled = registry.IdentityKey("Pair", "{\"a\":\"x|b=y\",\"b\":\"\"}");
        var genuine = registry.IdentityKey("Pair", "{\"a\":\"x\",\"b\":\"y\"}");

        smuggled.Should().NotBe(genuine);
    }

    [Fact]
    public void IdentityKey_EveryBuiltInResourceIdentity_IsStillReachable()
    {
        // Requiring every named argument to be present is stricter than before. This is the
        // non-vacuity check on that: the built-ins must still produce keys for an ordinary call,
        // or the dedupe would have been silently turned off rather than made correct.
        var registry = ToolKnowledgeRegistry.Default;

        registry.IdentityKey("Read", "{\"file_path\":\"a.txt\"}").Should().NotBeNull();
        registry.IdentityKey("Glob", "{\"pattern\":\"*.cs\",\"path\":\"src\"}").Should().NotBeNull();
        registry.IdentityKey("Grep", "{\"pattern\":\"x\",\"path\":\"src\",\"glob\":\"*.cs\"}").Should().NotBeNull();
        registry.IdentityKey("Skill", "{\"skill\":\"s\"}").Should().NotBeNull();
        registry.IdentityKey("WebFetch", "{\"url\":\"https://x\"}").Should().NotBeNull();
        registry.IdentityKey("WebSearch", "{\"query\":\"q\"}").Should().NotBeNull();
        registry.IdentityKey("get-task", "{\"taskId\":\"t1\"}").Should().NotBeNull();
    }
}
