using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// The at-close per-developer review-feedback pass: <see cref="ReviewFeedbackAgent.TryExtractAsync"/>
/// distills the mistakes a PR author repeats into
/// <c>KnowledgeBase/developers/&lt;slug&gt;.reviewfeedbacks.md</c>.
/// <para>
/// The record is committed to a public repository under a real person's name, so most of these tests pin
/// the ways it must REFUSE to write: no author, a bot author, an author that slugs to nothing, and an
/// existing record too large to show the model in full. The single-write path is the easy part; the file
/// never appearing under an identity nobody owns is the part worth pinning.
/// </para>
/// </summary>
public sealed class ReviewFeedbackAgentTests : LoggingTestBase
{
    private const string RunId = "feedback-run-1";
    private const string RepoRoot = "/work/reviewbot";
    private const string DevDir = RepoRoot + "/KnowledgeBase/developers";
    private const string SourcePr = "github/o-r/42";
    private const string Today = "2026-08-04";

    private const string ValidRecord =
        "## PATTERNS\n\n"
        + "### Drops the CancellationToken on async calls\n"
        + "- **Seen in:** github/o-r/42\n"
        + "- **What happens:** async APIs are called without forwarding the ambient token.\n"
        + "- **How to avoid it:** grep the diff for `Async(` with no token argument before pushing.";

    public ReviewFeedbackAgentTests(ITestOutputHelper output)
        : base(output)
    {
    }

