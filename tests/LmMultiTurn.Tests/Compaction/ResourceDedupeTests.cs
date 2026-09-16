using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>RC1/RC6 (eval spec §4): every older result of the same resource identity points at the newest one.</summary>
public sealed class ResourceDedupeTests
{
    private static ThreadFixture Reads(params string[] paths)
    {
        var thread = new ThreadFixture().Human("go");
        foreach (var path in paths)
        {
            _ = thread.ToolTurn(tool: "Read", args: $$"""{"file_path":"{{path}}"}""", result: $"contents of {path}");
        }

        return thread;
    }

    [Fact]
    public void SameFileReadFourTimes_TheFirstThreeAreSupersededByTheFourth()
    {
        var thread = Reads("a.md", "a.md", "a.md", "a.md"); // results at seq 3, 5, 7, 9

        var superseded = ResourceDedupe.Superseded(thread.Rows, ToolKnowledgeRegistry.Default);

        superseded
            .Should()
            .Equal(
                new Dictionary<long, long>
                {
                    [3] = 9,
                    [5] = 9,
                    [7] = 9,
                }
            );
    }

    [Fact]
    public void DifferentIdentities_AreNotSuperseded()
    {
        var thread = Reads("a.md", "b.md");

        ResourceDedupe.Superseded(thread.Rows, ToolKnowledgeRegistry.Default).Should().BeEmpty();
    }

    [Fact]
    public void SkillReloadedThreeTimes_KeepsOnlyTheLastLoad()
    {
        var thread = new ThreadFixture().Human("go");
        for (var i = 0; i < 3; i++)
        {
            _ = thread.ToolTurn(tool: "Skill", args: """{"skill":"tdd"}""", result: "skill text");
        }

        ResourceDedupe
            .Superseded(thread.Rows, ToolKnowledgeRegistry.Default)
            .Should()
            .Equal(new Dictionary<long, long> { [3] = 7, [5] = 7 });
    }

    [Fact]
    public void ShellAndUnknownTools_AreNeverSuperseded()
    {
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurn(tool: "Bash", args: """{"command":"ls"}""")
            .ToolTurn(tool: "Bash", args: """{"command":"ls"}""")
            .ToolTurn(tool: "Mystery", args: """{"x":1}""")
            .ToolTurn(tool: "Mystery", args: """{"x":1}""");

        ResourceDedupe.Superseded(thread.Rows, ToolKnowledgeRegistry.Default).Should().BeEmpty();
    }

    [Fact]
    public void DeferredAndCheckpointRows_AreSkipped()
    {
        var thread = Reads("a.md").ToolTurn(tool: "Read", args: """{"file_path":"a.md"}""", deferred: true);

        ResourceDedupe.Superseded(thread.Rows, ToolKnowledgeRegistry.Default).Should().BeEmpty();
    }

    [Fact]
    public void AHostOverride_MakesAShellToolDedupable()
    {
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurn(tool: "Bash", args: """{"command":"ls"}""")
            .ToolTurn(tool: "Bash", args: """{"command":"ls"}""");

        var registry = ToolKnowledgeRegistry.Merge(
            new Dictionary<string, ToolKnowledgeEntry>
            {
                ["Bash"] = new() { Kind = ToolKind.Resource, Identity = ["command"] },
            }
        );

        ResourceDedupe.Superseded(thread.Rows, registry).Should().Equal(new Dictionary<long, long> { [3] = 5 });
    }
}
