using CodeReviewDaemon.Sample.Orchestration;

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
    public void HunkBody_LineContentStartingWithHeaderMarkerLikeText_IsPreservedNotMisreadAsAFileHeader()
    {
        // F-002 regression: a deleted/added line whose OWN content begins with "-- "/"++ " renders, once
        // its diff marker is prepended, as "--- "/"+++ " — indistinguishable from a real file-header line
        // unless recognition is gated on whether a hunk is currently active.
        const string diff = """
            diff --git a/src/Marker.cs b/src/Marker.cs
            --- a/src/Marker.cs
            +++ b/src/Marker.cs
            @@ -1,2 +1,2 @@
             context
            --- deleted marker-like line
            +++ added marker-like line
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        // The file's real path must survive — a naive "+++ " match would have overwritten it with
        // "added marker-like line".
        file.Path.Should().Be("src/Marker.cs");

        var hunk = file.Hunks.Single();
        hunk.Lines.Should().HaveCount(3);
        hunk.DeletedLines.Should().ContainSingle().Which.Text.Should().Be("-- deleted marker-like line");
        hunk.Lines[2].Kind.Should().Be(DiffLineKind.Added);
        hunk.Lines[2].Text.Should().Be("++ added marker-like line");
    }

    [Fact]
    public void HunkHeader_OverflowingCoordinate_NeverThrowsAndSkipsOnlyThatHunk()
    {
        // F-001 regression: hunk coordinates are matched by \d+ — an unbounded digit run — so a header
        // presenting more digits than an int can hold must be rejected rather than throwing OverflowException.
        const string diff = """
            diff --git a/src/Big.cs b/src/Big.cs
            --- a/src/Big.cs
            +++ b/src/Big.cs
            @@ -99999999999999999999,1 +1,1 @@
            -bad old
            +bad new
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var act = () => UnifiedDiffParser.Parse(diff);
        act.Should().NotThrow();

        var file = act().Files.Single();

        // Only the malformed hunk is dropped; the well-formed one that follows still parses.
        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.OldRange.Should().Be(new LineRange(1, 1));
        hunk.Lines.Should().HaveCount(2);
    }

    [Fact]
    public void HunkHeader_StartPlusCountOverflow_IsRejectedRatherThanWrapping()
    {
        // Both coordinates individually parse as valid ints, but Start + Count - 1 would overflow — the
        // "unchecked LineRange.End arithmetic can wrap" half of F-001.
        const string diff = """
            diff --git a/src/Overflow.cs b/src/Overflow.cs
            --- a/src/Overflow.cs
            +++ b/src/Overflow.cs
            @@ -2147483646,5 +1,1 @@
            -old
            +new
            """;

        var act = () => UnifiedDiffParser.Parse(diff);

        act.Should().NotThrow();
        act().Files.Single().Hunks.Should().BeEmpty();
    }

    [Fact]
    public void LineRange_EndDoesNotWrapWhenStartPlusCountOverflowsInt()
    {
        // Defense in depth for F-001: even if something else ever constructs a LineRange from extreme
        // values, the endpoint must clamp rather than silently wrap into a negative number.
        var range = new LineRange(int.MaxValue - 1, 5);

        range.End.Should().Be(int.MaxValue);
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

    /// <summary>Two files whose paths differ only by case — a legal, distinct pair on a case-sensitive
    /// repository. F-004 regression fixture for both the exact-match and ambiguity halves of the fix.</summary>
    private const string CaseDistinctDiff = """
        diff --git a/src/Foo.cs b/src/Foo.cs
        --- a/src/Foo.cs
        +++ b/src/Foo.cs
        @@ -1,1 +1,1 @@
        -old upper
        +new upper
        diff --git a/src/foo.cs b/src/foo.cs
        --- a/src/foo.cs
        +++ b/src/foo.cs
        @@ -1,1 +1,1 @@
        -old lower
        +new lower
        """;

    [Fact]
    public void FindFile_ExactCaseMatchWinsOverASameNamedFileDifferingOnlyByCase()
    {
        var manifest = UnifiedDiffParser.Parse(CaseDistinctDiff);

        manifest.FindFile("src/Foo.cs")!.Hunks.Single().DeletedLines.Single().Text.Should().Be("old upper");
        manifest.FindFile("src/foo.cs")!.Hunks.Single().DeletedLines.Single().Text.Should().Be("old lower");
    }

    [Fact]
    public void FindFile_CaseInsensitiveMatchIsRejectedWhenTwoFilesOnlyDifferByCase()
    {
        // No exact-case match for "FOO.cs" itself, and the case-insensitive fallback now has two equally
        // plausible candidates (Foo.cs and foo.cs) — that ambiguity must not resolve to either one.
        var manifest = UnifiedDiffParser.Parse(CaseDistinctDiff);

        manifest.FindFile("src/FOO.cs").Should().BeNull();
    }

    [Fact]
    public void FindFile_SuffixMatchIsRejectedWhenTwoFilesShareTheSameSuffix()
    {
        // F-004 regression: two unrelated files sharing a path suffix must not let a suffix-relative
        // citation silently resolve to whichever one this parser happened to see first.
        const string diff = """
            diff --git a/moduleA/src/Util.cs b/moduleA/src/Util.cs
            --- a/moduleA/src/Util.cs
            +++ b/moduleA/src/Util.cs
            @@ -1,1 +1,1 @@
            -old a
            +new a
            diff --git a/moduleB/src/Util.cs b/moduleB/src/Util.cs
            --- a/moduleB/src/Util.cs
            +++ b/moduleB/src/Util.cs
            @@ -1,1 +1,1 @@
            -old b
            +new b
            """;

        var manifest = UnifiedDiffParser.Parse(diff);

        manifest.FindFile("src/Util.cs").Should().BeNull();
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

    [Fact]
    public void HunkHeader_UnderCountedNewSide_DropsTheHunkRatherThanFabricatingExtraLines()
    {
        // F-005: the header declares only 1 new-side row, but the body actually carries 5 added lines
        // before the diff ends. Left unchecked, those extra lines would still be assigned new-side line
        // numbers and would resolve as verifiable citation evidence even though the header never promised
        // them. The whole hunk must be dropped instead of trusting the body over the header.
        const string diff = """
            diff --git a/src/Under.cs b/src/Under.cs
            --- a/src/Under.cs
            +++ b/src/Under.cs
            @@ -0,0 +1,1 @@
            +line1
            +line2
            +line3
            +line4
            +line5
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void HunkHeader_OverCountedOldSide_DropsTheHunkRatherThanOverstatingCoverage()
    {
        // F-005: the header declares 5 old-side rows, but the body only carries 1 deleted line before the
        // diff ends. The declared span overstates what this hunk actually covers, so it must be dropped
        // rather than kept with a range nothing in the body backs up.
        const string diff = """
            diff --git a/src/Over.cs b/src/Over.cs
            --- a/src/Over.cs
            +++ b/src/Over.cs
            @@ -1,5 +1,1 @@
            -old
            +new
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void HunkHeader_SideMismatch_DropsTheHunkWhenOldAndNewCountsAreBothInconsistentWithConsumption()
    {
        // F-005: header declares old=2/new=2, but the body only carries 1 old-side row (the context line)
        // against 3 new-side rows (context + two adds) — neither side reconciles, a stronger malformation
        // than a one-sided under/over count.
        const string diff = """
            diff --git a/src/Mismatch.cs b/src/Mismatch.cs
            --- a/src/Mismatch.cs
            +++ b/src/Mismatch.cs
            @@ -1,2 +1,2 @@
             context1
            +added1
            +added2
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void HunkHeader_ZeroCountPureAddition_IsStillAcceptedAfterReconciliation()
    {
        // F-005 must not regress the legitimate zero-count case: a pure addition declares 0 old-side rows
        // and some added lines. This has to keep parsing exactly as before.
        const string diff = """
            diff --git a/src/New.cs b/src/New.cs
            --- /dev/null
            +++ b/src/New.cs
            @@ -0,0 +1,2 @@
            +line1
            +line2
            """;

        var hunk = UnifiedDiffParser.Parse(diff).Files.Single().Hunks.Single();

        hunk.OldRange.Count.Should().Be(0);
        hunk.NewRange.Count.Should().Be(2);
    }

    [Fact]
    public void HunkHeader_ZeroCountPureDeletion_IsStillAcceptedAfterReconciliation()
    {
        // Mirror of the pure-addition case: 0 new-side rows, some deleted lines.
        const string diff = """
            diff --git a/src/Gone.cs b/src/Gone.cs
            --- a/src/Gone.cs
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -line1
            -line2
            """;

        var hunk = UnifiedDiffParser.Parse(diff).Files.Single().Hunks.Single();

        hunk.OldRange.Count.Should().Be(2);
        hunk.NewRange.Count.Should().Be(0);
    }

    [Fact]
    public void HunkHeader_NonEmptyOldRangeStartingAtLineZero_IsRejected()
    {
        // F-001 residual: unified-diff line numbers are 1-based, so a non-empty old-side range (count > 0)
        // starting at line 0 is malformed. The body below is deliberately built to reconcile exactly against
        // the declared counts (F-005 would accept it), isolating that rejection here comes from the
        // start-0 check specifically, not from a declared/consumed mismatch.
        const string diff = """
            diff --git a/src/OldZero.cs b/src/OldZero.cs
            --- a/src/OldZero.cs
            +++ b/src/OldZero.cs
            @@ -0,2 +1,2 @@
             context1
             context2
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void HunkHeader_NonEmptyNewRangeStartingAtLineZero_IsRejected()
    {
        // Mirror of the old-side case: the new-side range is the one that is non-empty and zero-started.
        const string diff = """
            diff --git a/src/NewZero.cs b/src/NewZero.cs
            --- a/src/NewZero.cs
            +++ b/src/NewZero.cs
            @@ -1,2 +0,2 @@
             context1
             context2
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void HunkHeader_ImplicitCountOneStartingAtLineZero_IsRejected()
    {
        // A header with no explicit ",count" defaults to count 1 (git omits the count when it is 1) — still
        // a non-empty range, so a zero start must be rejected exactly as it would be with an explicit count.
        const string diff = """
            diff --git a/src/ImplicitZero.cs b/src/ImplicitZero.cs
            --- a/src/ImplicitZero.cs
            +++ b/src/ImplicitZero.cs
            @@ -0 +1 @@
            +line1
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Hunks.Should().BeEmpty();
    }

    [Fact]
    public void RejectedHunkHeader_QuarantinesBodySoMarkerLikeLinesCannotForgeFileIdentityAndLaterHunkStaysAttributed()
    {
        // F-004: a hunk header that is syntactically shaped ("@@ ... @@" matches) but numerically rejected —
        // here, a non-empty old-range starting at line 0, the same F-001 residual fixed above — leaves no
        // active HunkBuilder for its body to fall into. Without quarantining that body, "--- "/"+++ "-shaped
        // content is exactly what a citation-verifying caller must not be able to smuggle: it would be read
        // as a genuine file-header pair (the same class of collision the F-002 fix guards against once a
        // hunk IS active) and silently retarget the file this manifest attributes everything else to.
        const string diff = """
            diff --git a/src/Real.cs b/src/Real.cs
            --- a/src/Real.cs
            +++ b/src/Real.cs
            @@ -0,2 +1,2 @@
            --- forged/Old.cs
            +++ forged/New.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        // The genuine "--- "/"+++ " pair from before the rejected hunk must survive its quarantined body.
        file.Path.Should().Be("src/Real.cs");
        file.OldPath.Should().BeNull();

        // Only the well-formed trailing hunk parses, and it is attributed to the correct (unforged) file —
        // the citation-attribution guarantee a downstream verifier relies on.
        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.HunkId.Should().Be("v1:src/Real.cs:1,1->1,1");
        hunk.DeletedLines.Should().ContainSingle().Which.Text.Should().Be("old");
    }

    [Fact]
    public void RejectedHunkHeader_QuarantineDoesNotLeakAcrossAFileBoundary()
    {
        // The quarantine flag is per-hunk-header, not per-file: a rejected header with no subsequent valid
        // hunk in the SAME file must not suppress genuine "--- "/"+++ " recognition for the NEXT file's
        // header, once a "diff --git" boundary is crossed.
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -0,1 +1,1 @@
            --- forged/path
            diff --git a/src/B.cs b/src/B.cs
            --- a/src/B.cs
            +++ b/src/B.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var manifest = UnifiedDiffParser.Parse(diff);

        var fileA = manifest.Files[0];
        fileA.Path.Should().Be("src/A.cs");
        fileA.Hunks.Should().BeEmpty();

        var fileB = manifest.Files[1];
        fileB.Path.Should().Be("src/B.cs");
        var hunk = fileB.Hunks.Should().ContainSingle().Subject;
        hunk.HunkId.Should().Be("v1:src/B.cs:1,1->1,1");
    }

    // F-010: the quarantine established for F-004 only gated the "--- "/"+++ " pair. Every other file-metadata
    // handler ("Binary files ", "rename from "/"rename to ", "copy from "/"copy to ", "new file mode"/
    // "deleted file mode") was still unconditional, so a rejected hunk header's quarantined body shaped like
    // any of them could still mutate the file's identity, change kind, or binary flag. Each test below plants
    // exactly one such forged line inside a rejected hunk's body and asserts (a) the genuine header's metadata
    // survives unmutated, and (b) a trailing well-formed hunk still parses and attributes to the right file.

    [Fact]
    public void RejectedHunkHeader_QuarantinesBodySoBinaryFilesLineCannotForgeABinaryFlag()
    {
        const string diff = """
            diff --git a/src/Real2.cs b/src/Real2.cs
            --- a/src/Real2.cs
            +++ b/src/Real2.cs
            @@ -0,1 +1,1 @@
            Binary files a/forged and b/forged differ
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.IsBinary.Should().BeFalse();
        file.Path.Should().Be("src/Real2.cs");
        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.HunkId.Should().Be("v1:src/Real2.cs:1,1->1,1");
    }

    [Fact]
    public void RejectedHunkHeader_QuarantinesBodySoRenameFromLineCannotForgeAnOldPath()
    {
        const string diff = """
            diff --git a/src/Real3.cs b/src/Real3.cs
            --- a/src/Real3.cs
            +++ b/src/Real3.cs
            @@ -0,1 +1,1 @@
            rename from forged/Old.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.OldPath.Should().BeNull();
        file.ChangeKind.Should().Be(DiffChangeKind.Modified);
        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.HunkId.Should().Be("v1:src/Real3.cs:1,1->1,1");
    }

    [Fact]
    public void RejectedHunkHeader_QuarantinesBodySoRenameToLineCannotForgeAPath()
    {
        const string diff = """
            diff --git a/src/Real4.cs b/src/Real4.cs
            --- a/src/Real4.cs
            +++ b/src/Real4.cs
            @@ -0,1 +1,1 @@
            rename to forged/New.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Path.Should().Be("src/Real4.cs");
        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.HunkId.Should().Be("v1:src/Real4.cs:1,1->1,1");
    }

    [Fact]
    public void RejectedHunkHeader_QuarantinesBodySoCopyFromLineCannotForgeAnOldPath()
    {
        const string diff = """
            diff --git a/src/Real5.cs b/src/Real5.cs
            --- a/src/Real5.cs
            +++ b/src/Real5.cs
            @@ -0,1 +1,1 @@
            copy from forged/Old.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.OldPath.Should().BeNull();
        file.ChangeKind.Should().Be(DiffChangeKind.Modified);
        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.HunkId.Should().Be("v1:src/Real5.cs:1,1->1,1");
    }

    [Fact]
    public void RejectedHunkHeader_QuarantinesBodySoCopyToLineCannotForgeAPath()
    {
        const string diff = """
            diff --git a/src/Real6.cs b/src/Real6.cs
            --- a/src/Real6.cs
            +++ b/src/Real6.cs
            @@ -0,1 +1,1 @@
            copy to forged/New.cs
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.Path.Should().Be("src/Real6.cs");
        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.HunkId.Should().Be("v1:src/Real6.cs:1,1->1,1");
    }

    [Fact]
    public void RejectedHunkHeader_QuarantinesBodySoNewFileModeLineCannotForgeAnAddedChangeKind()
    {
        const string diff = """
            diff --git a/src/Real7.cs b/src/Real7.cs
            --- a/src/Real7.cs
            +++ b/src/Real7.cs
            @@ -0,1 +1,1 @@
            new file mode 100644
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.ChangeKind.Should().Be(DiffChangeKind.Modified);
        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.HunkId.Should().Be("v1:src/Real7.cs:1,1->1,1");
    }

    [Fact]
    public void RejectedHunkHeader_QuarantinesBodySoDeletedFileModeLineCannotForgeADeletedChangeKind()
    {
        const string diff = """
            diff --git a/src/Real8.cs b/src/Real8.cs
            --- a/src/Real8.cs
            +++ b/src/Real8.cs
            @@ -0,1 +1,1 @@
            deleted file mode 100644
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var file = UnifiedDiffParser.Parse(diff).Files.Single();

        file.ChangeKind.Should().Be(DiffChangeKind.Modified);
        var hunk = file.Hunks.Should().ContainSingle().Subject;
        hunk.HunkId.Should().Be("v1:src/Real8.cs:1,1->1,1");
    }
}
