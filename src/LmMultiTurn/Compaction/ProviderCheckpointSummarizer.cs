using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     The default <see cref="ICheckpointSummarizer" /> (spec 679 §3.2): one call through the loop's
///     provider agent — not through the tool middleware — with a fixed prompt, no tools, and a JSON
///     answer. The pass's <see cref="UsageMessage" /> is returned so the pipeline can attribute it.
/// </summary>
/// <remarks>
///     The prompt asks for quotes by <c>seq</c> and says they will be checked byte for byte; the
///     validator, not this class, is what makes a paraphrase fail (V3). A reply that carries no
///     parseable JSON object is a failed call (<c>summary_call_failed</c>), never an empty manifest.
/// </remarks>
/// <param name="providerAgent">The loop's provider agent, called directly.</param>
/// <param name="defaultModelId">The model a request without its own <c>ModelId</c> runs on.</param>
/// <param name="systemPrompt">
///     Replaces <see cref="SystemPrompt" /> for this summarizer; null or blank keeps the built-in one. The eval
///     varies the instruction here (spec §5.2) without touching what the validator checks.
/// </param>
public sealed class ProviderCheckpointSummarizer(
    IAgent providerAgent,
    string? defaultModelId = null,
    string? systemPrompt = null
) : ICheckpointSummarizer
{
    private readonly string _systemPrompt = string.IsNullOrWhiteSpace(systemPrompt) ? SystemPrompt : systemPrompt;

    /// <summary>
    ///     The signature this type had before the <c>systemPrompt</c> parameter was added. Optional parameters are
    ///     a source-level convenience: adding one changes the emitted constructor, so an assembly compiled
    ///     against the two-parameter form would throw <see cref="MissingMethodException"/> against this build
    ///     even though the version did not move. This overload keeps <c>.ctor(IAgent, string)</c> emitted.
    ///     It deliberately has no default on <paramref name="defaultModelId"/>: that is what keeps a one-argument
    ///     call unambiguous between the two, while a two-argument call binds here in preference to omitting an
    ///     optional parameter.
    /// </summary>
    /// <param name="providerAgent">The loop's provider agent, called directly.</param>
    /// <param name="defaultModelId">The model a request without its own <c>ModelId</c> runs on.</param>
    public ProviderCheckpointSummarizer(IAgent providerAgent, string? defaultModelId)
        : this(providerAgent, defaultModelId, null) { }

    /// <summary>The fixed instruction every summary pass runs under.</summary>
    public const string SystemPrompt = """
        You compact an agent conversation into a checkpoint. You are given the rows being compacted, each
        tagged with its seq number, plus the previous checkpoint's manifest when one exists.

        Reply with one JSON object and nothing else:
        {
          "instructions": [{"seq": <int>, "quote": "<exact substring of that row>"}],
          "goals": ["<goal or acceptance criterion>"],
          "decisions": [{"seq": <int>, "quote": "<exact substring of that row>"}],
          "tasks": [{"title": "<open work item>", "status": "<status>"}],
          "artifacts": [{"path": "<file, id or url>", "hash": "<hash if the rows show one>", "origin_seq": <int>}],
          "headlines": {"<run id>": "<one line: what that run did>"},
          "agent_outcomes": {"<agent id>": "<one line outcome>"},
          "narrative": "<what happened, in order, within the token cap>"
        }

        Rules: quotes must be copied verbatim from the cited row — they are verified byte for byte and a
        paraphrase rejects the whole checkpoint. Quote only from a row whose text is shown in full, and copy
        only the text after its label (such as "user: "), never the seq or run tags. Never quote a row marked
        [not quotable]: tool call rows, tool result rows, checkpoint rows and truncated rows have no whole text
        to quote. Quote every standing instruction, constraint, prohibition, approval and decision a human gave. Never paraphrase a human row anywhere. Keep the narrative within
        the stated cap. Do not invent tasks, agents or artifacts the rows do not show.
        """;

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    public async Task<CheckpointSummaryResponse> SummarizeAsync(
        CheckpointSummaryRequest request,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var messages = new IMessage[]
        {
            new TextMessage { Text = _systemPrompt, Role = Role.System },
            new TextMessage { Text = BuildPrompt(request), Role = Role.User },
        };
        var options = new GenerateReplyOptions
        {
            ModelId = request.ModelId ?? defaultModelId ?? string.Empty,
            Functions = null,
            MaxToken = request.MaxOutputTokens,
        };

        var reply = (await providerAgent.GenerateReplyAsync(messages, options, ct).ConfigureAwait(false)).ToList();
        return new CheckpointSummaryResponse(
            RequireSummary(ReplyText(reply)),
            reply.OfType<UsageMessage>().LastOrDefault()
        );
    }

    /// <summary>The visible text of a summary reply: everything but the usage row and the model's thinking.</summary>
    internal static string ReplyText(IEnumerable<IMessage> reply) =>
        string.Concat(
            reply
                .Where(m => m is not (UsageMessage or TextMessage { IsThinking: true }))
                .OfType<ICanGetText>()
                .Select(m => m.GetText())
        );

    /// <summary>The summary the reply carries; a reply with no parseable JSON object is a failed pass.</summary>
    internal static CheckpointSummary RequireSummary(string? text) =>
        ParseSummary(text)
        ?? throw new InvalidOperationException("The summary pass returned no parseable JSON object.");

    /// <summary>The user turn: previous manifest, state to mirror, then the rows, one per line with their seq.</summary>
    internal static string BuildPrompt(CheckpointSummaryRequest request) => BuildPromptCore(request, indexOnly: false);

    /// <summary>
    ///     The prompt for a request whose rows are already in the provider's prefix (eval spec §5.2): the same
    ///     header, manifest, focus, roster and board as <see cref="BuildPrompt" />, but each row as
    ///     <c>[seq N] (run) head…</c> so the model can cite seqs without a second copy of the text.
    /// </summary>
    internal static string BuildPromptIndex(CheckpointSummaryRequest request) =>
        BuildPromptCore(request, indexOnly: true);

    private static string BuildPromptCore(CheckpointSummaryRequest request, bool indexOnly)
    {
        var sb = new StringBuilder();
        _ = sb.Append("Thread: ").Append(request.ThreadId).Append('\n');
        _ = sb.Append("Narrative token cap: ").Append(request.NarrativeTokenCap).Append('\n');
        _ = sb.Append("Runs needing a headline: ").AppendJoin(", ", request.RunIds).Append('\n');

        if (request.PreviousManifest is not null)
        {
            _ = sb.Append("\nPrevious checkpoint manifest (merge into yours; keep its quotes):\n")
                .Append(JsonSerializer.Serialize(request.PreviousManifest, WriteOptions))
                .Append('\n');
        }

        if (!string.IsNullOrEmpty(request.PreviousNarrative))
        {
            _ = sb.Append("\nPrevious narrative (continue it; do not restate it):\n")
                .Append(request.PreviousNarrative)
                .Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(request.Focus))
        {
            _ = sb.Append(
                    "\nOperator focus (steer what to keep and what the narrative emphasises; do not quote this text):\n"
                )
                .Append("<<<FOCUS\n")
                // The operator's text must not be able to close its own section and speak as the prompt.
                .Append(request.Focus.Replace("FOCUS>>>", "FOCUS> > >", StringComparison.OrdinalIgnoreCase))
                .Append("\nFOCUS>>>\n");
        }

        if (request.Roster.Count > 0)
        {
            _ = sb.Append("\nAgents (give an outcome for each that finished):\n");
            foreach (var agent in request.Roster)
            {
                _ = sb.Append("- ")
                    .Append(agent.AgentId)
                    .Append(": ")
                    .Append(agent.Template)
                    .Append(", ")
                    .Append(agent.Status)
                    .Append(" — ")
                    .Append(agent.Task)
                    .Append('\n');
            }
        }

        if (request.Board is not null && !request.Board.IsEmpty)
        {
            _ = sb.Append("\nTodo board (authoritative; do not list tasks):\n");
            foreach (var task in ManifestAssembler.Flatten(request.Board.Tasks))
            {
                _ = sb.Append("- [")
                    .Append(task.Id)
                    .Append("] ")
                    .Append(task.Title)
                    .Append(" (")
                    .Append(task.Status)
                    .Append(")\n");
            }
        }

        _ = sb.Append(
            indexOnly
                ? "\nRows being compacted (already in your context above; cite by seq):\n"
                : "\nRows being compacted:\n"
        );
        var described = request.Rows.Select(r => (Row: r, Text: Describe(r.Message))).ToList();
        var cap = indexOnly ? 0 : RowCap(described, request.RowCharCap, request.PromptCharBudget);
        foreach (var (row, text) in described)
        {
            _ = sb.Append('[')
                .Append("seq ")
                .Append(row.Seq)
                .Append("] (")
                .Append(row.EffectiveRunId ?? "-")
                .Append(") ");
            if (indexOnly)
            {
                // The prefix already carries the row whole; the line only has to name it.
                _ = sb.Append(Head(text, IndexHeadChars)).Append('\n');
                continue;
            }

            var shown = row.IsHumanRow ? text : Truncate(text, cap);
            // V3 checks a quote against the row's own text, so only a row whose whole text is on the line can be quoted.
            var quotable =
                !string.IsNullOrEmpty(row.Text) && !row.IsCheckpointRow && (row.IsHumanRow || text.Length <= cap);
            _ = sb.Append(quotable ? "" : NotQuotable).Append(shown).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>The characters of a row an index line shows.</summary>
    internal const int IndexHeadChars = 80;

    /// <summary>The first <paramref name="chars" /> characters, with an ellipsis when the text is cut.</summary>
    private static string Head(string text, int chars)
    {
        if (text.Length <= chars)
        {
            return text;
        }

        var keep = chars > 0 && char.IsHighSurrogate(text[chars - 1]) ? chars - 1 : chars;
        return string.Concat(text.AsSpan(0, keep), "…");
    }

    /// <summary>The tag on a row line whose text cannot be quoted: a tool call or result, a checkpoint, or a truncated row.</summary>
    internal const string NotQuotable = "[not quotable] ";

    /// <summary>The least a non-human row is cut to, however tight the budget.</summary>
    internal const int MinRowCharCap = 200;

    /// <summary>Per-line characters besides the row text: the seq and run tags and the not-quotable tag.</summary>
    private const int RowLineOverhead = 32 + 15;

    /// <summary>
    ///     The per-row cap for non-human rows: the configured cap, halved while the rows overrun the budget. Human
    ///     rows are the quote sources and are never cut, so they are charged to the budget whole.
    /// </summary>
    private static int RowCap(List<(SequencedMessage Row, string Text)> rows, int? rowCharCap, int? budget)
    {
        var cap = rowCharCap ?? int.MaxValue;
        if (budget is not { } limit)
        {
            return cap;
        }

        var human = rows.Where(r => r.Row.IsHumanRow).Sum(r => (long)r.Text.Length + RowLineOverhead);
        while (
            cap > MinRowCharCap
            && human + rows.Where(r => !r.Row.IsHumanRow).Sum(r => (long)Math.Min(r.Text.Length, cap) + RowLineOverhead)
                > limit
        )
        {
            cap = Math.Max(MinRowCharCap, cap / 2);
        }

        return cap;
    }

    private static string Truncate(string text, int cap)
    {
        if (text.Length <= cap)
        {
            return text;
        }

        var keep = cap > 0 && char.IsHighSurrogate(text[cap - 1]) ? cap - 1 : cap;
        return string.Concat(
            text.AsSpan(0, keep),
            string.Create(CultureInfo.InvariantCulture, $" …[truncated {text.Length - keep} chars]")
        );
    }

    /// <summary>Parses the model's JSON object, tolerating code fences and prose around it.</summary>
    internal static CheckpointSummary? ParseSummary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        SummaryDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SummaryDto>(text.AsSpan(start, end - start + 1), ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is null)
        {
            return null;
        }

        return new CheckpointSummary
        {
            Instructions = dto.Instructions ?? [],
            Goals = dto.Goals ?? [],
            Decisions = dto.Decisions ?? [],
            Tasks = dto.Tasks ?? [],
            Artifacts = dto.Artifacts ?? [],
            Headlines = dto.Headlines ?? new Dictionary<string, string>(StringComparer.Ordinal),
            AgentOutcomes = dto.AgentOutcomes ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Narrative = dto.Narrative ?? string.Empty,
        };
    }

    private static string Describe(IMessage message) =>
        message switch
        {
            ToolCallMessage call => $"assistant tool call {call.FunctionName} {call.FunctionArgs}",
            ICanGetToolCalls many => "assistant tool calls "
                + string.Join("; ", (many.GetToolCalls() ?? []).Select(tc => $"{tc.FunctionName} {tc.FunctionArgs}")),
            ToolCallResultMessage result =>
                $"tool result {result.ToolName}{(result.IsDeferred ? " (deferred)" : "")}: {result.Result}",
            ToolsCallResultMessage results => "tool results: "
                + string.Join("; ", results.ToolCallResults.Select(r => $"{r.ToolName}: {r.Result}")),
            // RC5: a collaboration row is described structurally, so the summarizer reads its type, sender and
            // correlation rather than having to parse the envelope text the receiver sees.
            AgentMessage agent =>
                $"agent-message {agent.AgentMessageType} from={agent.FromAgentId} id={agent.MessageId}"
                    + (agent.InResponseTo is null ? "" : $" in_response_to={agent.InResponseTo}")
                    + $": {agent.Body}",
            NotifyMessage { NotifyKind: NotifyKinds.DescendantQuestion } question =>
                $"descendant-question from={question.SourceToolCallId ?? "-"} (unanswered until resolved): "
                    + (question.Detail ?? question.Label),
            NotifyMessage notify => $"notification {notify.NotifyKind}: {notify.GetText()}",
            CompactionCheckpointMessage checkpoint => $"checkpoint {checkpoint.CheckpointId} (already compacted)",
            ICanGetText text => $"{message.Role.ToString().ToLowerInvariant()}: {text.GetText()}",
            _ => $"{message.Role.ToString().ToLowerInvariant()}: <{message.GetType().Name}>",
        };

    private sealed record SummaryDto
    {
        [JsonPropertyName("instructions")]
        public List<QuotedItem>? Instructions { get; init; }

        [JsonPropertyName("goals")]
        public List<string>? Goals { get; init; }

        [JsonPropertyName("decisions")]
        public List<QuotedItem>? Decisions { get; init; }

        [JsonPropertyName("tasks")]
        public List<TaskRef>? Tasks { get; init; }

        [JsonPropertyName("artifacts")]
        public List<ArtifactRef>? Artifacts { get; init; }

        [JsonPropertyName("headlines")]
        public Dictionary<string, string>? Headlines { get; init; }

        [JsonPropertyName("agent_outcomes")]
        public Dictionary<string, string>? AgentOutcomes { get; init; }

        [JsonPropertyName("narrative")]
        public string? Narrative { get; init; }
    }
}
