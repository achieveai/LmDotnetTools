using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
/// <c>RecallConversation</c> (spec 679 §6): the model's bounded, verbatim read of the rows a checkpoint
/// covers. Self-bound to one thread; registered by <see cref="MultiTurnAgentLoop"/> on its own registry
/// at construction whenever compaction is at least in <see cref="CompactionMode.Warn"/>, and never added
/// or removed afterwards, so the tool list (the first prompt-cache segment) stays static. Not inherited
/// by children — it is registered after the inheritable-tool snapshot — and a child loop registers its
/// own instance over its own thread. With no active checkpoint it answers <c>nothing_compacted</c>: the
/// model already sees everything.
/// </summary>
public sealed class RecallConversationToolProvider : IFunctionProvider
{
    public const string ToolName = "RecallConversation";

    /// <summary>The answer when nothing is behind a boundary.</summary>
    public const string NothingCompacted = "nothing_compacted";

    private const int PageSize = 256;
    private const string TruncatedSuffix = "…[truncated, seq {0}]";
    private const string ContinuedSuffix = "…[continues, seq {0}, next offset {1}]";
    private const int AnswerOverheadChars = 512;
    private const int MaxShrinkAttempts = 6;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _threadId;
    private readonly IConversationStore? _store;
    private readonly Func<long?> _activeBoundarySeq;
    private readonly RecallLimits _limits;
    private readonly Func<int?> _resultCharCap;

    /// <param name="threadId">The thread every read is bound to.</param>
    /// <param name="store">The store rows are read from; null answers <c>nothing_compacted</c>.</param>
    /// <param name="activeBoundarySeq">The active checkpoint's boundary, or null.</param>
    /// <param name="limits">The read caps.</param>
    /// <param name="resultCharCap">
    ///     The execution view's per-result cap in characters, or null when the view does not trim. A recall
    ///     answer is kept under it so the view never trims the answer to a read of a trimmed result.
    /// </param>
    internal RecallConversationToolProvider(
        string threadId,
        IConversationStore? store,
        Func<long?> activeBoundarySeq,
        RecallLimits? limits = null,
        Func<int?>? resultCharCap = null
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(activeBoundarySeq);
        _threadId = threadId;
        _store = store;
        _activeBoundarySeq = activeBoundarySeq;
        _limits = limits ?? new RecallLimits();
        _resultCharCap = resultCharCap ?? (() => null);
    }

    public string ProviderName => "Compaction";

    public int Priority => 100;

    public IEnumerable<FunctionDescriptor> GetFunctions()
    {
        yield return new FunctionDescriptor
        {
            Contract = BuildContract(),
            Handler = HandleAsync,
            ProviderName = ProviderName,
        };
    }

    private FunctionContract BuildContract() =>
        new()
        {
            Name = ToolName,
            Description =
                "Read rows of this conversation that were compacted behind the checkpoint boundary, verbatim. "
                + "The checkpoint's Index lists seq ranges per run; narrow by query, seq range, run_id or "
                + "tool_call_id. Answers nothing_compacted when no checkpoint is active. A tool result shown "
                + "cleared or with characters elided can be read at any time by seq or tool_call_id, paging "
                + "with offset. You cannot compact the conversation; compaction is automatic or user-triggered.",
            Parameters =
            [
                Parameter(
                    "query",
                    JsonSchemaObject.String("Keyword or phrase, case-insensitive, matched against row text.")
                ),
                Parameter("from_seq", JsonSchemaObject.Integer("First seq to read (inclusive). Default 1.")),
                Parameter("to_seq", JsonSchemaObject.Integer("Last seq to read (inclusive). Default: the boundary.")),
                Parameter("tool_call_id", JsonSchemaObject.String("Return one tool call and its result.")),
                Parameter("seq", JsonSchemaObject.Integer("Return exactly this row, even one after the boundary.")),
                Parameter(
                    "offset",
                    JsonSchemaObject.Integer(
                        "With seq or tool_call_id: character offset to start reading each row's text at."
                    )
                ),
                Parameter("run_id", JsonSchemaObject.String("Only rows of this run (see the Index).")),
                Parameter(
                    "limit",
                    JsonSchemaObject.Integer($"Max rows. Default {_limits.DefaultLimit}, max {_limits.MaxLimit}.")
                ),
                Parameter(
                    "max_chars",
                    JsonSchemaObject.Integer(
                        $"Total text budget. Default {_limits.DefaultMaxChars}, max {_limits.MaxMaxChars}; "
                            + $"a row is cut at {_limits.RowCharCap} chars."
                    )
                ),
            ],
        };

    private static FunctionParameterContract Parameter(string name, JsonSchemaObject type) =>
        new()
        {
            Name = name,
            Description = type.Description,
            ParameterType = type,
            IsRequired = false,
        };

