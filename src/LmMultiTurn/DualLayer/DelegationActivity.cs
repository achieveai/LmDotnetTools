using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

/// <summary>
/// What the executor actually ran for one delegated call, recorded by the host from the run's own
/// messages. The executor's report is model-written prose. It can drift. For example, it can run the
/// next step of a sequence the planner never asked for, and then report on that step instead. This
/// record is the check on the prose: the planner is told, from ground truth, when the run was not
/// exactly the call it asked for.
/// </summary>
internal sealed class DelegationActivity(string executorThreadId)
{
    private const int MaxArgumentsLength = 160;
    private const int MaxValueLength = 80;

    private readonly Dictionary<string, string?> _argumentsByCallId = new(StringComparer.Ordinal);
    private readonly List<ExecutedToolCall> _executed = [];

    /// <summary>The executor's finished tool calls, in the order their results arrived.</summary>
    public IReadOnlyList<ExecutedToolCall> Executed => _executed;

    /// <summary>Records one message of the executor's run. Other threads' messages (its sub-agents) are skipped.</summary>
    public void Observe(IMessage message)
    {
        if (message.ThreadId is { } threadId && !string.Equals(threadId, executorThreadId, StringComparison.Ordinal))
        {
            return;
        }

        switch (message)
        {
            case ToolCallMessage { ToolCallId: { } callId } toolCall:
                _argumentsByCallId[callId] = toolCall.FunctionArgs;
                break;
            case ToolCallResultMessage result:
                string? arguments = null;
                _ =
                    result.ToolCallId is { } resultCallId
                    && _argumentsByCallId.TryGetValue(resultCallId, out arguments);
                _executed.Add(new ExecutedToolCall(result.ToolName ?? "(unnamed tool)", arguments, result.IsError));
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Nothing when the run was the requested call, once, and it succeeded. Otherwise a note for the
    /// planner that lists every call the executor ran, marks which one was the call made, and names
    /// each argument the executor changed.
    /// </summary>
    /// <remarks>
    /// The same call is the same tool with the planner's argument values. An optional argument the
    /// planner left out does not make it a different call when the executor passes it empty or at its
    /// default (<c>""</c>, <c>0</c>, <c>false</c>, <c>null</c>, <c>[]</c>, <c>{}</c>): tool-calling
    /// models routinely fill those in. Any other difference is named, key by key, so the planner can
    /// judge it. Only a call to a different tool, or a second call after the planner's, is extra.
    /// </remarks>
    public static string? Describe(DelegatedToolCall requested, IReadOnlyList<ExecutedToolCall> executed)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(executed);

        var comparisons = executed.Select(call => Compare(requested, call)).ToList();
        var requestedIndex = comparisons.FindIndex(c => c is { Changes.Count: 0 });
        if (requestedIndex < 0)
        {
            requestedIndex = comparisons.FindIndex(c => c is not null);
        }

        if (executed.Count == 1 && requestedIndex == 0 && comparisons[0]!.Changes.Count == 0 && !executed[0].IsError)
        {
            return null;
        }

        var note = new StringBuilder();
        _ = note.Append("---\nExecutor activity for this call (recorded by the host, not written by the executor):");
        if (executed.Count == 0)
        {
            _ = note.Append("\nNo tools ran. The report above comes from what the executor already knew.");
            return note.ToString();
        }

        for (var i = 0; i < executed.Count; i++)
        {
            var call = executed[i];
            _ = note.Append('\n').Append(i + 1).Append(". ").Append(call.ToolName);
            if (call.ArgumentsJson is { } arguments)
            {
                _ = note.Append(' ').Append(Truncate(arguments, MaxArgumentsLength));
            }

            _ = note.Append(call.IsError ? ": failed" : ": succeeded")
                .Append(
                    i != requestedIndex ? " (extra: not the call you made)"
                    : comparisons[i]!.Changes is { Count: > 0 } changes
                        ? $" (your call, sent with different arguments: {string.Join("; ", changes)})"
                    : " (your call)"
                );
        }

        if (requestedIndex < 0)
        {
            _ = note.Append("\nYour call was not run.");
        }
        else if (comparisons[requestedIndex]!.Changes.Count > 0)
        {
            _ = note.Append(
                "\nThe executor sent your call with the arguments listed. That is what it asked the tool for, "
                    + "not what the tool did: a tool or the host can ignore or override an argument, so take "
                    + "what happened from the tool's own result. Make sure the report above still answers "
                    + "what you asked."
            );
        }

        if (executed.Count > (requestedIndex < 0 ? 0 : 1))
        {
            _ = note.Append(
                "\nThe executor also ran calls other than yours. Make sure the report above answers your call. "
                    + "If it does not, call again."
            );
        }

        return note.ToString();
    }

    /// <summary>Null when <paramref name="executed"/> is another tool; otherwise how its arguments differ.</summary>
    private static Comparison? Compare(DelegatedToolCall requested, ExecutedToolCall executed)
    {
        if (!string.Equals(requested.ToolName, executed.ToolName, StringComparison.Ordinal))
        {
            return null;
        }

        // The arguments are unknown when the provider streamed the call without a finished message.
        // The name is then the best evidence there is.
        if (executed.ArgumentsJson is null)
        {
            return new Comparison([]);
        }

        JsonNode? asked;
        JsonNode? ran;
        try
        {
            asked = JsonNode.Parse(requested.ArgumentsJson);
            ran = JsonNode.Parse(executed.ArgumentsJson);
        }
        catch (JsonException)
        {
            return new Comparison(
                string.Equals(requested.ArgumentsJson, executed.ArgumentsJson, StringComparison.Ordinal)
                    ? []
                    : ["arguments differ"]
            );
        }

        if (asked is not JsonObject askedObject || ran is not JsonObject ranObject)
        {
            return new Comparison(JsonNode.DeepEquals(asked, ran) ? [] : ["arguments differ"]);
        }

        var changes = new List<string>();
        foreach (var (key, askedValue) in askedObject)
        {
            if (!ranObject.TryGetPropertyValue(key, out var ranValue))
            {
                if (!IsEmptyOrDefault(askedValue))
                {
                    changes.Add($"\"{key}\" left out (you passed {Render(askedValue)})");
                }
            }
            else if (!JsonNode.DeepEquals(askedValue, ranValue))
            {
                changes.Add($"\"{key}\" {Render(askedValue)} -> {Render(ranValue)}");
            }
        }

        foreach (var (key, ranValue) in ranObject)
        {
            if (!askedObject.ContainsKey(key) && !IsEmptyOrDefault(ranValue))
            {
                changes.Add($"\"{key}\" added: {Render(ranValue)}");
            }
        }

        return new Comparison(changes);
    }

    private static bool IsEmptyOrDefault(JsonNode? value) =>
        value switch
        {
            null => true,
            JsonArray array => array.Count == 0,
            JsonObject obj => obj.Count == 0,
            JsonValue scalar => scalar.GetValueKind() switch
            {
                JsonValueKind.String => scalar.GetValue<string>().Length == 0,
                JsonValueKind.Number => scalar.TryGetValue<decimal>(out var number) && number == 0,
                JsonValueKind.False => true,
                JsonValueKind.Null => true,
                _ => false,
            },
            _ => false,
        };

    private static string Render(JsonNode? value) => Truncate(value?.ToJsonString() ?? "null", MaxValueLength);

    private sealed record Comparison(IReadOnlyList<string> Changes);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max), "…");
}

/// <summary>One tool call the executor finished. <see cref="ArgumentsJson"/> is null when it was not observed.</summary>
internal sealed record ExecutedToolCall(string ToolName, string? ArgumentsJson, bool IsError);
