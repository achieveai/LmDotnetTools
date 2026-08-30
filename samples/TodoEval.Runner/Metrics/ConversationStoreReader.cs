using System.Text.Json;
using System.Text.RegularExpressions;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// Offline reader for the host's on-disk conversation store
/// (<c>conversations/&lt;threadId&gt;/</c>), implementing exactly the store contract pinned by
/// <c>evals/todo-eval/metrics-spec.md</c> (the #618 oracle spec this harness is the C# twin of):
/// <list type="bullet">
/// <item><c>messages.json</c> is a JSON array of camelCase envelopes whose <c>messageJson</c> is a
/// STRING holding the snake_case inner message; array order is the thread's message order.</item>
/// <item>Calls are envelopes with <c>messageType == "ToolCallMessage"</c>; results are
/// <c>"ToolCallResultMessage"</c>, paired by <c>tool_call_id</c> within the same thread. An
/// unpaired call counts as a call, never as an error.</item>
/// <item>An ERROR result is the defensive union: <c>is_error == true</c> on the result message,
/// OR a <c>result</c> text (leading whitespace removed) starting with <c>Error:</c>, ordinal and
/// case-sensitive. The text prefix is the primary signal — the store records
/// <c>is_error: false</c> on results whose text is an error.</item>
/// <item>Turns are distinct non-null generation ids (envelope <c>generationId</c>, falling back to
/// the inner message's <c>generationId</c>/<c>generation_id</c>).</item>
/// </list>
/// The one thing the spec does not need but this harness does: the sub-agent parent link
/// (<c>sample.subAgentOf</c> in <c>metadata.json</c>), used to group each root conversation with
/// its sub-agent threads when one isolated host served a whole sweep rather than a single run.
/// </summary>
internal static class ConversationStoreReader
{
    private const string SubAgentParentKey = "sample.subAgentOf";
    private const string TodoBoardKey = "todo.board";
    public const string SubAgentDirPrefix = "subagent-";

    // Fabricated-compliance heuristic, verbatim from metrics-spec.md. Only ever applied to
    // sub-agent threads with zero task-tool calls, so a truthful report of real board work can
    // never be flagged.
    private static readonly Regex ClaimVerb = new("(?i)(claim|complet|marked)", RegexOptions.CultureInvariant);
    private static readonly Regex ClaimNoun = new("(?i)(task|todo|board)", RegexOptions.CultureInvariant);

    /// <summary>One persisted conversation thread, reduced to the spec's per-thread facts.</summary>
    internal sealed record ThreadData
    {
        public required string ThreadId { get; init; }
        public string? ParentThreadId { get; init; }
        public string? TodoBoardJson { get; init; }

        /// <summary>Distinct non-null generation ids — the spec's turn count for this thread.</summary>
        public required int TurnCount { get; init; }

        /// <summary>All tools, not only task tools.</summary>
        public required int TotalToolCalls { get; init; }

        public required int UnpairedToolCalls { get; init; }

        /// <summary>Per task tool (calls, errors); only the 15 task tools appear.</summary>
        public required IReadOnlyDictionary<string, ToolStats> PerTaskTool { get; init; }

        public required IReadOnlyList<RetryStorm> RetryStorms { get; init; }

        /// <summary>A successful block-task call with a non-empty <c>blockedBy</c> was made in this thread.</summary>
        public required bool BlockRecorded { get; init; }

        /// <summary>A successful block-task call with an omitted/empty <c>blockedBy</c> was made in this thread.</summary>
        public required bool BlockExplicitlyCleared { get; init; }

        public required int TaskToolCallCount { get; init; }

        /// <summary>True when this is a tool-less sub-agent thread whose assistant text still claims board work.</summary>
        public required bool FabricatedComplianceSuspect { get; init; }

