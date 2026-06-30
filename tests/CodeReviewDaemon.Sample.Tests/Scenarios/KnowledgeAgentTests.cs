using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// P4.1 — the Knowledge agent distills a review into <c>KnowledgeBase/{slug}.md</c> and regenerates
/// <c>_toc.md</c> from the entries present. These tests pin the file-writing and ToC-regeneration
/// behavior against in-memory fakes: the entry lands at the slugified path, the ToC links every
/// Markdown entry (sorted, excluding itself and non-entries), and the entry is always heading-prefixed.
/// </summary>
public sealed class KnowledgeAgentTests : LoggingTestBase
{
    private const string RunId = "knowledge-run-1";
    private const string RepoRoot = "/work/reviewbot";
    private const string KbDir = RepoRoot + "/KnowledgeBase";

    public KnowledgeAgentTests(ITestOutputHelper output)
        : base(output)
    {
    }

    [Fact]
    public async Task WriteEntryAsync_writes_the_entry_at_the_slugified_path()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning("# Null Checks\n\nAlways null-check external inputs.");

        var result = await Knowledge(agent, fs).WriteEntryAsync(
            RepoRoot, "Null Checks", "Distill this review.", CancellationToken.None);

        result.EntryFileName.Should().Be("null-checks.md");
        result.RunId.Should().Be(RunId);
        fs.Files.Should().ContainKey(KbDir + "/null-checks.md");
        fs.Files[KbDir + "/null-checks.md"].Should().Be("# Null Checks\n\nAlways null-check external inputs.");
    }

    [Fact]
    public async Task WriteEntryAsync_prefixes_a_title_heading_when_the_model_omits_one()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning("Always null-check external inputs.");

        _ = await Knowledge(agent, fs).WriteEntryAsync(
            RepoRoot, "Null Checks", "distill", CancellationToken.None);

        fs.Files[KbDir + "/null-checks.md"].Should().Be("# Null Checks\n\nAlways null-check external inputs.");
    }

    [Fact]
    public async Task WriteEntryAsync_regenerates_the_toc_with_a_link_to_the_entry()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning("# Null Checks\n\nbody");

        _ = await Knowledge(agent, fs).WriteEntryAsync(
            RepoRoot, "Null Checks", "distill", CancellationToken.None);

        fs.Files[KbDir + "/_toc.md"].Should().Be("# Knowledge Base\n\n- [Null Checks](null-checks.md)\n");
    }

    [Fact]
    public async Task WriteEntryAsync_lists_pre_existing_entries_sorted_and_excludes_non_entries()
    {
        var fs = new FakeSandboxFileSystem();
        // A KB that already holds one entry plus the bookkeeping files the ToC must ignore.
        fs.Files[KbDir + "/aaa-first.md"] = "# Aaa First\n\nearlier knowledge";
        fs.Files[KbDir + "/.gitkeep"] = string.Empty;
        fs.Files[KbDir + "/_toc.md"] = "# Knowledge Base\n\n- [Aaa First](aaa-first.md)\n";
        var agent = AgentReturning("# Null Checks\n\nbody");

        _ = await Knowledge(agent, fs).WriteEntryAsync(
            RepoRoot, "Null Checks", "distill", CancellationToken.None);

        // Sorted by file name; .gitkeep and _toc.md excluded.
        fs.Files[KbDir + "/_toc.md"].Should().Be(
            "# Knowledge Base\n\n- [Aaa First](aaa-first.md)\n- [Null Checks](null-checks.md)\n");
    }

    [Fact]
    public async Task WriteEntryAsync_falls_back_to_the_file_name_when_an_entry_has_no_heading()
    {
        var fs = new FakeSandboxFileSystem();
        fs.Files[KbDir + "/legacy.md"] = "no heading here";
        var agent = AgentReturning("# Null Checks\n\nbody");

        _ = await Knowledge(agent, fs).WriteEntryAsync(
            RepoRoot, "Null Checks", "distill", CancellationToken.None);

        fs.Files[KbDir + "/_toc.md"].Should().Be(
            "# Knowledge Base\n\n- [legacy.md](legacy.md)\n- [Null Checks](null-checks.md)\n");
    }

    [Theory]
    [InlineData("Null Checks", "null-checks.md")]
    [InlineData("  Retry / Backoff!! ", "retry-backoff.md")]
    [InlineData("HTTP & gRPC", "http-grpc.md")]
    public async Task WriteEntryAsync_slugifies_the_title(string title, string expectedFileName)
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning("# x\n\nbody");

        var result = await Knowledge(agent, fs).WriteEntryAsync(
            RepoRoot, title, "distill", CancellationToken.None);

        result.EntryFileName.Should().Be(expectedFileName);
    }

    private static FakeMultiTurnAgent AgentReturning(string text) =>
        new(RunId, new TextMessage { Text = text, Role = Role.Assistant, RunId = RunId });

    private KnowledgeAgent Knowledge(FakeMultiTurnAgent agent, FakeSandboxFileSystem fs) =>
        new(agent, fs, LoggerFactory.CreateLogger<KnowledgeAgent>());
}
