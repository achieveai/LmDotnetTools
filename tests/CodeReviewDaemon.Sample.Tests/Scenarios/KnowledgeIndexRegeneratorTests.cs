using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The property the notes-branch merge leans on: <c>_index.jsonl</c> and <c>_toc.md</c> are pure functions
/// of the entry files present, so a listing that lost entries to a merge resolution can always be rebuilt
/// from the tree rather than reconciled between two sides (issue #218 item 6).
/// </summary>
public sealed class KnowledgeIndexRegeneratorTests : LoggingTestBase
{
    private const string KnowledgeBaseDir = "/work/reviewbot/KnowledgeBase";

    public KnowledgeIndexRegeneratorTests(ITestOutputHelper output)
        : base(output) { }

    private KnowledgeIndexRegenerator Create(FakeSandboxFileSystem fs) =>
        new(fs, LoggerFactory.CreateLogger<KnowledgeIndexRegeneratorTests>());

    private static string Entry(string title, string scope) =>
        $"""
            ---
            title: {title}
            tags: []
            scope: {scope}
            sourcePrs: []
            updated: 2026-08-25
            ---

            Body.
            """;

    [Fact]
    public async Task Rebuilds_a_listing_that_lost_an_entry_to_a_merge_resolution()
    {
        // Exactly the tree `git merge -X theirs` leaves behind when a concurrent PR's knowledge reached the
        // default branch first: BOTH entry files are present (the merge kept them — they never conflicted,
        // being different paths), but the whole-file listings conflicted and the notes branch's copy won,
        // so the earlier entry is missing from both.
        var fs = new FakeSandboxFileSystem()
            .Seed($"{KnowledgeBaseDir}/system/earlier-lane.md", Entry("Earlier Lane Lesson", "system"))
            .Seed($"{KnowledgeBaseDir}/system/this-lane.md", Entry("This Lane Lesson", "system"))
            .Seed(
                $"{KnowledgeBaseDir}/_index.jsonl",
                """{"file":"system/this-lane.md","title":"This Lane Lesson","tags":[],"scope":"system","sourcePrs":[],"updated":"2026-08-25"}"""
                    + "\n"
            )
            .Seed(
                $"{KnowledgeBaseDir}/_toc.md",
                "# Knowledge Base\n\n## system\n\n- [This Lane Lesson](system/this-lane.md)\n"
            );

        var changed = await Create(fs).RegenerateAsync(KnowledgeBaseDir, CancellationToken.None);

        changed.Should().BeTrue("the rebuilt listings differ from the ones the merge resolution left behind");

        var index = fs.Files[$"{KnowledgeBaseDir}/_index.jsonl"];
        index.Should().Contain("system/earlier-lane.md", "the entry the resolution dropped is back in the index");
        index.Should().Contain("system/this-lane.md");

        var toc = fs.Files[$"{KnowledgeBaseDir}/_toc.md"];
        toc.Should().Contain("system/earlier-lane.md");
        toc.Should().Contain("system/this-lane.md");
    }

    [Fact]
    public async Task Reports_no_change_when_the_listings_already_describe_the_tree()
    {
        var fs = new FakeSandboxFileSystem().Seed(
            $"{KnowledgeBaseDir}/system/only-entry.md",
            Entry("Only Entry", "system")
        );

        // First pass establishes the listings; the second has nothing to do. The merge caller commits only
        // on a true, so a false here is what keeps an empty commit off the default branch every sweep.
        _ = await Create(fs).RegenerateAsync(KnowledgeBaseDir, CancellationToken.None);
        var changed = await Create(fs).RegenerateAsync(KnowledgeBaseDir, CancellationToken.None);

        changed.Should().BeFalse("both renderers are deterministic, so an unchanged tree rebuilds identically");
    }
}
