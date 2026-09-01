using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Pins <see cref="DiffCitationVerifier"/> — the primitive a synthesis/reconciliation pass would call to ask
/// "does the diff actually back this citation up" — against every outcome in <see cref="CitationOutcome"/>.
/// Two scenarios get their own focused coverage per the task this file was written for:
/// <list type="bullet">
/// <item>deleted-line freshness/expiry: a citation about removed code is only as good as the manifest's own
/// currency, which <see cref="DiffCitationVerifier.Verify"/>'s <c>expectedHeadSha</c> parameter exists to check</item>
/// <item>stacked-branch out-of-scope: a citation the diff never covers resolves <see cref="CitationOutcome.OutOfScope"/>
/// purely from manifest coverage, with no git-topology reasoning involved — see the type's own remarks</item>
/// </list>
/// </summary>
public sealed class DiffCitationVerifierTests
{
    /// <summary>
    /// One hunk: two lines of context, one deleted line (old12), two added lines (new12, new13). Built once so
    /// every outcome test resolves against the same, real parsed manifest rather than a hand-built fake.
    /// </summary>
    private static DiffManifest BuildManifest(string? headSha = "head-abc") =>
        UnifiedDiffParser.Parse(
            """
            diff --git a/src/Foo.cs b/src/Foo.cs
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -10,7 +10,8 @@ namespace Demo
             line10
             line11
            -line12 old
            +line12 new
            +line12b inserted
             line13
             line14
             line15
             line16
            """,
            baseSha: "base-abc",
            headSha: headSha
        );

    [Fact]
    public void AddedLine_OnTheNewSide_ResolvesVerifiedChangedAndIsCommentable()
    {
        var manifest = BuildManifest();

        var result = DiffCitationVerifier.Verify(manifest, new DiffCitation("src/Foo.cs", 12, DiffSide.New));

        result.Outcome.Should().Be(CitationOutcome.VerifiedChanged);
        result.IsVerified.Should().BeTrue();
        result.IsCommentable.Should().BeTrue();
        result.Evidence.Should().Be("line12 new");
        result.HunkId.Should().Be("v1:src/Foo.cs:10,7->10,8");
    }

    [Fact]
    public void DeletedLine_OnTheOldSide_ResolvesVerifiedDeletedButIsNotCommentable()
    {
        var manifest = BuildManifest();

        var result = DiffCitationVerifier.Verify(manifest, new DiffCitation("src/Foo.cs", 12, DiffSide.Old));

        result.Outcome.Should().Be(CitationOutcome.VerifiedDeleted);
        result.IsVerified.Should().BeTrue();
        result.IsCommentable.Should().BeFalse();
        // The freshness evidence a caller actually wants: what the removed line said, verbatim.
        result.Evidence.Should().Be("line12 old");
    }

    [Fact]
    public void ContextLine_ResolvesContextOnlyAndIsNeitherVerifiedNorCommentable()
    {
        var manifest = BuildManifest();

        // new-side 10 is "line10", a context line — real code the diff carries, but not part of what
        // changed.
        var result = DiffCitationVerifier.Verify(manifest, new DiffCitation("src/Foo.cs", 10, DiffSide.New));

        result.Outcome.Should().Be(CitationOutcome.ContextOnly);
        result.IsVerified.Should().BeFalse();
        result.IsCommentable.Should().BeFalse();
        result.Evidence.Should().Be("line10");
    }

    [Fact]
    public void PathNotInTheDiffAtAll_ResolvesOutOfScope_ThisIsTheStackedBranchCase()
    {
        // Models a citation about a file that lives on a sibling/stacked/ancestor branch but was never
        // touched by THIS pull request's diff. Verification never inspects branch topology — it is a pure
        // manifest-coverage check, so a path the manifest never saw is indistinguishable from, and handled
        // identically to, a typo'd path.
        var manifest = BuildManifest();

        var result = DiffCitationVerifier.Verify(manifest, new DiffCitation("src/OnAnotherBranch.cs", 1, DiffSide.New));

        result.Outcome.Should().Be(CitationOutcome.OutOfScope);
        result.IsVerified.Should().BeFalse();
        result.HunkId.Should().BeNull();
        result.Evidence.Should().BeNull();
    }

