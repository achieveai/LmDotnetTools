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

    internal RecallConversationToolProvider(
        string threadId,
        IConversationStore? store,
        Func<long?> activeBoundarySeq,
        RecallLimits? limits = null
    )
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(activeBoundarySeq);
        _threadId = threadId;
        _store = store;
        _activeBoundarySeq = activeBoundarySeq;
        _limits = limits ?? new RecallLimits();
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
                + "tool_call_id. Answers nothing_compacted when no checkpoint is active.",
            Parameters =
            [
                Parameter(
                    "query",
                    JsonSchemaObject.String("Keyword or phrase, case-insensitive, matched against row text.")
                ),
                Parameter("from_seq", JsonSchemaObject.Integer("First seq to read (inclusive). Default 1.")),
                Parameter("to_seq", JsonSchemaObject.Integer("Last seq to read (inclusive). Default: the boundary.")),
                Parameter("tool_call_id", JsonSchemaObject.String("Return one tool call and its result.")),
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
        if (boundary is not { } boundarySeq || _store is null)
        {
            return ToolHandlerResult.FromText(JsonSerializer.Serialize(new { error = NothingCompacted }, Json));
        }

        var result = await ReadAsync(_store, boundarySeq, args, ct).ConfigureAwait(false);
        return ToolHandlerResult.FromText(JsonSerializer.Serialize(result, Json));
    }

    /// <summary>The bounded read itself; separated so the caps can be exercised without the registry.</summary>
    internal async Task<RecallResult> ReadAsync(
        IConversationStore store,
        long boundarySeq,
        RecallArgs args,
        CancellationToken ct
    )
    {
        var from = Math.Max(1, args.FromSeq ?? 1);
        var to = Math.Min(boundarySeq, args.ToSeq ?? boundarySeq);
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
                if (persisted.Seq is not { } seq || seq > boundarySeq)
                {
                    continue;
                }

                if (Project(persisted, seq) is not { } row || !Matches(row, persisted, args))
                {
                    continue;
                }

                matched++;
                if (rows.Count >= limit || budget <= 0)
                {
                    truncated = true;
                    continue;
                }

                var text = row.Text;
                if (text.Length > _limits.RowCharCap || text.Length > budget)
                {
                    var keep = Math.Max(0, Math.Min(_limits.RowCharCap, budget));
                    text =
                        text[..keep]
                        + string.Format(System.Globalization.CultureInfo.InvariantCulture, TruncatedSuffix, seq);
                    truncated = true;
                }

                budget -= text.Length;
                rows.Add(row with { Text = text });
            }

            var last = page[^1].Seq ?? to;
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
                truncated
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
