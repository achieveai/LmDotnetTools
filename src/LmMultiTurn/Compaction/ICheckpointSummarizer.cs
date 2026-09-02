using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     What the summarizer is given (spec 679 §3.2): the previous checkpoint's manifest and narrative
///     when chaining, the raw rows between the previous boundary and the cut, and the state the
///     deterministic sections mirror, so the model can name outcomes and headlines for them.
/// </summary>
public sealed record CheckpointSummaryRequest
{
    public required string ThreadId { get; init; }

    /// <summary>The active checkpoint's manifest, merged field by field into the new one; null for a first checkpoint.</summary>
    public ContextManifest? PreviousManifest { get; init; }

    /// <summary>The active checkpoint's narrative: one input to the regenerated narrative, never re-summarised as a document.</summary>
    public string? PreviousNarrative { get; init; }

    /// <summary>The rows the new checkpoint covers that the previous one did not: previous boundary + 1 … cut.</summary>
    public required IReadOnlyList<SequencedMessage> Rows { get; init; }

    /// <summary>The human rows of the current run at or before the cut. Quoted whole by the assembler; here for context only.</summary>
    public IReadOnlyList<SequencedMessage> CurrentInstruction { get; init; } = [];

    /// <summary>The todo board, when the loop has one.</summary>
    public TodoBoardSnapshot? Board { get; init; }

    /// <summary>The sub-agent roster, as the manifest will carry it.</summary>
    public IReadOnlyList<AgentRef> Roster { get; init; } = [];

    /// <summary>The runs in <see cref="Rows" />, oldest first: one headline is wanted for each.</summary>
    public required IReadOnlyList<string> RunIds { get; init; }

    /// <summary>The most the narrative may be, in estimated tokens (V7).</summary>
    public long NarrativeTokenCap { get; init; } = 2_000;

    /// <summary>The model the summary pass should use, or null for the summarizer's default.</summary>
    public string? ModelId { get; init; }
}

/// <summary>
///     The model's contribution to a manifest (spec 679 §3.3): selections of rows to quote, free-text
///     goals, model-extracted tasks and artifacts, one headline per run, an outcome per agent, and the
///     narrative. Every quote is checked against its row before anything is committed (V3).
/// </summary>
public sealed record CheckpointSummary
{
    public IReadOnlyList<QuotedItem> Instructions { get; init; } = [];

    public IReadOnlyList<string> Goals { get; init; } = [];

    public IReadOnlyList<QuotedItem> Decisions { get; init; } = [];

    /// <summary>Used only when the loop has no board; ids are discarded (V4).</summary>
    public IReadOnlyList<TaskRef> Tasks { get; init; } = [];

    public IReadOnlyList<ArtifactRef> Artifacts { get; init; } = [];

    /// <summary>Headline per run id.</summary>
    public IReadOnlyDictionary<string, string> Headlines { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Outcome per agent id, for agents the rows show finishing.</summary>
    public IReadOnlyDictionary<string, string> AgentOutcomes { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public required string Narrative { get; init; }
}

/// <summary>The summary plus the usage the pass consumed, when the summarizer made a model call.</summary>
public sealed record CheckpointSummaryResponse(CheckpointSummary Summary, UsageMessage? Usage);

/// <summary>
///     The seam the checkpoint pipeline calls to get the model's half of a manifest (spec 679 §3.2).
///     <see cref="ProviderCheckpointSummarizer" /> is the default; tests substitute a deterministic one.
/// </summary>
public interface ICheckpointSummarizer
{
    Task<CheckpointSummaryResponse> SummarizeAsync(CheckpointSummaryRequest request, CancellationToken ct = default);
}
