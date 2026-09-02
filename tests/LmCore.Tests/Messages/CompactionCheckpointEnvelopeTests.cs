namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

/// <summary>
///     Pins the envelope a checkpoint is rendered into for the model (#683; spec 679 §2.3): section
///     order, <c>Current instruction</c> first and omitted when empty, every human quote carrying its
///     <c>seq</c>, and the recall hint only when the host says which tool answers it.
/// </summary>
public class CompactionCheckpointEnvelopeTests
{
    private static CompactionCheckpointMessage Checkpoint(ContextManifest manifest) =>
        new()
        {
            CheckpointId = "cp-t-1",
            Boundary = new CheckpointBoundary { Seq = 60, MessageId = "row-60" },
            Trigger = CompactionTrigger.Preemptive,
            Manifest = manifest,
            Narrative = "Ran the tests, then fixed the build.",
            CreatedAtUtc = new DateTimeOffset(2026, 9, 2, 12, 0, 0, TimeSpan.Zero),
        };

    private static ContextManifest FullManifest() =>
        new()
        {
            CurrentInstruction = [new QuotedItem { Seq = 3, Quote = "fix the flaky test\nthen report" }],
            Instructions = [new QuotedItem { Seq = 1, Quote = "never push" }],
            Goals = ["green CI"],
            Decisions = [new QuotedItem { Seq = 2, Quote = "approved: delete the flag" }],
            Tasks =
            [
                new TaskRef
                {
                    Id = "1",
                    Title = "fix it",
                    Status = "InProgress",
                },
            ],
            Artifacts = [new ArtifactRef { Path = "src/a.cs", OriginSeq = 20 }],
            Agents =
            [
                new AgentRef
                {
                    AgentId = "agent-1",
                    Status = "Completed",
                    Outcome = "done",
                },
            ],
            Index =
            [
                new IndexEntry
                {
                    FromSeq = 1,
                    ToSeq = 60,
                    RunId = "run-1",
                    Headline = "the fix",
                },
            ],
        };

    private static List<string> Headings(string text) =>
        [.. text.Split('\n').Where(l => l.StartsWith("## ", StringComparison.Ordinal))];

    [Fact]
    public void Sections_RenderInSpecOrder_WithCurrentInstructionFirst()
    {
        var text = Checkpoint(FullManifest()).RenderEnvelope(CheckpointRenderOptions.Default);

        Assert.Equal(
            [
                "## Current instruction (verbatim, seq 3)",
                "## Standing instructions (verbatim, oldest first)",
                "## Goal and acceptance criteria",
                "## Decisions and approvals",
                "## Open work",
                "## Artifacts and evidence",
                "## Agents",
                "## What happened",
                "## Index of compacted history",
            ],
            Headings(text)
        );
        Assert.StartsWith(
            "<context-checkpoint version=\"1\" id=\"cp-t-1\" covers_seq=\"1-60\" created_at=\"2026-09-02T12:00:00.0000000+00:00\">\n",
            text
        );
        Assert.EndsWith("</context-checkpoint>", text);
    }

    [Fact]
    public void CurrentInstruction_QuotesTheWholeRowVerbatim_WithItsSeq()
    {
        var text = Checkpoint(FullManifest()).RenderEnvelope(CheckpointRenderOptions.Default);

        Assert.Contains("## Current instruction (verbatim, seq 3)\n- [seq 3] fix the flaky test\nthen report\n", text);
    }

    [Fact]
    public void CurrentInstruction_IsOmittedWhenEmpty_AndStandingInstructionsLeadInstead()
    {
        var manifest = FullManifest() with { CurrentInstruction = [] };

        var text = Checkpoint(manifest).RenderEnvelope(CheckpointRenderOptions.Default);

        Assert.DoesNotContain("Current instruction", text);
        Assert.Equal("## Standing instructions (verbatim, oldest first)", Headings(text)[0]);
    }

    [Fact]
    public void SeveralCurrentInstructionRows_ListEverySeqInTheHeading_InSeqOrder()
    {
        var manifest = FullManifest() with
        {
            CurrentInstruction =
            [
                new QuotedItem { Seq = 3, Quote = "start" },
                new QuotedItem { Seq = 30, Quote = "also do this" },
            ],
        };

        var text = Checkpoint(manifest).RenderEnvelope(CheckpointRenderOptions.Default);

        Assert.Contains(
            "## Current instruction (verbatim, seq 3, 30)\n- [seq 3] start\n- [seq 30] also do this\n",
            text
        );
    }

    [Fact]
    public void EmptySections_AreOmitted_ButTheNarrativeAlwaysRenders()
    {
        var text = Checkpoint(new ContextManifest()).RenderEnvelope(CheckpointRenderOptions.Default);

        Assert.Equal(["## What happened"], Headings(text));
        Assert.Contains("## What happened\nRan the tests, then fixed the build.\n", text);
    }

    [Fact]
    public void RecallHint_RendersOnlyWhenTheHostNamesTheTool()
    {
        var checkpoint = Checkpoint(FullManifest());

        var silent = checkpoint.RenderEnvelope(CheckpointRenderOptions.Default);
        var withTool = checkpoint.RenderEnvelope(new CheckpointRenderOptions { RecallToolName = "RecallConversation" });

        Assert.DoesNotContain("RecallConversation", silent);
        Assert.EndsWith(
            "Use RecallConversation to read any compacted range verbatim.\n</context-checkpoint>",
            withTool
        );
    }

    [Fact]
    public void Text_IsTheDefaultRendering_SoTheMirrorAndTheRequestAgree()
    {
        var checkpoint = Checkpoint(FullManifest());

        Assert.Equal(checkpoint.RenderEnvelope(CheckpointRenderOptions.Default), checkpoint.Text);
    }
}
