using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     The local size heuristic the cut rules and the validator measure with (spec 679 §4.2): text
///     length / 4, plus 12 tokens of per-message framing, plus tool arguments and results / 4. It is an
///     estimate for ordering decisions, not a count; #681's estimator seam replaces it through the
///     <c>Func&lt;IMessage, long&gt;</c> every option record here accepts.
/// </summary>
internal static class CompactionTokenEstimate
{
    /// <summary>Tokens charged per message for role and framing.</summary>
    public const long PerMessageOverhead = 12;

    /// <summary>The estimator every option record defaults to.</summary>
    public static readonly Func<IMessage, long> Default = Estimate;

    /// <summary>Estimated tokens of a run of text.</summary>
    public static long EstimateText(string? text) => text is null ? 0 : (text.Length + 3) / 4;

    /// <summary>Estimated tokens of one message, including its framing.</summary>
    public static long Estimate(IMessage message) => Estimate(message, EstimateText);

    /// <summary>
    ///     A message estimator that sizes every run of text with <paramref name="text"/> instead of the
    ///     length / 4 heuristic: a host with a real tokenizer plugs it in here, and the per-message framing
    ///     stays the same. The heuristic overcounts line-numbered prose by ~40% (measured: an 85k estimate
    ///     for a 60k request), which is enough to push a fitting request over the usable window and into
    ///     the fit escalation that trims the tool results the model has not read yet.
    /// </summary>
    public static Func<IMessage, long> Create(Func<string?, long> text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return message => Estimate(message, text);
    }

    private static long Estimate(IMessage message, Func<string?, long> text)
    {
        ArgumentNullException.ThrowIfNull(message);

        var body = message switch
        {
            ToolCallMessage call => text(call.FunctionName) + text(call.FunctionArgs),
            ICanGetToolCalls calls => (calls.GetToolCalls() ?? []).Sum(c =>
                text(c.FunctionName) + text(c.FunctionArgs)
            ),
            ToolCallResultMessage result => text(result.Result),
            ToolsCallResultMessage results => results.ToolCallResults.Sum(r => text(r.Result)),
            ICanGetText t => text(t.GetText()),
            _ => 0,
        };

        return PerMessageOverhead + body;
    }

    /// <summary>
    ///     Estimated tokens of the tool definitions sent with every request: name, description and parameter
    ///     schema JSON per tool, plus framing. Measured runs put this prefix at ~17.6k tokens; leaving it out
    ///     made the policy believe a 32k window had 28k of room when it had 10k.
    /// </summary>
    public static long EstimateToolSchemas(IEnumerable<FunctionContract> contracts)
    {
        ArgumentNullException.ThrowIfNull(contracts);
        return contracts.Sum(contract =>
        {
            string schema;
            try
            {
                schema = JsonSerializer.Serialize(contract.GetJsonSchema());
            }
            catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
            {
                schema = string.Join(' ', (contract.Parameters ?? []).Select(p => p.Name + " " + p.Description));
            }

            return PerMessageOverhead
                + EstimateText(contract.Name)
                + EstimateText(contract.Description)
                + EstimateText(schema);
        });
    }

    /// <summary>Estimated tokens of a message list.</summary>
    public static long Estimate(IEnumerable<IMessage> messages, Func<IMessage, long>? estimator = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var measure = estimator ?? Default;
        return messages.Sum(measure);
    }
}