    // ---- Refusals: the record names a real person, in public ----------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dependabot[bot]")]
    [InlineData("github-actions[bot]")]
    [InlineData("???")]
    public async Task TryExtractAsync_writes_nothing_when_no_developer_is_addressable(string? author)
    {
        // A missing author is an ORDINARY outcome (the provider payload omitted it, or the unit was
        // reconstructed from a branch name). A bot has nobody to give feedback to. An author that slugs to
        // nothing has no filename. In every case the alternative — a placeholder stem — would file a
        // public, named record under an identity nobody owns, and merge distinct people into one file.
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(ValidRecord);

        var result = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, author, "notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(
            KnowledgeExtractionOutcome.Declined, "there is nothing to retry — no identity was ever reported");
        fs.Writes.Should().BeEmpty();
        agent.ReceivedInputs.Should().BeEmpty("the model is not worth invoking when nothing can be written");
    }

    [Theory]
    [InlineData("octocat", "octocat")]
    [InlineData("Jane.Doe@contoso.com", "jane-doe-contoso-com")]
    [InlineData("AchieveAI\\gautam", "achieveai-gautam")]
    [InlineData("../../.git/hooks/pre-commit", "git-hooks-pre-commit")]
    public async Task TryExtractAsync_writes_the_record_under_a_slugged_name_inside_developers(
        string author, string expectedSlug)
    {
        // No component of this path comes from the model — it is derived here from the provider-reported
        // author — so the traversal class is removed rather than defended against. The slug is [a-z0-9-]
        // by construction, which is what makes an ADO uniqueName email and a crafted path both harmless.
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(ValidRecord);

        var result = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, author, "notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        result.EntryFileName.Should().Be($"developers/{expectedSlug}.reviewfeedbacks.md");
        fs.Files.Should().ContainKey($"{DevDir}/{expectedSlug}.reviewfeedbacks.md");
        fs.Files.Keys.Should().OnlyContain(
            key => key.StartsWith(DevDir + "/", StringComparison.Ordinal),
            "every write stays inside the reserved per-developer directory");
    }

    [Fact]
    public async Task TryExtractAsync_refuses_an_oversized_record_instead_of_rewriting_it_from_a_partial_view()
    {
        // The model REPLACES the record wholesale from what it is shown. Running it against a record too
        // large to show in full would delete every pattern outside the window — turning a damaged record
        // into a permanently truncated one. Refusing leaves the damage readable and repairable.
        var fs = new FakeSandboxFileSystem();
        var path = DevDir + "/octocat.reviewfeedbacks.md";
        var huge = new string('x', 40_000);
        fs.Files[path] = huge;
        var agent = AgentReturning(ValidRecord);

        var result = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, "octocat", "notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Failed, "this is retryable once the record shrinks");
        fs.Writes.Should().BeEmpty();
        fs.Files[path].Should().Be(huge, "the existing record survives byte-identical");
        agent.ReceivedInputs.Should().BeEmpty("the model is never shown a record it cannot be given in full");
    }

    // ---- The gate: most PRs add nothing --------------------------------------------------------------

    [Fact]
    public async Task TryExtractAsync_declines_and_leaves_the_record_byte_identical_when_the_gate_fires()
    {
        var fs = new FakeSandboxFileSystem();
        var path = DevDir + "/octocat.reviewfeedbacks.md";
        var seeded = "---\ndeveloper: octocat\nsourcePrs: [\"github/o-r/1\"]\nupdated: 2026-07-01\n---\n\n## PATTERNS\n\n### Old\n";
        fs.Files[path] = seeded;
        var agent = AgentReturning("NO_FEEDBACK");

        var result = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, "octocat", "notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Declined);
        fs.Writes.Should().BeEmpty();
        fs.Files[path].Should().Be(seeded, "a decline must not touch the record — not even its updated date");
    }

    // ---- The hosted-mode contract --------------------------------------------------------------------

    /// <summary>
    /// The reply shape observed live on both daemons: on the S2S path this turn is a hosted conversation
    /// whose <c>workspace-agent</c> mode prompt tells the model to use sandbox tools, keep task memory, and
    /// summarize what it changed — instructions that outrank the extraction profile riding as a system-prompt
    /// appendix. Every August 2026 knowledge run answered like this, so every one wrote nothing.
    /// </summary>
    private const string WorkspaceAgentStyleReply =
        "I reviewed the notes and left the existing review unchanged. "
        + "I also created workspace task memory at `Memory/tasks/review-42.md`.";

    [Fact]
    public async Task TryExtractAsync_restates_the_output_contract_in_the_user_turn()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning("NO_FEEDBACK");

        _ = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, "octocat", "distill these notes", SourcePr, Today, CancellationToken.None);

        var sent = InputText(agent.ReceivedInputs.Should().ContainSingle().Subject);
        sent.Should().Contain("distill these notes");
        sent.Should().Contain("NO_FEEDBACK");
        sent.Should().Contain("## PATTERNS");
        sent.Should().Contain(
            "Do not use tools", "the mode prompt mandates tool use for all operations; this turn must not");
        sent.Should().Contain(
            "Do not choose or name an output file", "the daemon owns the path, not the model");
    }

    [Fact]
    public async Task TryExtractAsync_nudges_once_when_the_reply_ignores_the_contract_then_writes()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(WorkspaceAgentStyleReply).ThenReplies(Assistant(ValidRecord));

        var result = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, "octocat", "notes", SourcePr, Today, CancellationToken.None);

        // A SAME-THREAD second turn, not a fresh run: the model keeps the notes and record it already read.
        agent.ReceivedInputs.Should().HaveCount(2);
        InputText(agent.ReceivedInputs[1]).Should().Contain("## PATTERNS");
        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
    }

    [Fact]
    public async Task TryExtractAsync_fails_rather_than_declining_when_the_nudged_reply_is_still_unusable()
    {
        // Failed, not Declined: this is a LOST extraction the caller may retry. Conflating the two is what
        // made every knowledge-extraction failure permanent (defect D5).
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning(WorkspaceAgentStyleReply).ThenReplies(Assistant("Still summarizing my actions."));

        var result = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, "octocat", "notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Failed);
        fs.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task TryExtractAsync_refuses_an_empty_patterns_block_rather_than_blanking_the_record()
    {
        // `## PATTERNS` followed by nothing is ambiguous, and the write semantics make the ambiguity
        // expensive: what the model emits REPLACES the file, so honouring it would delete every pattern.
        // The prompt has an unambiguous way to say "nothing" — NO_FEEDBACK — so an empty block is a
        // malformed reply, not an instruction to erase.
        var fs = new FakeSandboxFileSystem();
        var path = DevDir + "/octocat.reviewfeedbacks.md";
        var seeded = "---\ndeveloper: octocat\nsourcePrs: [\"github/o-r/1\"]\nupdated: 2026-07-01\n---\n\n## PATTERNS\n\n### Old\n";
        fs.Files[path] = seeded;
        var agent = AgentReturning("## PATTERNS\n\n   \n").ThenReplies(Assistant("## PATTERNS\n"));

        var result = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, "octocat", "notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Failed);
        fs.Writes.Should().BeEmpty();
        fs.Files[path].Should().Be(seeded);
    }

    // ---- The written record --------------------------------------------------------------------------

    [Fact]
    public async Task TryExtractAsync_injects_the_frontmatter_and_merges_the_source_prs()
    {
        var fs = new FakeSandboxFileSystem();
        var path = DevDir + "/octocat.reviewfeedbacks.md";
        fs.Files[path] =
            "---\ndeveloper: octocat\nsourcePrs: [\"github/o-r/1\"]\nupdated: 2026-07-01\n---\n\n## PATTERNS\n\n### Old pattern\n";
        var agent = AgentReturning(ValidRecord);

        var result = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, "octocat", "notes", SourcePr, Today, CancellationToken.None);

        result.Outcome.Should().Be(KnowledgeExtractionOutcome.Wrote);
        var written = fs.Files[path];
        written.Should().StartWith("---\ndeveloper: octocat\n");
        written.Should().Contain("updated: 2026-08-04");
        written.Should().Contain("Drops the CancellationToken");

        // The evidence trail accumulates: the earlier PR is not dropped when a later one contributes.
        var meta = KnowledgeIndex.ParseFrontmatter("developers/octocat.reviewfeedbacks.md", written);
        meta!.SourcePrs.Should().Equal("github/o-r/1", SourcePr);

        // Deliberately NO title: a per-developer record must not be able to masquerade as a curated
        // knowledge entry if it ever reaches the index.
        meta.Title.Should().BeEmpty();
    }

    [Fact]
    public async Task TryExtractAsync_shows_the_model_the_existing_record_without_the_daemon_owned_frontmatter()
    {
        // The model is told not to write frontmatter; showing it the fields invites echoing them back into
        // the body, where the next run would read them as content. It DOES need the existing patterns, so a
        // second instance updates the pattern it belongs to instead of appending a near-duplicate.
        var fs = new FakeSandboxFileSystem();
        fs.Files[DevDir + "/octocat.reviewfeedbacks.md"] =
            "---\ndeveloper: octocat\nsourcePrs: [\"github/o-r/1\"]\nupdated: 2026-07-01\n---\n\n"
            + "## PATTERNS\n\n### Drops the CancellationToken on async calls\n- **Seen in:** github/o-r/1\n";
        var agent = AgentReturning("NO_FEEDBACK");

        _ = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, "octocat", "notes", SourcePr, Today, CancellationToken.None);

        var sent = InputText(agent.ReceivedInputs.Should().ContainSingle().Subject);
        sent.Should().Contain("### Drops the CancellationToken on async calls");
        sent.Should().NotContain("sourcePrs:", "the daemon-owned frontmatter is stripped before the model sees it");
        sent.Should().NotContain("updated: 2026-07-01");
    }

    [Fact]
    public async Task TryExtractAsync_tells_the_model_there_is_no_record_yet_for_a_first_time_developer()
    {
        var fs = new FakeSandboxFileSystem();
        var agent = AgentReturning("NO_FEEDBACK");

        _ = await Feedback(agent, fs).TryExtractAsync(
            RepoRoot, "octocat", "notes", SourcePr, Today, CancellationToken.None);

        InputText(agent.ReceivedInputs.Should().ContainSingle().Subject)
            .Should().Contain("this developer has no record yet");
    }

    private static FakeMultiTurnAgent AgentReturning(string text) => new(RunId, Assistant(text));

    private static TextMessage Assistant(string text) =>
        new() { Text = text, Role = Role.Assistant, RunId = RunId };

    /// <summary>The prose the daemon actually sent on a turn (the agent's single user message).</summary>
    private static string InputText(UserInput input) =>
        string.Join("\n", input.Messages.OfType<TextMessage>().Select(message => message.Text));

    private ReviewFeedbackAgent Feedback(FakeMultiTurnAgent agent, FakeSandboxFileSystem fs) =>
        new(agent, fs, LoggerFactory.CreateLogger<ReviewFeedbackAgent>());
}
