using CodeReviewDaemon.Sample.Orchestration;
using FluentAssertions;
using Xunit;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Pins <see cref="UnifiedDiffParser"/> and <see cref="DiffRiskClassifier"/> against every unified-diff shape
/// a GitHub/Azure DevOps PR diff actually presents: an ordinary modify, an added/deleted file, a rename with
/// and without a content change, and a binary file — plus the two derived views (<c>ChangedRightRanges</c>,
/// stable <c>HunkId</c>) that <see cref="DiffCitationVerifier"/> and any future caller build on. Everything
/// here is a fact about the parser's OUTPUT for a hand-written input; none of it drives real git.
/// </summary>
public sealed class UnifiedDiffManifestTests
{
    /// <summary>
    /// A single-hunk modify: 6 context + 1 deleted + 2 added lines, matching a
    /// <c>@@ -10,7 +10,8 @@</c> header exactly. The two added lines land at new-side 12 and 13
    /// (consecutive), and the one deleted line is old-side 12 — the fixture every range/id assertion below
    /// reads from.
    /// </summary>
    private const string ModifyDiff = """
        diff --git a/src/Foo.cs b/src/Foo.cs
        index 1111111..2222222 100644
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
        """;

    [Fact]
    public void Modify_ParsesOldAndNewRangesFromTheHunkHeader()
    {
        var manifest = UnifiedDiffParser.Parse(ModifyDiff);

        var file = manifest.Files.Should().ContainSingle().Subject;
        file.Path.Should().Be("src/Foo.cs");
        file.OldPath.Should().BeNull();
        file.ChangeKind.Should().Be(DiffChangeKind.Modified);
        file.IsBinary.Should().BeFalse();

        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.OldRange.Should().Be(new LineRange(10, 7));
        hunk.NewRange.Should().Be(new LineRange(10, 8));
        hunk.SectionHeading.Should().Be("namespace Demo");
    }

    [Fact]
    public void Modify_DeletedLinesCarryTheirOldLineNumberAndText()
    {
        var manifest = UnifiedDiffParser.Parse(ModifyDiff);
        var hunk = manifest.Files.Single().Hunks.Single();

        hunk.DeletedLines.Should().ContainSingle();
        hunk.DeletedLines[0].OldLineNumber.Should().Be(12);
        hunk.DeletedLines[0].Text.Should().Be("line12 old");
    }

    [Fact]
    public void Modify_ChangedRightRangesCoalesceConsecutiveAddedLinesIntoOneRange()
    {
        var manifest = UnifiedDiffParser.Parse(ModifyDiff);
        var hunk = manifest.Files.Single().Hunks.Single();

        // new12 "line12 new" and new13 "line12b inserted" are adjacent additions with nothing between them
        // on the new side, so they must be reported as one range, not two.
        hunk.ChangedRightRanges.Should().ContainSingle().Which.Should().Be(new LineRange(12, 2));
    }

    [Fact]
    public void ChangedRightRanges_InterleavedDeletionDoesNotSplitTheRun()
    {
        // Old and new each lose/gain one line with no context between them: -old1 +new1 -old2 +new2.
        // The deleted lines consume no new-side numbers, so new1 and new2 land at new-side 1 and 2 —
        // adjacent — even though an unrelated Deleted entry sits between them in the line list.
        const string diff = """
            diff --git a/src/Interleave.cs b/src/Interleave.cs
            --- a/src/Interleave.cs
            +++ b/src/Interleave.cs
            @@ -1,2 +1,2 @@
            -old1
            +new1
            -old2
            +new2
            """;

        var hunk = UnifiedDiffParser.Parse(diff).Files.Single().Hunks.Single();

        hunk.ChangedRightRanges.Should().ContainSingle().Which.Should().Be(new LineRange(1, 2));
        hunk.DeletedLines.Select(d => d.OldLineNumber).Should().Equal(1, 2);
    }

    [Fact]
    public void ChangedRightRanges_InterveningContextLineSplitsTheRun()
    {
        // +added1, then a context line, then +added3: the context line DOES consume a new-side number
        // (new2), so the two additions are not adjacent and must report as two separate ranges.
        const string diff = """
            diff --git a/src/Split.cs b/src/Split.cs
            --- a/src/Split.cs
            +++ b/src/Split.cs
            @@ -1,1 +1,3 @@
            +added1
             context1
            +added3
            """;

        var hunk = UnifiedDiffParser.Parse(diff).Files.Single().Hunks.Single();

        hunk.ChangedRightRanges.Should().Equal(new LineRange(1, 1), new LineRange(3, 1));
    }

