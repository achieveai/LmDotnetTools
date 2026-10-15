using System.Globalization;
using System.Text.Json.Nodes;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Which of the four scoped, one-shot CLI surfaces (if any) <see cref="ScopedCommandLine.Parse"/>
/// recognized.</summary>
internal enum ScopedCommandKind
{
    /// <summary>Not a scoped command at all — falls through to normal daemon startup
    /// (<c>ReviewProfileArgs.Extract</c> + <c>app.Run()</c>).</summary>
    None,

    /// <summary><c>--workflow-operation &lt;name&gt;</c>.</summary>
    WorkflowOperation,

    /// <summary><c>--list-candidate-prs</c>.</summary>
    ListCandidatePrs,

    /// <summary>Read-only inventory of existing review-artifact branches and their source PR lifecycle.</summary>
    ListReviewBranches,

    /// <summary><c>--run-pr &lt;repoKey&gt; &lt;prId&gt; &lt;approvedHead&gt; &lt;approvedBase&gt;</c>.</summary>
    RunPr,

    /// <summary>Bounded exact-PR requests supplied as JSON lines on stdin.</summary>
    RunPrStream,

    /// <summary>One exact branch-backed merged completion, without polling.</summary>
    CompleteMergedPr,

    /// <summary>A finite explicit list of merged completions under one coordinator.</summary>
    CompleteMergedBatch,

    /// <summary>Read-only inspection of an exact branch for archive-only completion.</summary>
    InspectMergedArchive,

    /// <summary>Explicit exact-branch archive with a separate publish-and-delete confirmation.</summary>
    ArchiveMergedPr,

    /// <summary>Retain missing task notes for an exact completed run without rerunning its review.</summary>
    RetainReviewNotes,

    /// <summary>Inspect or explicitly reset one incomplete review's occupied slot.</summary>
    ResetReviewRun,

    /// <summary>Inspect one round-owned slot; confirmation refuses until a safe reset exists.</summary>
    ResetReviewRound,

    /// <summary>
    /// <c>--redo-artifact-branch &lt;repoKey&gt; &lt;prId&gt;</c> (task #82, requirement 4) — the explicit,
    /// operator-only, deletion-only redo. Parsed here rather than by its own <c>args is [...]</c> match so a
    /// fat-fingered redo is a usage error on the same path as every other scoped command instead of falling
    /// through into normal daemon startup.
    /// </summary>
    RedoArtifactBranch,

    /// <summary>
    /// The first argument names a scoped command, but the rest of the argument list does not match its
    /// required arity. This must be a usage failure, not a silent fall-through into
    /// <c>ReviewProfileArgs.Extract</c>/normal daemon startup — an operator who fat-fingers <c>--run-pr</c>'s
    /// four arguments must be told so, not hand the daemon an unrelated set of "review args" and have it boot
    /// the poller instead.
    /// </summary>
    Malformed,
}

/// <summary>
/// Parses <c>Program.Main</c>'s raw <c>args</c> into exactly one of: not-a-scoped-command, one of the four
/// well-formed scoped commands (task #81 plan, plus task #82's redo), or malformed. Pure and DI-free by design — <c>Program.cs</c>
/// must decide (and, on <see cref="ScopedCommandKind.Malformed"/>, exit) before it builds any configuration,
/// host, or provider, so this parse has to be checkable on its own, with no host required to exercise it.
/// </summary>
internal static class ScopedCommandLine
{
    private static readonly string[] KnownPrefixes =
    [
        "--workflow-operation",
        "--list-candidate-prs",
        "--list-review-branches",
        "--run-pr",
        "--run-pr-stream",
        "--complete-merged-pr",
        "--complete-merged-batch",
        "--inspect-merged-archive",
        "--archive-merged-pr",
        "--retain-review-notes",
        "--reset-review-run",
        "--reset-review-round",
        "--redo-artifact-branch",
    ];

    public static bool ContainsScopedCommand(string[] args) => args.Any(arg => KnownPrefixes.Contains(arg));