        public bool IsSubAgentThread => ThreadId.StartsWith(SubAgentDirPrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<ThreadData> LoadAllThreads(string conversationsDir)
    {
        if (!Directory.Exists(conversationsDir))
        {
            return [];
        }

        var threads = new List<ThreadData>();
        foreach (
            var threadDir in Directory.EnumerateDirectories(conversationsDir).OrderBy(d => d, StringComparer.Ordinal)
        )
        {
            if (File.Exists(Path.Combine(threadDir, "messages.json")))
            {
                threads.Add(LoadThread(threadDir));
            }
        }

        return threads;
    }

    /// <summary>
    /// Groups threads into (root thread + all transitive sub-agent descendants) via the durable
    /// <c>sample.subAgentOf</c> link. Offline on purpose — it must work on an archived sweep with
    /// no host running, and it is what scopes each run's metrics when one host served many runs.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ThreadData>> GroupByRootThread(
        IReadOnlyList<ThreadData> threads
    )
    {
        var byId = threads.ToDictionary(t => t.ThreadId, StringComparer.Ordinal);
        var groups = new Dictionary<string, List<ThreadData>>(StringComparer.Ordinal);

        foreach (var thread in threads)
        {
            var root = thread;
            var hops = 0;
            while (
                root.ParentThreadId is { } parentId
                && byId.TryGetValue(parentId, out var parent)
                // A cycle in the parent links would be store corruption; cap the walk so the
                // extractor degrades to "treat as its own root" instead of hanging.
                && ++hops <= threads.Count
            )
            {
                root = parent;
            }

            if (!groups.TryGetValue(root.ThreadId, out var group))
            {
                group = [];
                groups[root.ThreadId] = group;
            }

            group.Add(thread);
        }

        return groups.ToDictionary(kvp => kvp.Key, kvp => (IReadOnlyList<ThreadData>)kvp.Value, StringComparer.Ordinal);
    }

    internal static ThreadData LoadThread(string threadDir)
    {
        var threadId = Path.GetFileName(threadDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var (parentThreadId, todoBoardJson) = ReadMetadata(Path.Combine(threadDir, "metadata.json"));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(threadDir, "messages.json")));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{threadDir}\\messages.json is not a JSON array of envelopes.");
        }

        // Pass 1 (spec): pair results by tool_call_id, within this thread only, first result wins.
        var resultById = new Dictionary<string, (string Text, bool IsErrorFlag)>(StringComparer.Ordinal);
        foreach (var envelope in doc.RootElement.EnumerateArray())
        {
            if (GetString(envelope, "messageType") != "ToolCallResultMessage")
            {
                continue;
            }

            if (TryParseInner(envelope, out var inner))
            {
                using (inner)
                {
                    var callId = GetString(inner.RootElement, "tool_call_id");
                    if (!string.IsNullOrEmpty(callId) && !resultById.ContainsKey(callId))
                    {
                        var isErrorFlag =
                            inner.RootElement.TryGetProperty("is_error", out var flag)
                            && flag.ValueKind == JsonValueKind.True;
                        resultById[callId] = (ResultText(inner.RootElement), isErrorFlag);
                    }
                }
            }
        }

        // Pass 2 (spec): calls in message order, turns, assistant texts.
        var generationIds = new HashSet<string>(StringComparer.Ordinal);
        var walk = new List<StormWalkItem>();
        var perTool = new Dictionary<string, ToolStatsBuilder>(StringComparer.Ordinal);
        var totalToolCalls = 0;
        var unpairedToolCalls = 0;
        var taskToolCalls = 0;
        var blockRecorded = false;
        var blockExplicitlyCleared = false;
        var assistantTexts = new List<string>();

        foreach (var envelope in doc.RootElement.EnumerateArray())
        {
            var messageType = GetString(envelope, "messageType");
            var generationId = GetString(envelope, "generationId");

            JsonDocument? inner = null;
            try
            {
                if (
                    (generationId is null || messageType is "ToolCallMessage" or "TextMessage")
                    && TryParseInner(envelope, out var parsed)
                )
                {
                    inner = parsed;
                }

                generationId ??= inner is null
                    ? null
                    : GetString(inner.RootElement, "generationId") ?? GetString(inner.RootElement, "generation_id");
                if (generationId is not null)
                {
                    _ = generationIds.Add(generationId);
                }

                if (
                    messageType == "TextMessage"
                    && inner is not null
                    && string.Equals(GetString(envelope, "role"), "Assistant", StringComparison.OrdinalIgnoreCase)
                    && GetString(inner.RootElement, "text") is { } text
                )
                {
                    assistantTexts.Add(text);
                }

                if (messageType != "ToolCallMessage" || inner is null)
                {
                    continue;
                }

                var tool = GetString(inner.RootElement, "function_name") ?? "";
                var callId = GetString(inner.RootElement, "tool_call_id");
                totalToolCalls++;

                (string Text, bool IsErrorFlag) result = ("", false);
                var hasResult = !string.IsNullOrEmpty(callId) && resultById.TryGetValue(callId, out result);
                if (!hasResult)
                {
                    unpairedToolCalls++;
                }

                // Defensive union per metrics-spec.md: is_error == true OR the "Error:" text
                // prefix. The text prefix is the primary signal (production records
                // is_error: false on textual errors); the flag is honoured in case the store
                // ever starts setting it.
                var isError = hasResult && (result.IsErrorFlag || IsErrorText(result.Text));

                if (!TaskTools.Contains(tool))
                {
                    continue;
                }

                taskToolCalls++;
                if (!perTool.TryGetValue(tool, out var stats))
                {
                    stats = new ToolStatsBuilder();
                    perTool[tool] = stats;
                }

                stats.Calls++;
                if (isError)
                {
                    stats.Errors++;
                }

                var rawArgs = GetString(inner.RootElement, "function_args");
                var canonical = JsonCanonicalizer.CanonicalizeArgs(rawArgs ?? "");
                walk.Add(new StormWalkItem(RetryStormDetector.MakeIdentity(tool, canonical), isError, hasResult));

                if (!isError && hasResult && tool == "block-task")
                {
                    if (HasNonEmptyBlockedBy(rawArgs))
                    {
                        blockRecorded = true;
                    }
                    else
                    {
                        blockExplicitlyCleared = true;
                    }
                }
            }
            finally
            {
                inner?.Dispose();
            }
        }

