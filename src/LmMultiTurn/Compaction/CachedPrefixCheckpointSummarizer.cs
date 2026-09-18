using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     The cached-prefix summarizer (eval spec §5.2): sends the agent's own request prefix byte for byte —
///     system prompt, active envelope, rows through the cut as the view shapes them, the same tool
///     definitions — and one appended user turn carrying the summary instruction. A provider that caches on a
///     byte-identical prefix then serves those rows at the cache-read rate. Tool use is disallowed
///     (<c>tool_choice=none</c>); a reply that calls a tool is a failed pass. Only valid on the loop's own
///     model: a different summary model shares no prompt cache, and the runtime refuses that combination.
/// </summary>
/// <param name="providerAgent">The loop's provider agent, called directly.</param>
/// <param name="prefixThroughSeq">The view as the agent sends it, cut after the given seq.</param>
/// <param name="functions">The tool definitions the loop sends, so the prefix matches byte for byte.</param>
/// <param name="defaultModelId">The model a request without its own <c>ModelId</c> runs on.</param>
/// <param name="instruction">
///     Replaces <see cref="ProviderCheckpointSummarizer.SystemPrompt" /> as the appended turn's instruction;
///     null or blank keeps the built-in one.
/// </param>
public sealed class CachedPrefixCheckpointSummarizer(
    IAgent providerAgent,
    Func<long, IReadOnlyList<IMessage>> prefixThroughSeq,
    Func<FunctionContract[]?> functions,
    string? defaultModelId = null,
    string? instruction = null
) : ICheckpointSummarizer
{
    private readonly string _instruction = string.IsNullOrWhiteSpace(instruction)
        ? ProviderCheckpointSummarizer.SystemPrompt
        : instruction;

    public async Task<CheckpointSummaryResponse> SummarizeAsync(
        CheckpointSummaryRequest request,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var lastCovered = request.Rows.Count == 0 ? 0 : request.Rows[^1].Seq;
        var messages = new List<IMessage>(prefixThroughSeq(lastCovered))
        {
            new TextMessage
            {
                Text = _instruction + "\n\n" + ProviderCheckpointSummarizer.BuildPromptIndex(request),
                Role = Role.User,
            },
        };
        var options = new GenerateReplyOptions
        {
            ModelId = request.ModelId ?? defaultModelId ?? string.Empty,
            Functions = functions(),
            ToolChoice = "none",
            MaxToken = request.MaxOutputTokens,
        };

        var reply = (await providerAgent.GenerateReplyAsync(messages, options, ct).ConfigureAwait(false)).ToList();
        if (reply.Any(m => m is ToolCallMessage or ICanGetToolCalls))
        {
            throw new InvalidOperationException(
                "The cached-prefix summary pass called a tool; the checkpoint is not built from it."
            );
        }

        return new CheckpointSummaryResponse(
            ProviderCheckpointSummarizer.RequireSummary(ProviderCheckpointSummarizer.ReplyText(reply)),
            reply.OfType<UsageMessage>().LastOrDefault()
        );
    }
}