    [Fact]
    public void HunkId_IsStableAcrossReparsesOfTheSameText()
    {
        var first = UnifiedDiffParser.Parse(ModifyDiff).Files.Single().Hunks.Single().HunkId;
        var second = UnifiedDiffParser.Parse(ModifyDiff).Files.Single().Hunks.Single().HunkId;

        first.Should().Be(second);
        first.Should().Be("v1:src/Foo.cs:10,7->10,8");
    }

    [Fact]
    public void HunkId_DiffersWhenTheHunkPositionDiffers()
    {
        const string diffAtLaterLine = """
            diff --git a/src/Foo.cs b/src/Foo.cs
            --- a/src/Foo.cs
            +++ b/src/Foo.cs
            @@ -110,7 +110,8 @@ namespace Demo
             line10
             line11
            -line12 old
            +line12 new
            +line12b inserted
             line13
             line14
             line15
             line16
            """;

        var idAt10 = UnifiedDiffParser.Parse(ModifyDiff).Files.Single().Hunks.Single().HunkId;
        var idAt110 = UnifiedDiffParser.Parse(diffAtLaterLine).Files.Single().Hunks.Single().HunkId;

        idAt10.Should().NotBe(idAt110);
    }

    [Fact]
    public void AddedFile_OldRangeIsEmptyAnchorAndOldPathIsNull()
    {
        const string diff = """
            diff --git a/src/New.cs b/src/New.cs
            new file mode 100644
            index 0000000..abc1234
            --- /dev/null
            +++ b/src/New.cs
            @@ -0,0 +1,3 @@
            +line1
            +line2
            +line3
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Path.Should().Be("src/New.cs");
        file.OldPath.Should().BeNull();
        file.ChangeKind.Should().Be(DiffChangeKind.Added);
        var hunk = file.Hunks.Single();
        hunk.OldRange.Should().Be(new LineRange(0, 0));
        hunk.NewRange.Should().Be(new LineRange(1, 3));
        hunk.DeletedLines.Should().BeEmpty();
    }

    [Fact]
    public void DeletedFile_PathComesFromThePreImageAndNewRangeIsEmptyAnchor()
    {
        const string diff = """
            diff --git a/src/Old.cs b/src/Old.cs
            deleted file mode 100644
            index abc1234..0000000
            --- a/src/Old.cs
            +++ /dev/null
            @@ -1,3 +0,0 @@
            -line1
            -line2
            -line3
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Path.Should().Be("src/Old.cs");
        file.OldPath.Should().BeNull();
        file.ChangeKind.Should().Be(DiffChangeKind.Deleted);
        var hunk = file.Hunks.Single();
        hunk.NewRange.Should().Be(new LineRange(0, 0));
        hunk.DeletedLines.Should().HaveCount(3);
    }

    [Fact]
    public void RenamedFileWithContentChange_CarriesBothPathsAndModifiedHunk()
    {
        const string diff = """
            diff --git a/src/Old.cs b/src/New.cs
            similarity index 92%
            rename from src/Old.cs
            rename to src/New.cs
            index abc1234..def5678 100644
            --- a/src/Old.cs
            +++ b/src/New.cs
            @@ -1,2 +1,2 @@
            -old line
            +new line
             context
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Path.Should().Be("src/New.cs");
        file.OldPath.Should().Be("src/Old.cs");
        file.ChangeKind.Should().Be(DiffChangeKind.Renamed);
        file.Hunks.Should().ContainSingle();
    }

    [Fact]
    public void PureRenameWithNoContentChange_HasNoHunksButStillCarriesBothPaths()
    {
        // 100%-similarity rename: git emits no "--- "/"+++ " pair at all in this case, so the path has to
        // come off the "diff --git"/"rename from"/"rename to" lines alone.
        const string diff = """
            diff --git a/src/Old2.cs b/src/New2.cs
            similarity index 100%
            rename from src/Old2.cs
            rename to src/New2.cs
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Path.Should().Be("src/New2.cs");
        file.OldPath.Should().Be("src/Old2.cs");
        file.ChangeKind.Should().Be(DiffChangeKind.Renamed);
        file.Hunks.Should().BeEmpty();
        file.IsBinary.Should().BeFalse();
    }

    [Fact]
    public void BinaryFile_IsFlaggedAndCarriesItsPathWithNoHunks()
    {
        // Binary files get no "--- "/"+++ " pair either; the path has to come off "diff --git" alone.
        const string diff = """
            diff --git a/assets/image.png b/assets/image.png
            index abc1234..def5678 100644
            Binary files a/assets/image.png and b/assets/image.png differ
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Path.Should().Be("assets/image.png");
        file.IsBinary.Should().BeTrue();
        file.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void MultiFileDiff_ProducesOneEntryPerFileInOrder()
    {
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            diff --git a/src/B.cs b/src/B.cs
            --- a/src/B.cs
            +++ b/src/B.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var manifest = UnifiedDiffParser.Parse(diff);

        manifest.Files.Select(f => f.Path).Should().Equal("src/A.cs", "src/B.cs");
    }

    [Fact]
    public void NoNewlineAtEndOfFileMarker_IsSkippedWithoutAffectingLineNumbers()
    {
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            \ No newline at end of file
            """;

        var hunk = UnifiedDiffParser.Parse(diff).Files.Single().Hunks.Single();

        hunk.Lines.Should().HaveCount(2);
        hunk.ChangedRightRanges.Should().ContainSingle().Which.Should().Be(new LineRange(1, 1));
    }

