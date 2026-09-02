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
public sealed class ProviderCheckpointSummarizer(IAgent providerAgent, string? defaultModelId = null)
    : ICheckpointSummarizer
{
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
        paraphrase rejects the whole checkpoint. Quote every standing instruction, constraint, prohibition,
        approval and decision a human gave. Never paraphrase a human row anywhere. Keep the narrative within
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
            new TextMessage { Text = SystemPrompt, Role = Role.System },
            new TextMessage { Text = BuildPrompt(request), Role = Role.User },
        };
        var options = new GenerateReplyOptions
        {
            ModelId = request.ModelId ?? defaultModelId ?? string.Empty,
            Functions = null,
        };

        var reply = (await providerAgent.GenerateReplyAsync(messages, options, ct).ConfigureAwait(false)).ToList();
        var usage = reply.OfType<UsageMessage>().LastOrDefault();
        var text = string.Concat(
            reply
                .Where(m => m is not UsageMessage && m is not TextMessage { IsThinking: true })
                .OfType<ICanGetText>()
                .Select(m => m.GetText())
        );

        var summary =
            ParseSummary(text)
            ?? throw new InvalidOperationException("The summary pass returned no parseable JSON object.");
        return new CheckpointSummaryResponse(summary, usage);
    }

    /// <summary>The user turn: previous manifest, state to mirror, then the rows, one per line with their seq.</summary>
    internal static string BuildPrompt(CheckpointSummaryRequest request)
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

        _ = sb.Append("\nRows being compacted:\n");
        foreach (var row in request.Rows)
        {
            _ = sb.Append('[')
                .Append("seq ")
                .Append(row.Seq)
                .Append("] (")
                .Append(row.EffectiveRunId ?? "-")
                .Append(") ")
                .Append(Describe(row.Message))
                .Append('\n');
        }

        return sb.ToString();
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
