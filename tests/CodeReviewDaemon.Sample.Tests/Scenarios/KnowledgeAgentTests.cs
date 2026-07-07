using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The at-close Knowledge agent (design §1/§2): <see cref="KnowledgeAgent.TryExtractAsync"/> gates on
/// durable, generalizable knowledge, writes a layered <c>KnowledgeBase/&lt;scope&gt;/&lt;slug&gt;.md</c> entry
/// with daemon-injected frontmatter (create-or-update), and regenerates <c>_index.jsonl</c> + <c>_toc.md</c>
/// from the entries present. These tests pin that behavior against in-memory fakes.
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

    // ---- Fix 1: path-traversal hardening on SCOPE / UPDATES -----------------------------------------

    [Fact]
    public async Task TryExtractAsync_refuses_a_traversal_scope_and_writes_nothing_outside_the_KB()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(
            "## SCOPE: ../../etc\n"
            + "## TITLE: Evil\n\n"
            + "malicious body");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        // A "../../" scope must escape NOTHING: the write is refused outright (gate), not redirected.
        result.Should().BeNull();
        fs.Writes.Should().BeEmpty();
        fs.Files.Keys.Should().NotContain(key => !key.StartsWith(KbDir + "/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryExtractAsync_refuses_a_scope_that_contains_a_separator()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(
            "## SCOPE: system/nested\n"
            + "## TITLE: Split Scope\n\n"
            + "body");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        // Scope must be ONE ref-safe segment; a scope carrying a path separator is refused, not split.
        result.Should().BeNull();
        fs.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task TryExtractAsync_refuses_a_traversal_updates_target_even_when_that_file_exists()
    {
        var fs = new FakeSandboxFileSystem();
        // A file planted OUTSIDE the KB that a crafted ## UPDATES tries to redirect the write onto.
        var escapePath = KbDir + "/../../.git/hooks/pre-commit.md";
        fs.Files[escapePath] = "#!/bin/sh\necho pwned";
        var agent = AgentReturning(
            "## UPDATES: ../../.git/hooks/pre-commit.md\n"
            + "## SCOPE: system\n"
            + "## TITLE: Innocent Looking\n\n"
            + "body");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        // The traversal UPDATES is refused and the create falls back to the safe scope+slug INSIDE the KB;
        // the planted escape file is never touched, and every write stays under KnowledgeBase/.
        result.Should().NotBeNull();
        result!.EntryFileName.Should().Be("system/innocent-looking.md");
        fs.Files[escapePath].Should().Be("#!/bin/sh\necho pwned");
        fs.Writes.Should().OnlyContain(path => path.StartsWith(KbDir + "/", StringComparison.Ordinal));
    }

    // ---- Fix 2 (finding #3): a valid single-segment scope create stays indexed ----------------------

    [Fact]
    public async Task TryExtractAsync_indexes_a_valid_single_segment_scope_create()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(
            "## SCOPE: acme-widgets\n"
            + "## TITLE: Repo Rule\n"
            + "## TAGS: repo\n\n"
            + "A repo-scoped rule worth keeping.");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        result!.EntryFileName.Should().Be("acme-widgets/repo-rule.md");
        // The one-level regen walk indexes the single-segment scope entry into BOTH bookkeeping files.
        fs.Files[KbDir + "/_index.jsonl"].Should().Contain("\"file\":\"acme-widgets/repo-rule.md\"");
        fs.Files[KbDir + "/_toc.md"].Should().Contain("- [Repo Rule](acme-widgets/repo-rule.md)");
    }

    // ---- Fix 4: marker parsing tolerates leading prose ----------------------------------------------

    [Fact]
    public async Task TryExtractAsync_extracts_markers_even_after_a_leading_prose_line()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(
            "Here is the distilled entry:\n"
            + "## SCOPE: system\n"
            + "## TITLE: Null Checks\n"
            + "## TAGS: validation\n\n"
            + "Always null-check external inputs.");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        result.Should().NotBeNull();
        result!.EntryFileName.Should().Be("system/null-checks.md");
        var entryPath = KbDir + "/system/null-checks.md";
        var meta = KnowledgeIndex.ParseFrontmatter("system/null-checks.md", fs.Files[entryPath]);
        meta.Should().NotBeNull();
        meta!.Title.Should().Be("Null Checks");
        meta.Tags.Should().Equal("validation");
        fs.Files[entryPath].Should().Contain("Always null-check external inputs");
        // The preamble line lands neither in a marker nor in the body.
        fs.Files[entryPath].Should().NotContain("Here is the distilled entry");
    }

    private static FakeMultiTurnAgent AgentReturning(string text) =>
        new(RunId, new TextMessage { Text = text, Role = Role.Assistant, RunId = RunId });

    private KnowledgeAgent Knowledge(FakeMultiTurnAgent agent, FakeSandboxFileSystem fs) =>
        new(agent, fs, LoggerFactory.CreateLogger<KnowledgeAgent>());
}