        var fabricated =
            threadId.StartsWith(SubAgentDirPrefix, StringComparison.OrdinalIgnoreCase)
            && taskToolCalls == 0
            && assistantTexts.Any(t => ClaimVerb.IsMatch(t) && ClaimNoun.IsMatch(t));

        return new ThreadData
        {
            ThreadId = threadId,
            ParentThreadId = parentThreadId,
            TodoBoardJson = todoBoardJson,
            TurnCount = generationIds.Count,
            TotalToolCalls = totalToolCalls,
            UnpairedToolCalls = unpairedToolCalls,
            PerTaskTool = perTool.ToDictionary(
                kvp => kvp.Key,
                kvp => new ToolStats { Calls = kvp.Value.Calls, Errors = kvp.Value.Errors },
                StringComparer.Ordinal
            ),
            RetryStorms = RetryStormDetector.Walk(threadId, walk),
            BlockRecorded = blockRecorded,
            BlockExplicitlyCleared = blockExplicitlyCleared,
            TaskToolCallCount = taskToolCalls,
            FabricatedComplianceSuspect = fabricated,
        };
    }

    /// <summary>Error result per spec: text (leading whitespace removed) starts with <c>Error:</c>, ordinal.</summary>
    internal static bool IsErrorText(string resultText) =>
        resultText.TrimStart().StartsWith("Error:", StringComparison.Ordinal);

    /// <summary>The result's text: the string value itself, or compact JSON for a non-string value.</summary>
    private static string ResultText(JsonElement inner)
    {
        if (!inner.TryGetProperty("result", out var result))
        {
            return "";
        }

        return result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : result.GetRawText();
    }

    private static bool HasNonEmptyBlockedBy(string? rawArgs)
    {
        if (string.IsNullOrEmpty(rawArgs))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawArgs);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("blockedBy", out var blockedBy)
                && blockedBy.ValueKind == JsonValueKind.Array
                && blockedBy.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseInner(JsonElement envelope, out JsonDocument inner)
    {
        if (
            envelope.TryGetProperty("messageJson", out var messageJson)
            && messageJson.ValueKind == JsonValueKind.String
        )
        {
            try
            {
                inner = JsonDocument.Parse(messageJson.GetString()!);
                return true;
            }
            catch (JsonException)
            {
                // A malformed inner message must not sink the whole extraction.
            }
        }

        inner = null!;
        return false;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static (string? ParentThreadId, string? TodoBoardJson) ReadMetadata(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            return (null, null);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
        if (
            !doc.RootElement.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
        )
        {
            return (null, null);
        }

        string? parent = null;
        string? board = null;
        foreach (var property in properties.EnumerateObject())
        {
            if (property.NameEquals(SubAgentParentKey) && property.Value.ValueKind == JsonValueKind.String)
            {
                parent = property.Value.GetString();
            }
            else if (property.NameEquals(TodoBoardKey) && property.Value.ValueKind == JsonValueKind.String)
            {
                board = property.Value.GetString();
            }
        }

        return (parent, board);
    }

    private sealed class ToolStatsBuilder
    {
        public int Calls;
        public int Errors;
    }
}