    [Fact]
    public void NullOrEmptyDiffText_ReturnsEmptyManifestWithoutThrowing()
    {
        UnifiedDiffParser.Parse(null).Files.Should().BeEmpty();
        UnifiedDiffParser.Parse(string.Empty).Files.Should().BeEmpty();
    }

    [Fact]
    public void GarbledInput_NeverThrowsAndSkipsWhatItDoesNotRecognise()
    {
        const string garbage = """
            this is not a diff at all
            @@ nonsense @@
            random binary junk \x00\x01
            """;

        var act = () => UnifiedDiffParser.Parse(garbage);

        act.Should().NotThrow();
        act().Files.Should().BeEmpty();
    }

    [Fact]
    public void FindFile_MatchesExactPostImagePath()
    {
        var manifest = UnifiedDiffParser.Parse(ModifyDiff);

        manifest.FindFile("src/Foo.cs").Should().NotBeNull();
    }

    [Fact]
    public void FindFile_MatchesByPathSuffixAtASegmentBoundary()
    {
        var manifest = UnifiedDiffParser.Parse(ModifyDiff);

        // A finding written relative to a submodule/checkout root ("Foo.cs" alone, or a longer prefixed
        // variant) should still resolve to the one file the diff carries.
        manifest.FindFile("Foo.cs").Should().NotBeNull();
        manifest.FindFile("repo/checkout/src/Foo.cs").Should().NotBeNull();
    }

    [Fact]
    public void FindFile_ReturnsNullForAPathTheDiffNeverTouched()
    {
        var manifest = UnifiedDiffParser.Parse(ModifyDiff);

        manifest.FindFile("src/Unrelated.cs").Should().BeNull();
        manifest.FindFile(null).Should().BeNull();
    }

    [Fact]
    public void Classification_FlagsSecurityKeywordInAnAddedLine()
    {
        const string diff = """
            diff --git a/src/Login.cs b/src/Login.cs
            --- a/src/Login.cs
            +++ b/src/Login.cs
            @@ -1,1 +1,1 @@
            -var ok = true;
            +var ok = (password == storedValue);
            """;

        var hunk = UnifiedDiffParser.Parse(diff).Files.Single().Hunks.Single();

        hunk.Classification.Should().HaveFlag(DiffRiskClassification.Security);
    }

    [Fact]
    public void Classification_FlagsInvariantKeywordEvenWhenOnlyTheDeletedLineHasIt()
    {
        // Removing a guard is exactly as relevant as adding one — the classifier must not require the
        // keyword to appear on the added side. Path deliberately avoids the word "guard" itself, so this
        // pins the LINE-text match rather than the path-hint match already covered elsewhere.
        const string diff = """
            diff --git a/src/Feature.cs b/src/Feature.cs
            --- a/src/Feature.cs
            +++ b/src/Feature.cs
            @@ -1,1 +1,1 @@
            -ArgumentNullException.ThrowIfNull(value);
            +DoNothing(value);
            """;

        var hunk = UnifiedDiffParser.Parse(diff).Files.Single().Hunks.Single();

        hunk.Classification.Should().HaveFlag(DiffRiskClassification.Invariant);
    }

    [Fact]
    public void Classification_IsNoneWhenNothingMatchesEitherVocabulary()
    {
        const string diff = """
            diff --git a/src/Plain.cs b/src/Plain.cs
            --- a/src/Plain.cs
            +++ b/src/Plain.cs
            @@ -1,1 +1,1 @@
            -var total = a + b;
            +var total = a + b + c;
            """;

        var hunk = UnifiedDiffParser.Parse(diff).Files.Single().Hunks.Single();

        hunk.Classification.Should().Be(DiffRiskClassification.None);
    }

    [Fact]
    public void Classification_FlagsSecurityFromThePathAloneWhenLinesDoNotMatch()
    {
        const string diff = """
            diff --git a/src/Security/Policy.cs b/src/Security/Policy.cs
            --- a/src/Security/Policy.cs
            +++ b/src/Security/Policy.cs
            @@ -1,1 +1,1 @@
            -var x = 1;
            +var x = 2;
            """;

        var hunk = UnifiedDiffParser.Parse(diff).Files.Single().Hunks.Single();

        hunk.Classification.Should().HaveFlag(DiffRiskClassification.Security);
    }
}
