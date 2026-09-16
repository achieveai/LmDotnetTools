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
    public static long Estimate(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var body = message switch
        {
            ToolCallMessage call => EstimateText(call.FunctionName) + EstimateText(call.FunctionArgs),
            ICanGetToolCalls calls => (calls.GetToolCalls() ?? []).Sum(c =>
                EstimateText(c.FunctionName) + EstimateText(c.FunctionArgs)
            ),
            ToolCallResultMessage result => EstimateText(result.Result),
            ToolsCallResultMessage results => results.ToolCallResults.Sum(r => EstimateText(r.Result)),
            ICanGetText text => EstimateText(text.GetText()),
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
