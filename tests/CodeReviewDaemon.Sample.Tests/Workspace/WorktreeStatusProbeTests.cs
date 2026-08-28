using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

/// <summary>
/// <see cref="WorktreeStatusProbe"/> is the whole of the cleanliness gate's reading of
/// <c>git status --porcelain -b -z</c>: whether the probe answered, what it listed, and which of those
/// listings are contamination rather than the repository's own filters disagreeing with what was committed.
/// The parsing half is pure and pinned here directly; the classifying half runs two git probes per path and is
/// driven through the recording <see cref="FakeSandboxCommandRunner"/>.
/// </summary>
public sealed class WorktreeStatusProbeTests
{
    /// <summary>
    /// What <c>git status --porcelain -b -z</c> actually prepends in the checkout these tests describe. The
    /// daemon's store and reviewed checkout are both kept DETACHED, so git names <c>HEAD (no branch)</c>
    /// rather than a branch. Verified against git 2.53.0 rather than assumed: a clean detached checkout
    /// answers exactly <c>"## HEAD (no branch)\0"</c>.
    /// </summary>
    private const string DetachedHeader = "## HEAD (no branch)\0";

    private const string RecordedBlob = "4c38366b709654d4e876cb887e5ace15dd67bbeb";

    [Fact]
    public void Reported_separates_a_clean_answer_from_no_answer_at_all()
    {
        WorktreeStatusProbe.Reported(DetachedHeader).Should().BeTrue(
            "a clean tree still emits the branch header, which is the evidence the probe ran");
        WorktreeStatusProbe.Reported(DetachedHeader + " M src/Edited.cs\0").Should().BeTrue();
        WorktreeStatusProbe.Reported("## main...origin/main\0").Should().BeTrue();

        WorktreeStatusProbe.Reported(string.Empty).Should().BeFalse(
            "the empty string is what a probe whose output was LOST produces, not what a clean tree answers");
        WorktreeStatusProbe.Reported(null).Should().BeFalse();
        WorktreeStatusProbe.Reported(" M src/Edited.cs\0").Should().BeFalse(
            "entries without the header did not come from a `-b` probe that ran to completion");
    }

    /// <summary>
    /// The header is skipped, and skipping it is load-bearing. It arrives as its own NUL-terminated field and
    /// clears the short-field guard, so without the skip it reads as code <c>##</c> at path
    /// <c>HEAD (no branch)</c>: a leftover that is not a file, on every run, in a checkout the daemon
    /// deliberately keeps detached — which condemns every slot it is asked about.
    /// </summary>
    [Fact]
    public void Parse_does_not_read_the_branch_header_as_a_leftover_path()
    {
        WorktreeStatusProbe.Parse(DetachedHeader).Should().BeEmpty(
            "a detached checkout's header is not a dirty path");
        WorktreeStatusProbe.Parse("## main...origin/main\0").Should().BeEmpty(
            "neither is an attached one's");

        var entries = WorktreeStatusProbe.Parse(DetachedHeader + " M src/Edited.cs\0?? build/out.log\0");

        entries.Should().HaveCount(2);
        entries[0].Should().Be((" M", "src/Edited.cs"));
        entries[1].Should().Be(("??", "build/out.log"));
    }

    /// <summary>The skip is bounded to the leading field, so a real path beginning "## " stays reachable.</summary>
    [Fact]
    public void Parse_still_reads_a_record_whose_path_begins_with_the_header_prefix()
    {
        var entries = WorktreeStatusProbe.Parse(DetachedHeader + "?? ## odd name.md\0");

        entries.Should().ContainSingle().Which.Should().Be(("??", "## odd name.md"));
    }

    /// <summary>
    /// Porcelain v1 QUOTES any path containing a space, a quote or a non-ASCII byte, and a quoted path would
    /// never match the index entry the blob lookup asks about — so the NUL-delimited form is parsed instead.
    /// </summary>
    [Fact]
    public void Parse_reads_paths_that_the_newline_form_would_have_quoted()
    {
        var entries = WorktreeStatusProbe.Parse(
            DetachedHeader + " M src/My Documents/Ünïcode.cs\0?? build/out.log\0");

        entries.Should().HaveCount(2);
        entries[0].Should().Be((" M", "src/My Documents/Ünïcode.cs"));
        entries[1].Should().Be(("??", "build/out.log"));
    }