    private async Task<ToolHandlerResult> HandleAsync(string argsJson, ToolCallContext context, CancellationToken ct)
    {
        RecallArgs args;
        try
        {
            args = string.IsNullOrWhiteSpace(argsJson)
                ? new RecallArgs()
                : JsonSerializer.Deserialize<RecallArgs>(argsJson, Json) ?? new RecallArgs();
        }
        catch (JsonException ex)
        {
            return ToolHandlerResult.FromError($"invalid arguments: {ex.Message}", "invalid_arguments");
        }

        var boundary = _activeBoundarySeq();
        if (_store is null || (boundary is null && !args.IsTargeted))
        {
            return ToolHandlerResult.FromText(JsonSerializer.Serialize(new { error = NothingCompacted }, Json));
        }

        var cap = _resultCharCap();
        if (cap is { } limit)
        {
            // Leave room for the JSON around the text; the loop below absorbs escaping.
            args = args with
            {
                MaxChars = Math.Min(args.MaxChars ?? _limits.DefaultMaxChars, Math.Max(1, limit - AnswerOverheadChars)),
            };
        }

        var result = await ReadAsync(_store, boundary ?? 0, args, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(result, Json);
        for (var attempt = 0; cap is { } max && json.Length > max && attempt < MaxShrinkAttempts; attempt++)
        {
            // JSON escaping adds at least as many characters as it takes, so dropping the excess from the text
            // budget (plus the marker's own length) converges in a step or two.
            var budget = Math.Max(1, result.Rows.Sum(r => r.Text.Length) - (json.Length - max) - AnswerOverheadChars);
            args = args with { MaxChars = budget };
            result = await ReadAsync(_store, boundary ?? 0, args, ct).ConfigureAwait(false);
            json = JsonSerializer.Serialize(result, Json);
        }

        return ToolHandlerResult.FromText(json);
    }

    /// <summary>The bounded read itself; separated so the caps can be exercised without the registry.</summary>
    internal async Task<RecallResult> ReadAsync(
        IConversationStore store,
        long boundarySeq,
        RecallArgs args,
        CancellationToken ct
    )
    {
        // A targeted read (one seq, or one tool call) may reach past the boundary: it is how the model reads a
        // tool result the view cleared or trimmed. Everything else stays behind the boundary (§6.1).
        var targeted = args.IsTargeted;
        var from = args.Seq ?? Math.Max(1, args.FromSeq ?? 1);
        var to =
            args.Seq ?? (targeted ? (args.ToSeq ?? long.MaxValue) : Math.Min(boundarySeq, args.ToSeq ?? boundarySeq));
        var offset = targeted ? Math.Max(0, args.Offset ?? 0) : 0;
        var limit = Math.Clamp(args.Limit ?? _limits.DefaultLimit, 1, _limits.MaxLimit);
        var maxChars = Math.Clamp(args.MaxChars ?? _limits.DefaultMaxChars, 1, _limits.MaxMaxChars);

        var rows = new List<RecallRow>();
        var matched = 0;
        var truncated = false;
        var budget = maxChars;

        for (var cursor = from; cursor <= to; )
        {
            var page = await store.LoadMessageRangeAsync(_threadId, cursor, to, PageSize, ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            foreach (var persisted in page)
            {
                if (persisted.Seq is not { } seq || (!targeted && seq > boundarySeq) || seq < from || seq > to)
                {
                    continue;
                }

                if (Project(persisted, seq) is not { } row || !Matches(row, persisted, args))
                {
                    continue;
                }

                if (targeted)
                {
                    if (offset > 0 && row.Text.Length <= offset)
                    {
                        continue; // Paging a long result: the short call row has nothing left to show.
                    }

                    row = row with { Text = row.Text[offset..], Offset = offset, TotalChars = row.Text.Length };
                }

                matched++;
                if (rows.Count >= limit || budget <= 0)
                {
                    truncated = true;
                    continue;
                }

                var text = row.Text;
                int? nextOffset = null;
                var rowCap = targeted ? budget : _limits.RowCharCap;
                if (text.Length > rowCap || text.Length > budget)
                {
                    var keep = Math.Max(0, Math.Min(rowCap, budget));
                    if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
                    {
                        keep--;
                    }

                    if (targeted)
                    {
                        nextOffset = offset + keep;
                    }

                    text =
                        text[..keep]
                        + (
                            targeted
                                ? string.Format(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    ContinuedSuffix,
                                    seq,
                                    nextOffset
                                )
                                : string.Format(System.Globalization.CultureInfo.InvariantCulture, TruncatedSuffix, seq)
                        );
                    truncated = true;
                }

                budget -= text.Length;
                rows.Add(row with { Text = text, NextOffset = nextOffset });
            }

            var last = page[^1].Seq ?? to;
            if (budget <= 0 && targeted)
            {
                break;
            }
            if (page.Count < PageSize || last >= to)
            {
                break;
            }

            cursor = last + 1;
        }

        return new RecallResult
        {
            BoundarySeq = boundarySeq,
            Matched = matched,
            Returned = rows.Count,
            Truncated = truncated,
            Rows = rows,
            Hint =
                rows.FirstOrDefault(r => r.NextOffset is not null) is { } partial
                    ? $"Row seq {partial.Seq} continues: call again with the same seq or tool_call_id and offset={partial.NextOffset}."
                : truncated
                    ? "More than fits: narrow with query, from_seq/to_seq, run_id or tool_call_id, or raise limit/max_chars."
                : matched == 0 ? "No row matched; the checkpoint Index lists seq ranges per run."
                : "Every matching row is included.",
        };
    }

    private static RecallRow? Project(PersistedMessage persisted, long seq)
    {
        IMessage message;
        try
        {
            message = MessagePersistenceConverter.FromPersistedMessage(persisted);
        }
        catch (Exception)
        {
            return null;
        }

        // Reasoning rows are the model's own scratch (as GetAgentTranscript treats them) and checkpoint
        // rows are already summarised in the envelope: neither is conversation content to recall.
        if (message is ReasoningMessage or CompactionCheckpointMessage)
        {
            return null;
        }

        var (text, toolCallId) = message switch
        {
            ToolCallMessage call => ($"{call.FunctionName}({call.FunctionArgs})", call.ToolCallId),
            ToolCallResultMessage result => (result.Result ?? string.Empty, result.ToolCallId),
            ToolsCallMessage calls => (
                string.Join("\n", calls.ToolCalls.Select(c => $"{c.FunctionName}({c.FunctionArgs})")),
                null
            ),
            ToolsCallResultMessage results => (string.Join("\n", results.ToolCallResults.Select(r => r.Result)), null),
            ICanGetText textual => (textual.GetText() ?? string.Empty, null),
            _ => (string.Empty, null),
        };

        return new RecallRow
        {
            Seq = seq,
            RunId = persisted.RunId,
            Role = persisted.Role.ToLowerInvariant(),
            Type = persisted.MessageType,
            At = DateTimeOffset.FromUnixTimeMilliseconds(persisted.Timestamp).ToString("O"),
            Text = text,
            ToolCallId = toolCallId,
        };
    }

    private static bool Matches(RecallRow row, PersistedMessage persisted, RecallArgs args)
    {
        if (!string.IsNullOrEmpty(args.RunId) && !string.Equals(persisted.RunId, args.RunId, StringComparison.Ordinal))
        {
            return false;
        }

        if (
            !string.IsNullOrEmpty(args.ToolCallId)
            && !string.Equals(row.ToolCallId, args.ToolCallId, StringComparison.Ordinal)
        )
        {
            return false;
        }

        return string.IsNullOrEmpty(args.Query) || row.Text.Contains(args.Query, StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record RecallArgs
    {
        public string? Query { get; init; }

        public long? FromSeq { get; init; }

        public long? ToSeq { get; init; }

        public string? ToolCallId { get; init; }

        public string? RunId { get; init; }

        public int? Limit { get; init; }

        public int? MaxChars { get; init; }

        /// <summary>Exactly this row; lifts the boundary scope.</summary>
        public long? Seq { get; init; }

        /// <summary>With a targeted read, where in each row's text to start.</summary>
        public int? Offset { get; init; }

        /// <summary>One row or one tool call: a read that may reach a cleared or trimmed tail result.</summary>
        [JsonIgnore]
        public bool IsTargeted => Seq is not null || !string.IsNullOrEmpty(ToolCallId);
    }

    internal sealed record RecallRow
    {
        public required long Seq { get; init; }

        public required string RunId { get; init; }

        public required string Role { get; init; }

        public required string Type { get; init; }

        public required string At { get; init; }

        public required string Text { get; init; }

        public string? ToolCallId { get; init; }

        /// <summary>Targeted reads: where in the row's text <see cref="Text" /> starts.</summary>
        public int? Offset { get; init; }

        /// <summary>Targeted reads: the row's whole text length.</summary>
        public int? TotalChars { get; init; }

        /// <summary>Targeted reads: the offset to continue from, when the text was cut.</summary>
        public int? NextOffset { get; init; }
    }

    internal sealed record RecallResult
    {
        public required long BoundarySeq { get; init; }

        public required int Matched { get; init; }

        public required int Returned { get; init; }

        public required bool Truncated { get; init; }

        public required IReadOnlyList<RecallRow> Rows { get; init; }

        public required string Hint { get; init; }
    }
}