    public static IReadOnlyList<ScopedCommandLineResult> ReadMergedBatch(string path)
    {
        using var reader = File.OpenText(path);
        var buffer = new char[65537];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        if (length == buffer.Length)
            throw new InvalidDataException("Merged batch exceeds 64 KiB of text.");
        if (JsonNode.Parse(new string(buffer, 0, length)) is not JsonArray { Count: >= 1 and <= 18 } entries)
            throw new InvalidDataException("Merged batch requires 1–18 explicit requests.");
        var commands = new List<ScopedCommandLineResult>();
        foreach (var entry in entries)
        {
            if (
                entry is not JsonObject item
                || item.Count != 7
                || item.Any(pair =>
                    pair.Key
                        is not (
                            "repository"
                            or "prId"
                            or "artifactTipSha"
                            or "landedSha"
                            or "parentSha"
                            or "slot"
                            or "resume"
                        )
                )
                || item["slot"] is not JsonValue slotValue
                || !slotValue.TryGetValue<int>(out var slot)
                || slot is < 0 or > 5
                || item["resume"] is not JsonValue resumeValue
                || !resumeValue.TryGetValue<bool>(out var resume)
            )
                throw new InvalidDataException("Merged batch request has an invalid shape, slot or resume flag.");
            string Text(string key) =>
                item[key]?.GetValue<string>() ?? throw new InvalidDataException("Missing merged identity.");
            var command = Parse([
                "--complete-merged-pr",
                Text("repository"),
                Text("prId"),
                Text("artifactTipSha"),
                Text("landedSha"),
                Text("parentSha"),
                "--slot",
                slot.ToString(CultureInfo.InvariantCulture),
            ]);
            if (
                command.Kind != ScopedCommandKind.CompleteMergedPr
                || string.IsNullOrWhiteSpace(command.RunPrRepoKey)
                || command.RunPrId
                    != int.Parse(command.RunPrId!, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
            )
                throw new InvalidDataException(
                    "Merged batch requires canonical repository and positive PR identities."
                );
            commands.Add(command with { ResumeMerged = resume });
        }
        return commands;
    }

    public static ScopedCommandLineResult Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args is ["--workflow-operation", var operation])
        {
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.WorkflowOperation,
                WorkflowOperation = operation,
            };
        }

        if (args is ["--list-review-branches"])
            return new ScopedCommandLineResult { Kind = ScopedCommandKind.ListReviewBranches };

        if (args is ["--list-candidate-prs"])
        {
            return new ScopedCommandLineResult { Kind = ScopedCommandKind.ListCandidatePrs };
        }

        if (args is ["--retain-review-notes", var notesRunId] && long.TryParse(notesRunId, out var runId) && runId > 0)
            return new ScopedCommandLineResult { Kind = ScopedCommandKind.RetainReviewNotes, NotesRunId = runId };

        var confirmReset = args is ["--reset-review-run", _, "--confirm"];
        var resetArgs = confirmReset ? args[..^1] : args;
        if (
            resetArgs is ["--reset-review-run", var resetId]
            && long.TryParse(resetId, out var resetRunId)
            && resetRunId > 0
        )
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.ResetReviewRun,
                ResetRunId = resetRunId,
                ConfirmReset = confirmReset,
            };

        var confirmRoundReset = args is ["--reset-review-round", _, "--receipt", _, "--confirm"];
        var roundResetArgs = confirmRoundReset ? args[..^1] : args;
        if (
            (roundResetArgs is ["--reset-review-round", _] or ["--reset-review-round", _, "--receipt", _])
            && long.TryParse(roundResetArgs[1], NumberStyles.None, CultureInfo.InvariantCulture, out var resetRoundId)
            && resetRoundId > 0
            && (roundResetArgs.Length != 4 || !string.IsNullOrWhiteSpace(roundResetArgs[3]))
            && (!confirmRoundReset || roundResetArgs.Length == 4)
        )
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.ResetReviewRound,
                ResetRoundId = resetRoundId,
                ResetReceiptPath = roundResetArgs.Length == 4 ? roundResetArgs[3] : null,
                ConfirmReset = confirmRoundReset,
            };

        if (args is ["--complete-merged-batch", var batchPath] && !string.IsNullOrWhiteSpace(batchPath))
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.CompleteMergedBatch,
                MergedBatchPath = batchPath,
            };

        if (
            args
                is [
                    "--inspect-merged-archive",
                    var archiveRepo,
                    var archivePr,
                    var archiveTip,
                    var archiveLanded,
                    var archiveParent,
                ]
            && int.TryParse(archivePr, NumberStyles.None, CultureInfo.InvariantCulture, out var archiveNumber)
            && archiveNumber > 0
            && new[] { archiveTip, archiveLanded, archiveParent }.All(value =>
                value.Length == 40 && value.All(Uri.IsHexDigit)
            )
        )
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.InspectMergedArchive,
                RunPrRepoKey = archiveRepo,
                RunPrId = archivePr,
                ArtifactTipSha = archiveTip.ToLowerInvariant(),
                LandedSha = archiveLanded.ToLowerInvariant(),
                ParentSha = archiveParent.ToLowerInvariant(),
            };

        if (
            args
            is [
                "--archive-merged-pr",
                var archiveRepoKey,
                var archivePrId,
                var archiveBranchTip,
                var archiveMerge,
                var archiveMergeParent,
                "--evidence-root",
                var evidenceRoot,
                "--confirm-publish-and-delete",
            ]
        )
        {
            var identity = Parse([
                "--inspect-merged-archive",
                archiveRepoKey,
                archivePrId,
                archiveBranchTip,
                archiveMerge,
                archiveMergeParent,
            ]);
            if (identity.Kind == ScopedCommandKind.InspectMergedArchive && !string.IsNullOrWhiteSpace(evidenceRoot))
                return identity with { Kind = ScopedCommandKind.ArchiveMergedPr, ArchiveEvidenceRoot = evidenceRoot };
        }

        int? mergedSlot = null;
        var mergedInput = args;
        if (
            (
                args
                is ["--complete-merged-pr", _, _, _, _, _, "--inspect", "--slot", _]
                    or ["--complete-merged-pr", _, _, _, _, _, "--slot", _]
            )
            && int.TryParse(args[^1], out var slotIndex)
            && slotIndex is >= 0 and <= 5
        )
        {
            mergedSlot = slotIndex;
            mergedInput = args[..^2];
        }
        var inspectMerged = mergedInput is ["--complete-merged-pr", _, _, _, _, _, "--inspect"];
        var mergedArgs = inspectMerged ? mergedInput[..^1] : mergedInput;
        if (
            mergedArgs is ["--complete-merged-pr", var mergedRepo, var mergedPr, var tip, var landed, var parent]
            && int.TryParse(mergedPr, out var mergedNumber)
            && mergedNumber > 0
            && new[] { tip, landed, parent }.All(value => value.Length == 40 && value.All(Uri.IsHexDigit))
        )
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.CompleteMergedPr,
                RunPrRepoKey = mergedRepo,
                RunPrId = mergedPr,
                ArtifactTipSha = tip.ToLowerInvariant(),
                LandedSha = landed.ToLowerInvariant(),
                ParentSha = parent.ToLowerInvariant(),
                InspectMerged = inspectMerged,
                MergedSlot = mergedSlot,
            };

        var fresh = args is ["--run-pr", _, _, _, _, "--fresh"];
        var runArgs = fresh ? args[..^1] : args;
        if (runArgs is ["--run-pr", var repoKey, var prId, var headSha, var baseSha])
        {
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.RunPr,
                RunPrRepoKey = repoKey,
                RunPrId = prId,
                RunPrHeadSha = headSha,
                RunPrBaseSha = baseSha,
                Fresh = fresh,
            };
        }

        if (
            args is ["--run-pr-stream", var concurrencyText]
            && int.TryParse(concurrencyText, out var concurrency)
            && concurrency is >= 1 and <= 50
        )
        {
            return new ScopedCommandLineResult { Kind = ScopedCommandKind.RunPrStream, RunPrConcurrency = concurrency };
        }

        if (args is ["--redo-artifact-branch", var redoRepoKey, var redoPrId])
        {
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.RedoArtifactBranch,
                RedoRepoKey = redoRepoKey,
                RedoPrId = redoPrId,
            };
        }

        if (args.Length > 0 && Array.IndexOf(KnownPrefixes, args[0]) >= 0)
        {
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.Malformed,
                Error =
                    $"'{args[0]}' does not take {args.Length - 1} argument(s). Usage: "
                    + "--workflow-operation <name> | --list-candidate-prs | --list-review-branches | "
                    + "--run-pr <repoKey> <prId> <approvedHeadSha> <approvedBaseSha> [--fresh] | "
                    + "--complete-merged-pr <repoKey> <prId> <artifactTipSha> <landedSha> <parentSha> [--inspect] [--slot <0-5>] | "
                    + "--complete-merged-batch <jsonPath> | --inspect-merged-archive <repoKey> <prId> <artifactTipSha> <landedSha> <parentSha> | "
                    + "--archive-merged-pr <repoKey> <prId> <artifactTipSha> <landedSha> <parentSha> --evidence-root <localDirectory> --confirm-publish-and-delete | "
                    + "--run-pr-stream <concurrency:1-50> | --redo-artifact-branch <repoKey> <prId> | "
                    + "--reset-review-run <positive-id> [--confirm] | --reset-review-round <positive-id> [--receipt <jsonPath> --confirm]",
            };
        }

        return new ScopedCommandLineResult { Kind = ScopedCommandKind.None };
    }
}