    /// <summary>
    /// A rename or copy record carries a SECOND path field. Read as an entry of its own it would be a path
    /// with no status code, and the two-character slice would eat its first characters — so it is consumed
    /// with the record it belongs to, behind the header as it arrives in production.
    /// </summary>
    [Fact]
    public void Parse_consumes_the_second_path_field_of_a_rename_with_its_record()
    {
        var entries = WorktreeStatusProbe.Parse(
            DetachedHeader + "R  src/New.cs\0src/Old.cs\0 M src/Edited.cs\0");

        entries.Should().HaveCount(2);
        entries[0].Should().Be(("R ", "src/New.cs"));
        entries[1].Should().Be((" M", "src/Edited.cs"));
    }

    /// <summary>A clean tree produces no entries — the empty tail after the final delimiter is not one.</summary>
    [Fact]
    public void Parse_reads_a_clean_tree_as_no_entries()
    {
        WorktreeStatusProbe.Parse(string.Empty).Should().BeEmpty();
        WorktreeStatusProbe.Parse(null).Should().BeEmpty();
        WorktreeStatusProbe.Parse("\0").Should().BeEmpty();
    }

    /// <summary>
    /// The production failure the classifier exists to survive. WeveNova declares
    /// <c>sources/dev/WeveNova/services/app/ServiceConfig.ini</c> as <c>text eol=crlf</c> while the blob
    /// committed for it already holds CRLF, so git's clean filter maps the worktree copy to LF, compares it
    /// against a CRLF blob, and reports all 91 of 91 lines modified on a checkout nothing has touched. No
    /// <c>checkout --force</c>, <c>reset --hard</c> or <c>clean -ffdx</c> can settle it — measured.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_treats_a_path_holding_the_recorded_bytes_as_normalized_not_leftover()
    {
        const string NormalizedPath = "sources/dev/WeveNova/services/app/ServiceConfig.ini";
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                $"rev-parse :{NormalizedPath}",
                new SandboxCommandResult(0, RecordedBlob + "\n", string.Empty))
            .OnArgvContains(
                $"hash-object --no-filters -- {NormalizedPath}",
                new SandboxCommandResult(0, RecordedBlob + "\n", string.Empty));

        var classified = await WorktreeStatusProbe.ClassifyAsync(
            new GitRunner(runner), "/store", [(" M", NormalizedPath)], CancellationToken.None);