    [Fact]
    public void LineOutsideEveryHunkInAFileTheDiffDoesTouch_AlsoResolvesOutOfScope()
    {
        // Same "stacked branch" story, but the file itself IS in the diff — only this specific line is not
        // covered by any hunk (e.g. it is real code on an ancestor branch's version of this file, outside
        // what this PR's diff touched). Coverage is per-line, not per-file.
        var manifest = BuildManifest();

        var result = DiffCitationVerifier.Verify(manifest, new DiffCitation("src/Foo.cs", 9999, DiffSide.New));

        result.Outcome.Should().Be(CitationOutcome.OutOfScope);
    }

    [Fact]
    public void ExpectedHeadShaMismatch_ResolvesExpiredBeforeAnyLineLookup()
    {
        // The manifest was parsed for "head-abc"; the caller is now checking against a different head. Even
        // though line 12 (old side) really was deleted evidence in THAT manifest, the manifest itself is
        // stale for the head being asked about, so nothing about its content is trusted.
        var manifest = BuildManifest(headSha: "head-abc");

        var result = DiffCitationVerifier.Verify(
            manifest,
            new DiffCitation("src/Foo.cs", 12, DiffSide.Old),
            expectedHeadSha: "head-def"
        );

        result.Outcome.Should().Be(CitationOutcome.Expired);
        result.IsVerified.Should().BeFalse();
        result.HunkId.Should().BeNull();
        result.Evidence.Should().BeNull();
    }

    [Fact]
    public void ExpectedHeadShaMatch_ResolvesNormally()
    {
        var manifest = BuildManifest(headSha: "head-abc");

        var result = DiffCitationVerifier.Verify(
            manifest,
            new DiffCitation("src/Foo.cs", 12, DiffSide.Old),
            expectedHeadSha: "head-abc"
        );

        result.Outcome.Should().Be(CitationOutcome.VerifiedDeleted);
    }

    [Fact]
    public void NoExpectedHeadShaSupplied_SkipsTheFreshnessCheckEntirely()
    {
        var manifest = BuildManifest(headSha: "head-abc");

        var result = DiffCitationVerifier.Verify(manifest, new DiffCitation("src/Foo.cs", 12, DiffSide.Old));

        result.Outcome.Should().Be(CitationOutcome.VerifiedDeleted);
    }

    [Fact]
    public void PathMatchesBySuffixAtASegmentBoundary()
    {
        var manifest = BuildManifest();

        // A citation written relative to a different checkout root than the diff itself.
        var result = DiffCitationVerifier.Verify(manifest, new DiffCitation("checkout/src/Foo.cs", 12, DiffSide.New));

        result.Outcome.Should().Be(CitationOutcome.VerifiedChanged);
    }

    [Fact]
    public void OldSideLineOutsideTheHunksPreImageSpan_ResolvesOutOfScope()
    {
        // The hunk's pre-image span is old lines 10-16; a citation against old-side 100 has nothing to
        // match against on that side in this file, regardless of what the new side contains at that number.
        var manifest = BuildManifest();

        var result = DiffCitationVerifier.Verify(manifest, new DiffCitation("src/Foo.cs", 100, DiffSide.Old));

        result.Outcome.Should().Be(CitationOutcome.OutOfScope);
    }

    [Fact]
    public void MalformedHunkWithUnderCountedHeader_NeverFabricatesVerifiedEvidenceForSmuggledLines()
    {
        // F-005: a hunk header that under-declares its new-side count must not let the extra body lines it
        // didn't promise still resolve as verified citation evidence. UnifiedDiffManifest.Parse drops the
        // whole malformed hunk, so a citation against any of the smuggled lines resolves OutOfScope rather
        // than VerifiedChanged.
        var manifest = UnifiedDiffParser.Parse(
            """
            diff --git a/src/Smuggled.cs b/src/Smuggled.cs
            --- a/src/Smuggled.cs
            +++ b/src/Smuggled.cs
            @@ -0,0 +1,1 @@
            +line1
            +line2
            +line3
            """
        );

        var result = DiffCitationVerifier.Verify(manifest, new DiffCitation("src/Smuggled.cs", 3, DiffSide.New));

        result.Outcome.Should().Be(CitationOutcome.OutOfScope);
    }
}
