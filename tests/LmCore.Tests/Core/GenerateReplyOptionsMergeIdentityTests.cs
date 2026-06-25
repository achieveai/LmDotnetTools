using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Core;

/// <summary>
///     The run-identity fields (<see cref="GenerateReplyOptions.RunId"/>,
///     <see cref="GenerateReplyOptions.ParentRunId"/>, <see cref="GenerateReplyOptions.ThreadId"/>,
///     <see cref="GenerateReplyOptions.GenerationId"/>) are now part of the Merge contract — the
///     other's value overrides when set, otherwise the original is preserved. These ids drive the
///     client merge key, so a regression here silently breaks message grouping.
/// </summary>
public class GenerateReplyOptionsMergeIdentityTests
{
    [Fact]
    public void Merge_OverridesIdentityFields_WhenOtherSetsThem()
    {
        var original = new GenerateReplyOptions
        {
            RunId = "run-original",
            ParentRunId = "parent-original",
            ThreadId = "thread-original",
            GenerationId = "gen-original",
        };
        var other = new GenerateReplyOptions
        {
            RunId = "run-new",
            ParentRunId = "parent-new",
            ThreadId = "thread-new",
            GenerationId = "gen-new",
        };

        var merged = original.Merge(other);

        Assert.Equal("run-new", merged.RunId);
        Assert.Equal("parent-new", merged.ParentRunId);
        Assert.Equal("thread-new", merged.ThreadId);
        Assert.Equal("gen-new", merged.GenerationId);
    }

    [Fact]
    public void Merge_PreservesIdentityFields_WhenOtherLeavesThemNull()
    {
        var original = new GenerateReplyOptions
        {
            RunId = "run-original",
            ParentRunId = "parent-original",
            ThreadId = "thread-original",
            GenerationId = "gen-original",
        };
        // other carries no identity fields (all null) — original must survive the merge.
        var other = new GenerateReplyOptions { ModelId = "some-model" };

        var merged = original.Merge(other);

        Assert.Equal("run-original", merged.RunId);
        Assert.Equal("parent-original", merged.ParentRunId);
        Assert.Equal("thread-original", merged.ThreadId);
        Assert.Equal("gen-original", merged.GenerationId);
    }

    [Fact]
    public void Merge_AppliesIdentityFields_WhenOriginalHadNone()
    {
        var original = new GenerateReplyOptions { ModelId = "some-model" };
        var other = new GenerateReplyOptions
        {
            RunId = "run-new",
            ParentRunId = "parent-new",
            ThreadId = "thread-new",
            GenerationId = "gen-new",
        };

        var merged = original.Merge(other);

        Assert.Equal("run-new", merged.RunId);
        Assert.Equal("parent-new", merged.ParentRunId);
        Assert.Equal("thread-new", merged.ThreadId);
        Assert.Equal("gen-new", merged.GenerationId);
    }
}