        classified.Normalized.Should().ContainSingle().Which.Should().Be(NormalizedPath);
        classified.Leftovers.Should().BeEmpty();
    }

    /// <summary>
    /// A real edit changes the raw bytes and so changes their hash. The tolerance is keyed on blob identity
    /// and cannot be talked into passing content the recorded commit does not have.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_keeps_a_genuinely_edited_path_as_a_leftover()
    {
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse :src/Edited.cs",
                new SandboxCommandResult(0, RecordedBlob + "\n", string.Empty))
            .OnArgvContains(
                "hash-object --no-filters -- src/Edited.cs",
                new SandboxCommandResult(
                    0, "2222222222222222222222222222222222222222\n", string.Empty));

        var classified = await WorktreeStatusProbe.ClassifyAsync(
            new GitRunner(runner), "/store", [(" M", "src/Edited.cs")], CancellationToken.None);

        classified.Leftovers.Should().ContainSingle().Which.Should().Be(" M src/Edited.cs");
        classified.Normalized.Should().BeEmpty();
    }

    /// <summary>
    /// The tolerance is keyed on the blob, not on the status code, so it cannot be widened into "ignore
    /// modified files". An untracked leftover is contamination whatever bytes it holds, and its blob is never
    /// even consulted.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_refuses_an_untracked_leftover_without_consulting_blob_identity()
    {
        var runner = new FakeSandboxCommandRunner();

        var classified = await WorktreeStatusProbe.ClassifyAsync(
            new GitRunner(runner), "/store", [("??", "artifacts/agent-scratch.log")], CancellationToken.None);

        classified.Leftovers.Should().ContainSingle().Which.Should().Be("?? artifacts/agent-scratch.log");
        runner.Commands.Select(c => string.Join(' ', c.Argv)).Should().NotContain(
            // `rev-parse :<path>` is the FIRST of the two probes, so asserting only on `hash-object` would
            // still pass while an untracked path was being classified — the index lookup fails for it and
            // short-circuits before the second probe is ever reached.
            command => command.Contains("hash-object", StringComparison.Ordinal)
                || command.Contains("rev-parse :", StringComparison.Ordinal),
            "blob identity is only ever asked about a tracked modification");
    }

    /// <summary>
    /// The check fails CLOSED. A probe that cannot run leaves the path's identity <see cref="GitAnswer.Unknown"/>,
    /// and an unknown path stays a leftover — "I could not look" is not a finding that the bytes match.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_keeps_a_path_whose_blob_probe_cannot_run_as_a_leftover()
    {
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse :src/Unreadable.cs",
                new SandboxCommandResult(128, string.Empty, "fatal: path does not exist in the index"));

        var classified = await WorktreeStatusProbe.ClassifyAsync(
            new GitRunner(runner), "/store", [(" M", "src/Unreadable.cs")], CancellationToken.None);

        classified.Leftovers.Should().ContainSingle().Which.Should().Be(" M src/Unreadable.cs");
        classified.Normalized.Should().BeEmpty();
    }

    /// <summary>
    /// An exit-0 lookup that printed NOTHING is the same lost-output failure `-b` exists to catch one level
    /// up. Comparing an empty recorded blob against an empty on-disk one would make every path "identical",
    /// which is the one answer a check whose failure mode is silence must not produce.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_does_not_read_two_empty_answers_as_matching_blobs()
    {
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse :src/Silent.cs", new SandboxCommandResult(0, string.Empty, string.Empty))
            .OnArgvContains(
                "hash-object --no-filters -- src/Silent.cs",
                new SandboxCommandResult(0, string.Empty, string.Empty));

        var classified = await WorktreeStatusProbe.ClassifyAsync(
            new GitRunner(runner), "/store", [(" M", "src/Silent.cs")], CancellationToken.None);

        classified.Leftovers.Should().ContainSingle().Which.Should().Be(" M src/Silent.cs");
    }

    /// <summary>
    /// Above the bound the tree is wrong in bulk rather than normalized oddly, and classifying it would spend
    /// two git invocations per path to say so. Every entry stays a leftover and no probe is run at all.
    /// </summary>
    [Fact]
    public async Task ClassifyAsync_refuses_a_bulk_dirty_tree_without_probing_any_path()
    {
        var entries = Enumerable
            .Range(0, WorktreeStatusProbe.MaxClassifiedLeftovers + 1)
            .Select(i => (" M", $"src/File{i}.cs"))
            .ToList();
        var runner = new FakeSandboxCommandRunner();

        var classified = await WorktreeStatusProbe.ClassifyAsync(
            new GitRunner(runner), "/store", entries, CancellationToken.None);

        classified.Leftovers.Should().HaveCount(WorktreeStatusProbe.MaxClassifiedLeftovers + 1);
        classified.Normalized.Should().BeEmpty();
        runner.Commands.Should().BeEmpty("a tree this wrong is refused without paying to explain itself");
    }

    /// <summary>The bound is inclusive: a tree exactly at the limit is still worth classifying.</summary>
    [Fact]
    public async Task ClassifyAsync_still_classifies_a_tree_exactly_at_the_bound()
    {
        var entries = Enumerable
            .Range(0, WorktreeStatusProbe.MaxClassifiedLeftovers)
            .Select(i => (" M", $"src/File{i}.cs"))
            .ToList();
        var runner = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse :", new SandboxCommandResult(0, RecordedBlob + "\n", string.Empty))
            .OnArgvContains("hash-object", new SandboxCommandResult(0, RecordedBlob + "\n", string.Empty));

        var classified = await WorktreeStatusProbe.ClassifyAsync(
            new GitRunner(runner), "/store", entries, CancellationToken.None);

        classified.Normalized.Should().HaveCount(WorktreeStatusProbe.MaxClassifiedLeftovers);
        classified.Leftovers.Should().BeEmpty();
    }
}
