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

    // ---- Task 4: gated layered extraction (create/update + index) -----------------------------------

    private const string SourcePr = "github/o-r/42";
    private const string Today = "2026-07-06";

    [Fact]
    public async Task TryExtractAsync_returns_null_and_writes_nothing_when_the_gate_fires()
    {
        var fs = new FakeSandboxFileSystem();
        // Seed the index/ToC so we can prove the gate leaves them untouched.
        fs.Files[KbDir + "/_index.jsonl"] = "seeded-index";
        fs.Files[KbDir + "/_toc.md"] = "seeded-toc";
        var agent = AgentReturning("NO_KNOWLEDGE — this PR yields nothing durable.");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        result.Should().BeNull();
        fs.Writes.Should().BeEmpty();
        fs.Files[KbDir + "/_index.jsonl"].Should().Be("seeded-index");
        fs.Files[KbDir + "/_toc.md"].Should().Be("seeded-toc");
    }

    [Fact]
    public async Task TryExtractAsync_creates_a_layered_entry_with_roundtrip_frontmatter()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(
            "## SCOPE: system\n"
            + "## TITLE: Null Checks\n"
            + "## TAGS: validation, inputs\n\n"
            + "Always null-check external inputs before dereferencing them.");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        result.Should().NotBeNull();
        result!.EntryFileName.Should().Be("system/null-checks.md");
        result.RunId.Should().Be(RunId);

        // The entry lands under KnowledgeBase/<scope>/<slug>.md with daemon-injected frontmatter that
        // round-trips through KnowledgeIndex.ParseFrontmatter (the queryable-index contract).
        var entryPath = KbDir + "/system/null-checks.md";
        fs.Files.Should().ContainKey(entryPath);
        var meta = KnowledgeIndex.ParseFrontmatter("system/null-checks.md", fs.Files[entryPath]);
        meta.Should().NotBeNull();
        meta!.Title.Should().Be("Null Checks");
        meta.Tags.Should().Equal("validation", "inputs");
        meta.Scope.Should().Be("system");
        meta.SourcePrs.Should().Equal(SourcePr);
        meta.Updated.Should().Be(Today);
        fs.Files[entryPath].Should().Contain("Always null-check external inputs");

        // _index.jsonl + _toc.md regenerated to include the new entry.
        fs.Files.Should().ContainKey(KbDir + "/_index.jsonl");
        fs.Files[KbDir + "/_index.jsonl"].Should().Contain("\"file\":\"system/null-checks.md\"");
        fs.Files[KbDir + "/_index.jsonl"].Should().Contain("\"sourcePrs\":[\"" + SourcePr + "\"]");
        fs.Files.Should().ContainKey(KbDir + "/_toc.md");
        fs.Files[KbDir + "/_toc.md"].Should().Contain("- [Null Checks](system/null-checks.md)");
    }

    [Fact]
    public async Task TryExtractAsync_updates_the_named_entry_and_merges_sourcePrs()
    {
        var fs = new FakeSandboxFileSystem();
        // A pre-existing entry sourced from one PR that the model chooses to refine.
        fs.Files[KbDir + "/system/x.md"] =
            "---\n"
            + "title: X Invariant\n"
            + "tags: [alpha]\n"
            + "scope: system\n"
            + "sourcePrs: [\"old\"]\n"
            + "updated: 2026-07-01\n"
            + "---\n\n# X Invariant\noriginal body";
        var agent = AgentReturning(
            "## SCOPE: system\n"
            + "## TITLE: X Invariant\n"
            + "## TAGS: alpha\n"
            + "## UPDATES: system/x.md\n\n"
            + "refined body with more detail");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", "github/o-r/99", Today, CancellationToken.None);

        result.Should().NotBeNull();
        result!.EntryFileName.Should().Be("system/x.md");

        // The existing entry is rewritten in place — no near-duplicate second file.
        fs.Files.Keys
            .Where(key => key.StartsWith(KbDir + "/system/", StringComparison.Ordinal) && key.EndsWith(".md", StringComparison.Ordinal))
            .Should().ContainSingle().Which.Should().Be(KbDir + "/system/x.md");

        var meta = KnowledgeIndex.ParseFrontmatter("system/x.md", fs.Files[KbDir + "/system/x.md"]);
        meta.Should().NotBeNull();
        meta!.SourcePrs.Should().Equal("old", "github/o-r/99");
        meta.Updated.Should().Be(Today);
        fs.Files[KbDir + "/system/x.md"].Should().Contain("refined body with more detail");
        fs.Files[KbDir + "/system/x.md"].Should().NotContain("original body");

        // The regenerated index carries exactly one entry.
        fs.Files[KbDir + "/_index.jsonl"]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Should().ContainSingle();
    }

    private static FakeMultiTurnAgent AgentReturning(string text) =>
        new(RunId, new TextMessage { Text = text, Role = Role.Assistant, RunId = RunId });

    private KnowledgeAgent Knowledge(FakeMultiTurnAgent agent, FakeSandboxFileSystem fs) =>
        new(agent, fs, LoggerFactory.CreateLogger<KnowledgeAgent>());
}