/// <summary>One <see cref="ScopedCommandLine.Parse"/> outcome.</summary>
internal sealed record ScopedCommandLineResult
{
    public required ScopedCommandKind Kind { get; init; }

    public string? WorkflowOperation { get; init; }

    public int? RunPrConcurrency { get; init; }

    public bool Fresh { get; init; }

    public string? ArtifactTipSha { get; init; }
    public string? LandedSha { get; init; }
    public string? ParentSha { get; init; }
    public bool InspectMerged { get; init; }
    public int? MergedSlot { get; init; }
    public string? MergedBatchPath { get; init; }
    public string? ArchiveEvidenceRoot { get; init; }
    public bool ResumeMerged { get; init; }

    public long? NotesRunId { get; init; }

    public long? ResetRunId { get; init; }

    public long? ResetRoundId { get; init; }

    public string? ResetReceiptPath { get; init; }

    public bool ConfirmReset { get; init; }

    public string? RunPrRepoKey { get; init; }

    public string? RunPrId { get; init; }

    public string? RunPrHeadSha { get; init; }

    public string? RunPrBaseSha { get; init; }

    /// <summary>The repository key of a <see cref="ScopedCommandKind.RedoArtifactBranch"/> command.</summary>
    public string? RedoRepoKey { get; init; }

    /// <summary>The PR id of a <see cref="ScopedCommandKind.RedoArtifactBranch"/> command.</summary>
    public string? RedoPrId { get; init; }

    /// <summary>Set only when <see cref="Kind"/> is <see cref="ScopedCommandKind.Malformed"/> — the usage
    /// message to print to stderr before exiting.</summary>
    public string? Error { get; init; }
}
