using System.Globalization;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
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
    public async Task TryExtractAsync_declines_and_writes_nothing_when_the_gate_fires()
    {
        var fs = new FakeSandboxFileSystem();
        // Seed the index/ToC so we can prove the gate leaves them untouched.
        fs.Files[KbDir + "/_index.jsonl"] = "seeded-index";
        fs.Files[KbDir + "/_toc.md"] = "seeded-toc";
        var agent = AgentReturning("NO_KNOWLEDGE — this PR yields nothing durable.");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Declined);
        fs.Writes.Should().BeEmpty();
        fs.Files[KbDir + "/_index.jsonl"].Should().Be("seeded-index");
        fs.Files[KbDir + "/_toc.md"].Should().Be("seeded-toc");
    }

    // ---- The hosted-mode contract: extraction runs inside a workspace-agent conversation ------------

    /// <summary>
    /// The exact reply shape observed live on both daemons: on the S2S path the extraction turn is a hosted
    /// conversation whose mode prompt tells the model to use sandbox tools, keep task memory, and "summarize
    /// what you changed when done" — instructions that outrank the extraction profile riding as a system-prompt
    /// appendix. Every August run answered like this, so every August run wrote nothing.
    /// </summary>
    private const string WorkspaceAgentStyleReply =
        "I reviewed the notes and preserved the existing review unchanged. "
        + "I also created workspace task memory at `Memory/tasks/review-mcqdbdev-11251.md`. "
        + "Existing unrelated workspace modifications were left untouched.";

    [Fact]
    public async Task TryExtractAsync_restates_the_output_contract_in_the_user_turn()
    {
        // The profile prompt is only an APPENDIX to the host's workspace-agent mode prompt, and it loses.
        // The user turn is the last thing the model reads, so the contract has to be there too — otherwise
        // the reply is a conversational action summary and the extraction silently writes nothing.
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning("NO_KNOWLEDGE");

        _ = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        var sent = InputText(agent.ReceivedInputs.Should().ContainSingle().Subject);
        sent.Should().Contain("distill these notes");
        sent.Should().Contain("NO_KNOWLEDGE");
        sent.Should().Contain("## SCOPE:");
        sent.Should().Contain(
            "Do not use tools", "the mode prompt mandates tool use for all operations; this turn must not");
    }

    [Fact]
    public async Task TryExtractAsync_nudges_once_when_the_reply_ignores_the_contract_then_writes_the_entry()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(WorkspaceAgentStyleReply)
            .ThenReplies(Assistant(
                "## SCOPE: system\n"
                + "## TITLE: Notes Branch Lifecycle\n"
                + "## TAGS: daemon, notes\n\n"
                + "Delete the notes branch only after the extraction pass has run."));

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        // The salvage is a SAME-THREAD second turn, not a fresh run: the model keeps the notes it already read.
        agent.ReceivedInputs.Should().HaveCount(2);
        InputText(agent.ReceivedInputs[1]).Should().Contain("## SCOPE:");

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        result.EntryFileName.Should().Be("system/notes-branch-lifecycle.md");
        fs.Files.Should().ContainKey(KbDir + "/system/notes-branch-lifecycle.md");
    }

    [Fact]
    public async Task TryExtractAsync_gates_quietly_when_the_nudged_reply_declines()
    {
        // PR #249's whole input was "No new findings since the last review." — NO_KNOWLEDGE is the correct
        // answer there, and reaching it on the second turn is a success, not a failure.
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(WorkspaceAgentStyleReply).ThenReplies(Assistant("NO_KNOWLEDGE"));

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        // Reaching NO_KNOWLEDGE on the second turn is a decline, not a failure — nothing here is retryable.
        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Declined);
        fs.Writes.Should().BeEmpty();
        agent.ReceivedInputs.Should().HaveCount(2);
    }

    [Fact]
    public async Task TryExtractAsync_nudges_at_most_once_and_writes_nothing_when_the_reply_still_does_not_conform()
    {
        // Bounded salvage: one corrective turn, then give up. A retry loop against a model locked into the
        // wrong mode would burn the review budget without ever conforming.
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(WorkspaceAgentStyleReply).ThenReplies(Assistant(
            "What would you like me to do next — post the review comments or prepare a fix?"));

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        // An unusable reply is a LOST extraction, not a decline: the caller must be able to retry it on a
        // later sweep rather than merge the notes away as if the PR had taught nothing (defect D5).
        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Failed);
        fs.Writes.Should().BeEmpty();
        agent.ReceivedInputs.Should().HaveCount(2);
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

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
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

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
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
        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Failed);
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
        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Failed);
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
        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        result.EntryFileName.Should().Be("system/innocent-looking.md");
        fs.Files[escapePath].Should().Be("#!/bin/sh\necho pwned");
        fs.Writes.Should().OnlyContain(path => path.StartsWith(KbDir + "/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TryExtractAsync_refuses_an_UPDATES_target_that_is_a_bookkeeping_file()
    {
        var fs = new FakeSandboxFileSystem();
        // A real _toc.md already exists (regenerated by earlier runs); a crafted ## UPDATES must never
        // target a bookkeeping file even though it validates as a KB-relative path AND exists.
        fs.Files[KbDir + "/_toc.md"] = "# Table of Contents\n\n- [Old Entry](system/old.md)\n";
        var agent = AgentReturning(
            "## UPDATES: _toc.md\n"
            + "## SCOPE: system\n"
            + "## TITLE: Not The Toc\n\n"
            + "body");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        // The bookkeeping UPDATES is refused; the create falls back to the safe scope+slug path instead.
        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        result.EntryFileName.Should().Be("system/not-the-toc.md");
        fs.Files[KbDir + "/_toc.md"].Should().NotContain("title:");
        fs.Files[KbDir + "/_toc.md"].Should().Contain("- [Not The Toc](system/not-the-toc.md)");
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

        result.EntryFileName.Should().Be("acme-widgets/repo-rule.md");
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

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
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

    [Fact]
    public async Task TryExtractAsync_preserves_a_body_line_shaped_like_a_marker()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(
            "## SCOPE: system\n"
            + "## TITLE: Marker Syntax Guide\n"
            + "## TAGS: docs\n\n"
            + "The agent emits markers like:\n"
            + "## TAGS: a, b\n"
            + "Keep them at the top.");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        result!.EntryFileName.Should().Be("system/marker-syntax-guide.md");
        var entryPath = KbDir + "/system/marker-syntax-guide.md";
        var meta = KnowledgeIndex.ParseFrontmatter("system/marker-syntax-guide.md", fs.Files[entryPath]);
        meta.Should().NotBeNull();
        // The real header TAGS marker wins; the body's heading-shaped line is NOT re-parsed as a marker.
        meta!.Tags.Should().Equal("docs");
        fs.Files[entryPath].Should().Contain("The agent emits markers like:");
        fs.Files[entryPath].Should().Contain("## TAGS: a, b");
        fs.Files[entryPath].Should().Contain("Keep them at the top.");
    }

    [Fact]
    public async Task TryExtractAsync_reuses_an_existing_scope_directorys_case_avoiding_a_case_variant_collision()
    {
        // The extraction agent (an LLM) cases a repo scope inconsistently across runs. Written verbatim, a
        // second-cased scope ('MCQdbDEV' after an earlier 'mcqdbdev') becomes a distinct directory: a
        // case-sensitive git tracks both, but a case-insensitive checkout (Windows) collapses them and loses
        // entries, breaking KB retrieval (observed live on the mcqdb store). A new entry must reuse the
        // EXISTING scope directory's case so every entry for a scope stays in ONE directory.
        var fs = new FakeSandboxFileSystem();
        fs.Files[KbDir + "/mcqdbdev/existing-lesson.md"] =
            "---\ntitle: Existing\ntags: []\nscope: mcqdbdev\nsourcePrs: [\"github/o-r/1\"]\nupdated: 2026-07-01\n---\nbody";
        var agent = AgentReturning(
            "## SCOPE: MCQdbDEV\n## TITLE: New Lesson\n## TAGS: a\n\nA newly distilled lesson.");

        var result = await Knowledge(agent, fs).TryExtractAsync(
            RepoRoot, "notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        result!.EntryFileName.Should().Be(
            "mcqdbdev/new-lesson.md", "the new entry reuses the existing scope directory's case");
        fs.Files.Should().ContainKey(KbDir + "/mcqdbdev/new-lesson.md");
        fs.Files.Keys.Where(k => k.Contains("/MCQdbDEV/", StringComparison.Ordinal))
            .Should().BeEmpty("a second case-variant scope directory collides on a case-insensitive checkout");
        // The daemon-injected frontmatter scope matches the reconciled directory, not the model's casing.
        var meta = KnowledgeIndex.ParseFrontmatter("mcqdbdev/new-lesson.md", fs.Files[KbDir + "/mcqdbdev/new-lesson.md"]);
        meta!.Scope.Should().Be("mcqdbdev");
    }

    // ---- The existing-store listings the extraction prompt carries are bounded, and say when they are --

    /// <summary>One index record, sized like a real one so the listings below grow the way a real store does.</summary>
    private static string IndexRecord(int ordinal)
    {
        var id = ordinal.ToString("D4", CultureInfo.InvariantCulture);
        return "{\"file\":\"system/entry-" + id + ".md\",\"title\":\"Entry " + id
            + "\",\"tags\":[\"padding\",\"listing\"],\"scope\":\"system\",\"updated\":\"2026-07-06\"}";
    }

    /// <summary>The part of the extraction prompt under <paramref name="heading"/>, up to the next section.</summary>
    private static string Section(string prompt, string heading)
    {
        var start = prompt.IndexOf(heading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the prompt must carry the '{0}' section", heading);
        start += heading.Length;
        var end = prompt.IndexOf("\n\n## ", start, StringComparison.Ordinal);
        return end < 0 ? prompt[start..] : prompt[start..end];
    }

    [Fact]
    public async Task TryExtractAsync_tells_the_agent_when_the_existing_index_did_not_fit_in_the_prompt()
    {
        // The agent reads this listing SPECIFICALLY to update a related entry instead of duplicating one. A
        // listing that quietly loses its tail therefore does not merely shrink a prompt — it manufactures
        // duplicate Knowledge Base entries, because the agent still believes it has seen the whole store.
        var fs = new FakeSandboxFileSystem();
        fs.Files[KbDir + "/_index.jsonl"] =
            string.Join("\n", Enumerable.Range(0, 400).Select(IndexRecord));
        var logger = new CapturingLogger<KnowledgeAgent>();
        var agent = AgentReturning("NO_KNOWLEDGE");

        _ = await Knowledge(agent, fs, logger).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        var sent = InputText(agent.ReceivedInputs.Should().ContainSingle().Subject);
        var listing = Section(sent, "## Existing Knowledge Base index (_index.jsonl)\n");
        listing.Should().Contain(
            "This listing is PARTIAL",
            "the cap has to be visible to the AGENT, not only to our logs — the log cannot stop a duplicate");
        listing.Should().Contain(
            "not in this list",
            "the agent must be told the one inference it must not draw from a shortened listing");

        // What SURVIVED still has to be usable: whole records, in order, starting at the top of the store.
        var shown = listing[..listing.IndexOf("**This listing is PARTIAL", StringComparison.Ordinal)].Trim();
        shown.Should().Contain(IndexRecord(0), "the surviving head of the listing must still be readable");
        shown.Split('\n').Should().OnlyContain(
            line => line.EndsWith('}'),
            "cutting mid-record would hand the agent a torn entry it could misread as a real one");
        sent.Should().NotContain("system/entry-0399.md", "the tail is what did not fit");

        logger.CountAtLevel(LogLevel.Warning, "_index.jsonl").Should().Be(
            1, "operators need to know the store outgrew the prompt even though the agent was told too");
    }

    [Fact]
    public async Task TryExtractAsync_tells_the_agent_when_the_existing_toc_did_not_fit_in_the_prompt()
    {
        // The ToC is the index's neighbour route into the same prompt: an unbounded one costs the same and
        // hides the same entries, so it carries the same guarantee.
        var fs = new FakeSandboxFileSystem();
        fs.Files[KbDir + "/_toc.md"] = string.Join(
            "\n",
            Enumerable.Range(0, 400).Select(i =>
                "- [Entry " + i.ToString("D4", CultureInfo.InvariantCulture) + "](system/entry-"
                + i.ToString("D4", CultureInfo.InvariantCulture) + ".md) — padding padding padding padding"));
        var logger = new CapturingLogger<KnowledgeAgent>();
        var agent = AgentReturning("NO_KNOWLEDGE");

        _ = await Knowledge(agent, fs, logger).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        var sent = InputText(agent.ReceivedInputs.Should().ContainSingle().Subject);
        var listing = Section(sent, "## Existing Knowledge Base table of contents (_toc.md)\n");
        listing.Should().Contain("This listing is PARTIAL");
        listing.Should().Contain("(system/entry-0000.md)", "the surviving head must still be readable");
        sent.Should().NotContain("system/entry-0399.md");
        logger.CountAtLevel(LogLevel.Warning, "_toc.md").Should().Be(1);
    }

    [Fact]
    public async Task TryExtractAsync_does_not_claim_a_partial_listing_for_a_store_that_fits()
    {
        // The partner pin. A warning that fires on every healthy store is a warning the agent learns to
        // ignore, which costs us the one case it exists for.
        var fs = new FakeSandboxFileSystem();
        fs.Files[KbDir + "/_index.jsonl"] = string.Join("\n", Enumerable.Range(0, 12).Select(IndexRecord));
        fs.Files[KbDir + "/_toc.md"] = "# Knowledge Base\n\n- [Entry 0000](system/entry-0000.md)";
        var logger = new CapturingLogger<KnowledgeAgent>();
        var agent = AgentReturning("NO_KNOWLEDGE");

        _ = await Knowledge(agent, fs, logger).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        var sent = InputText(agent.ReceivedInputs.Should().ContainSingle().Subject);
        sent.Should().NotContain("This listing is PARTIAL");
        // And a store that fits is carried WHOLE — the cap must not shave a listing that never needed it.
        sent.Should().Contain(IndexRecord(0));
        sent.Should().Contain(IndexRecord(11));
        sent.Should().Contain("# Knowledge Base\n\n- [Entry 0000](system/entry-0000.md)");
        logger.CountAtLevel(LogLevel.Warning, "PARTIAL").Should().Be(0);
    }

    // ---- The reads themselves are bounded, and a refusal never reads as an absence ------------------

    /// <summary>Content one byte past <paramref name="limit"/> — the smallest input the read must refuse.</summary>
    private static string OverLimit(long limit) => new('x', (int)limit + 1);

    [Fact]
    public async Task TryExtractAsync_tells_the_agent_the_listing_was_unread_rather_than_rendering_an_empty_store()
    {
        // The char cap above bounds PARSING; this bounds INGESTION. When the file never arrives at all, the
        // "(empty)" rendering reserved for a missing file would tell the agent the Knowledge Base is empty at
        // the moment it is largest — the same duplicate-manufacturing lie, told louder.
        var fs = new FakeSandboxFileSystem();
        fs.Files[KbDir + "/_index.jsonl"] = OverLimit(SandboxReadLimits.KnowledgeListingBytes);
        var logger = new CapturingLogger<KnowledgeAgent>();
        var agent = AgentReturning("NO_KNOWLEDGE");

        _ = await Knowledge(agent, fs, logger).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        var sent = InputText(agent.ReceivedInputs.Should().ContainSingle().Subject);
        var listing = Section(sent, "## Existing Knowledge Base index (_index.jsonl)\n");
        listing.Should().Contain(
            "could NOT be read",
            "the agent has to know the silence below it is an unread store, not an empty one");
        listing.Should().Contain("The store is not empty; it is unread.");
        listing.Should().NotContain(
            "(empty)",
            "'(empty)' is the MISSING-file rendering; reusing it for a refusal is the lie this exists to stop");
        sent.Should().NotContain("xxxxxxxxxx", "the refused bytes must not reach the prompt");

        logger.CountAtLevel(LogLevel.Warning, "_index.jsonl").Should().Be(
            1, "operators are the only ones who can trim the listing at the source");
    }

    [Fact]
    public async Task TryExtractAsync_refuses_to_overwrite_an_entry_it_could_not_read()
    {
        // The write below an ## UPDATES is a whole-file OVERWRITE, and the merge read is the only thing that
        // carries the entry's title, tags and sourcePrs into it. Treating an unreadable entry as an absent one
        // would not skip a merge — it would replace a durable entry with one distilled without ever seeing it.
        var fs = new FakeSandboxFileSystem();
        var entryPath = KbDir + "/system/x.md";
        fs.Files[entryPath] = OverLimit(SandboxReadLimits.KnowledgeEntryBytes);
        var logger = new CapturingLogger<KnowledgeAgent>();
        var agent = AgentReturning(
            "## SCOPE: system\n"
            + "## TITLE: X Invariant\n"
            + "## UPDATES: system/x.md\n\n"
            + "refined body with more detail");

        var result = await Knowledge(agent, fs, logger).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(
            KnowledgeExtractionOutcome.Failed,
            "a run that cannot read its target has not declined — it is retryable once the entry is trimmed");
        fs.Files[entryPath].Should().Be(
            OverLimit(SandboxReadLimits.KnowledgeEntryBytes), "the entry we could not read is left untouched");
        fs.Writes.Should().BeEmpty(
            "not even the regen may run: it would rewrite both listings off a store we failed halfway through");
        logger.CountAtLevel(LogLevel.Error, "refusing").Should().Be(
            1, "destroying a durable entry is the failure this refuses, and it is an operator-visible one");
    }

    [Fact]
    public async Task TryExtractAsync_regenerates_listings_that_still_name_an_entry_it_could_not_read()
    {
        // The regen REPLACES both listings, and the reviewer reads _toc.md as the set of entries that exist.
        // Dropping an unreadable entry here would not merely fail to index it — it would delete the only route
        // anything has to a file still sitting in the store, and the next extraction would duplicate it.
        var fs = new FakeSandboxFileSystem();
        fs.Files[KbDir + "/system/huge-lesson.md"] = OverLimit(SandboxReadLimits.KnowledgeEntryBytes);
        var logger = new CapturingLogger<KnowledgeAgent>();
        var agent = AgentReturning(
            "## SCOPE: system\n"
            + "## TITLE: Null Checks\n\n"
            + "Always null-check external inputs before dereferencing them.");

        var result = await Knowledge(agent, fs, logger).TryExtractAsync(
            RepoRoot, "distill these notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        fs.Files[KbDir + "/_toc.md"].Should().Contain(
            "(system/huge-lesson.md)", "the link still resolves, and it is the only route left to that file");
        fs.Files[KbDir + "/_index.jsonl"].Should().Contain("\"file\":\"system/huge-lesson.md\"");
        fs.Files[KbDir + "/_toc.md"].Should().Contain(
            "too large to index",
            "listed under a path-derived title is honest about what is unknown; a fabricated one is not");
        fs.Files[KbDir + "/_toc.md"].Should().Contain(
            "(system/null-checks.md)", "the entry this run wrote is listed beside it as usual");
        fs.Files[KbDir + "/_index.jsonl"].Should().NotContain(
            "xxxxxxxxxx", "the refused bytes are not read, so nothing from them can leak into a listing");
        logger.CountAtLevel(LogLevel.Warning, "huge-lesson.md").Should().Be(1);
    }

    private static FakeMultiTurnAgent AgentReturning(string text) => new(RunId, Assistant(text));


    private static TextMessage Assistant(string text) =>
        new() { Text = text, Role = Role.Assistant, RunId = RunId };

    /// <summary>The prose the daemon actually sent on a turn (the agent's single user message).</summary>
    private static string InputText(UserInput input) =>
        string.Join("\n", input.Messages.OfType<TextMessage>().Select(message => message.Text));

    private KnowledgeAgent Knowledge(
        FakeMultiTurnAgent agent,
        FakeSandboxFileSystem fs,
        ILogger<KnowledgeAgent>? logger = null
    ) => new(agent, fs, logger ?? LoggerFactory.CreateLogger<KnowledgeAgent>());
}
