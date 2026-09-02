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

    /// <summary>Per-spawn phase timings the host stamps onto each sub-agent thread (#670 seam).</summary>
    public const string SpawnTimingsKey = "sample.spawnTimings";

    /// <summary>Host-side startup and directory work, stamped onto the root conversation.</summary>
    public const string StartupWorkKey = "sample.startupWork";
    public const string SubAgentDirPrefix = "subagent-";

    /// <summary>The error code reported when a failing result names none (metrics-spec.md).</summary>
    public const string UnclassifiedErrorCode = "unclassified";

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

        /// <summary>
        ///     Per tool (calls, errors, errors by code). Only the <c>task</c> and <c>coordination</c>
        ///     families get a row; <c>other</c> tools count in <see cref="TotalToolCalls" /> alone.
        /// </summary>
        public required IReadOnlyDictionary<string, ToolStats> PerTool { get; init; }

        public required IReadOnlyList<RetryStorm> RetryStorms { get; init; }

        /// <summary>A successful block-task call with a non-empty <c>blockedBy</c> was made in this thread.</summary>
        public required bool BlockRecorded { get; init; }

        /// <summary>A successful block-task call with an omitted/empty <c>blockedBy</c> was made in this thread.</summary>
        public required bool BlockExplicitlyCleared { get; init; }

        public required int TaskToolCallCount { get; init; }

        /// <summary>Calls to the 7 coordination tools, the coordination twin of <see cref="TaskToolCallCount" />.</summary>
        public required int CoordinationToolCallCount { get; init; }

        /// <summary>
        ///     The thread's distinct generation ids - the keys the usage reader's best-effort turn
        ///     join matches a record's attempt key against.
        /// </summary>
        public required IReadOnlyCollection<string> GenerationIds { get; init; }

        /// <summary>Raw <c>usage.records</c> property value from <c>metadata.json</c>, or null when absent.</summary>
        public string? UsageRecordsJson { get; init; }

        /// <summary>Raw <c>sample.spawnTimings</c> property value, or null when the host stamped none.</summary>
        public string? SpawnTimingsJson { get; init; }

        /// <summary>Raw <c>sample.startupWork</c> property value, or null when the host stamped none.</summary>
        public string? StartupWorkJson { get; init; }

        /// <summary>
        ///     How each <c>WaitAgent</c>/<c>WaitForAgents</c> call ended, keyed by the result's
        ///     <c>status</c> (or its error code). Empty on every build before #673/#674 emit those
        ///     states - a real counter reading zero, not a hardcoded zero.
        /// </summary>
        public required CountMap WaitOutcomes { get; init; }

        /// <summary>
        ///     The last <c>openObligations</c> value any coordination result carried, and how many
        ///     results carried the field at all. Zero/zero until #673 starts emitting it.
        /// </summary>
        public required int OpenObligationsLastObserved { get; init; }

        public required int OpenObligationResults { get; init; }

        /// <summary>True when this is a tool-less sub-agent thread whose assistant text still claims board work.</summary>
        public required bool FabricatedComplianceSuspect { get; init; }

        /// <summary>
        ///     Board rows this thread minted, never deleted, and later got a not-found for (#621 Part B).
        ///     A LOWER bound on real losses — see <see cref="BoardIdLedger" /> for the three gaps.
        /// </summary>
        public required IReadOnlyList<BoardIdVanish> BoardIdVanishes { get; init; }

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
        var metadata = ReadMetadata(Path.Combine(threadDir, "metadata.json"));

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(threadDir, "messages.json")));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{threadDir}\\messages.json is not a JSON array of envelopes.");
        }

        // Pass 1 (spec): pair results by tool_call_id, within this thread only, first result wins.
        var resultById = new Dictionary<string, ResultFacts>(StringComparer.Ordinal);
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
                        resultById[callId] = ResultFacts.Read(inner.RootElement);
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
        var coordinationToolCalls = 0;
        var blockRecorded = false;
        var blockExplicitlyCleared = false;
        var claimSignals = new List<(bool Verb, bool Noun)>();
        var coordination = new CoordinationOutcomes();
        var ledger = new BoardIdLedger();
        var vanishes = new List<BoardIdVanish>();

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
                    && ClaimSignals(inner.RootElement) is { } signals
                )
                {
                    claimSignals.Add(signals);
                }

                if (messageType != "ToolCallMessage" || inner is null)
                {
                    continue;
                }

                var tool = GetString(inner.RootElement, "function_name") ?? "";
                var callId = GetString(inner.RootElement, "tool_call_id");
                totalToolCalls++;

                var result = ResultFacts.None;
                var hasResult = !string.IsNullOrEmpty(callId) && resultById.TryGetValue(callId, out result);
                if (!hasResult)
                {
                    unpairedToolCalls++;
                }

                var family = ToolFamilies.Classify(tool);

                // Defensive union per metrics-spec.md: is_error == true OR the "Error:" text
                // prefix. The text prefix is the primary signal for the task family (production
                // records is_error: false on textual errors); the flag is honoured in case the
                // store ever starts setting it. A coordination refusal carries NO "Error:" prefix
                // - it is is_error plus an error_code - so the code's presence is a third disjunct
                // THERE ONLY, which leaves the 15 task tools' historical counts (and their
                // comparability with the archived baseline) untouched.
                var isError =
                    hasResult
                    && (
                        result.IsErrorFlag
                        || IsErrorText(result.Text)
                        || (family == ToolFamily.Coordination && result.ErrorCode is not null)
                    );

                if (family == ToolFamily.Other)
                {
                    continue;
                }

                if (family == ToolFamily.Task)
                {
                    taskToolCalls++;
                }
                else
                {
                    coordinationToolCalls++;
                    coordination.Record(tool, result, isError);
                }

                if (!perTool.TryGetValue(tool, out var stats))
                {
                    stats = new ToolStatsBuilder();
                    perTool[tool] = stats;
                }

                stats.Calls++;
                if (isError)
                {
                    stats.Errors++;

                    // A coordination refusal ALWAYS lands in the tally, falling back to
                    // "unclassified" when the code is missing, so a reader can never confuse "no
                    // refusals" with "refusals we failed to classify". A task error is tallied only
                    // when it really carries a code: the 15 board tools report errors as "Error:"
                    // text today, and blanketing them as unclassified would bury the coordination
                    // taxonomy under a hundred meaningless rows.
                    var code = result.ErrorCode ?? (family == ToolFamily.Coordination ? UnclassifiedErrorCode : null);
                    if (code is not null)
                    {
                        stats.ErrorCodes[code] = stats.ErrorCodes.TryGetValue(code, out var seen) ? seen + 1 : 1;
                    }
                }

                var rawArgs = GetString(inner.RootElement, "function_args");
                var canonical = JsonCanonicalizer.CanonicalizeArgs(rawArgs ?? "");

                // Coordination calls join the storm walk on the same terms as task calls. The walk's
                // own isError guard IS the polling exemption: a repeated call whose result is a
                // non-error (status running/timeout/question_received) is an observation and can
                // never extend a run.
                walk.Add(new StormWalkItem(RetryStormDetector.MakeIdentity(tool, canonical), isError, hasResult));

                // Everything below is board bookkeeping and applies to the task family alone.
                if (family == ToolFamily.Coordination)
                {
                    continue;
                }

                // Board-loss watch (#621 Part B). Same discrimination line as the server detector: a
                // not-found naming an id THIS thread minted and never deleted is a lost row; anything
                // else is a model typo and stays unreported.
                if (hasResult)
                {
                    if (isError)
                    {
                        if (BoardIdLedger.NotFoundTaskId(result.Text) is { } vanishedId && ledger.Owns(vanishedId))
                        {
                            vanishes.Add(
                                new BoardIdVanish
                                {
                                    ThreadId = threadId,
                                    TaskId = vanishedId,
                                    Tool = tool,
                                }
                            );
                        }
                    }
                    else
                    {
                        ledger.RecordSuccess(tool, result.Text);
                    }
                }

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
            && claimSignals.Any(c => c.Verb && c.Noun);

        return new ThreadData
        {
            ThreadId = threadId,
            ParentThreadId = metadata.ParentThreadId,
            TodoBoardJson = metadata.TodoBoardJson,
            TurnCount = generationIds.Count,
            TotalToolCalls = totalToolCalls,
            UnpairedToolCalls = unpairedToolCalls,
            PerTool = perTool.ToDictionary(
                kvp => kvp.Key,
                kvp => new ToolStats
                {
                    Calls = kvp.Value.Calls,
                    Errors = kvp.Value.Errors,
                    ErrorCodes = CountMap.From(kvp.Value.ErrorCodes),
                },
                StringComparer.Ordinal
            ),
            RetryStorms = RetryStormDetector.Walk(threadId, walk),
            BlockRecorded = blockRecorded,
            BlockExplicitlyCleared = blockExplicitlyCleared,
            TaskToolCallCount = taskToolCalls,
            CoordinationToolCallCount = coordinationToolCalls,
            GenerationIds = generationIds,
            UsageRecordsJson = metadata.UsageRecordsJson,
            SpawnTimingsJson = metadata.SpawnTimingsJson,
            StartupWorkJson = metadata.StartupWorkJson,
            WaitOutcomes = coordination.WaitOutcomes,
            OpenObligationsLastObserved = coordination.OpenObligationsLastObserved,
            OpenObligationResults = coordination.OpenObligationResults,
            FabricatedComplianceSuspect = fabricated,
            BoardIdVanishes = vanishes,
        };
    }

    /// <summary>Error result per spec: text (leading whitespace removed) starts with <c>Error:</c>, ordinal.</summary>
    internal static bool IsErrorText(string resultText) =>
        resultText.TrimStart().StartsWith("Error:", StringComparison.Ordinal);

    /// <summary>
    /// The fabricated-compliance signals for one assistant <c>TextMessage</c>. A raw transcript is
    /// matched with the spec's two regexes; a REDACTED transcript carries the same two booleans
    /// (plus a length) in place of the text, so the heuristic survives redaction unchanged. Returns
    /// null when the message carries no text at all.
    /// </summary>
    private static (bool Verb, bool Noun)? ClaimSignals(JsonElement inner)
    {
        if (!inner.TryGetProperty("text", out var text))
        {
            return null;
        }

        if (text.ValueKind == JsonValueKind.String)
        {
            var raw = text.GetString() ?? "";
            return (ClaimVerb.IsMatch(raw), ClaimNoun.IsMatch(raw));
        }

        return text.ValueKind == JsonValueKind.Object
            ? (
                text.TryGetProperty("claimVerbMatch", out var verb) && verb.ValueKind == JsonValueKind.True,
                text.TryGetProperty("claimNounMatch", out var noun) && noun.ValueKind == JsonValueKind.True
            )
            : null;
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

    /// <summary>The properties a thread's <c>metadata.json</c> contributes to the metrics.</summary>
    internal sealed record ThreadMetadata
    {
        public string? ParentThreadId { get; init; }
        public string? TodoBoardJson { get; init; }
        public string? UsageRecordsJson { get; init; }
        public string? SpawnTimingsJson { get; init; }
        public string? StartupWorkJson { get; init; }
    }

    private static ThreadMetadata ReadMetadata(string metadataPath)
    {
        if (!File.Exists(metadataPath))
        {
            return new ThreadMetadata();
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(metadataPath));
        if (
            !doc.RootElement.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
        )
        {
            return new ThreadMetadata();
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
        {
            // Every one of these is written by the host as a JSON STRING; an archive rewritten by a
            // tool that inlined one as an object must keep parsing, so both shapes are accepted.
            values[property.Name] =
                property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()
                : property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? property.Value.GetRawText()
                : null;
        }

        return new ThreadMetadata
        {
            ParentThreadId = Value(values, SubAgentParentKey),
            TodoBoardJson = Value(values, TodoBoardKey),
            UsageRecordsJson = Value(values, UsageReader.RecordsPropertyKey),
            SpawnTimingsJson = Value(values, SpawnTimingsKey),
            StartupWorkJson = Value(values, StartupWorkKey),
        };

        static string? Value(IReadOnlyDictionary<string, string?> values, string key) =>
            values.TryGetValue(key, out var value) ? value : null;
    }

    /// <summary>
    /// The wait/obligation counters one thread accumulates. They read zero on every build before
    /// #673/#674 emit those states, which is a measured zero rather than a hardcoded one - the day
    /// the host starts emitting a status or an openObligations field, these numbers move on their
    /// own with no further change here.
    /// </summary>
    private sealed class CoordinationOutcomes
    {
        private readonly Dictionary<string, int> _waitOutcomes = new(StringComparer.Ordinal);

        public CountMap WaitOutcomes => CountMap.From(_waitOutcomes);
        public int OpenObligationsLastObserved { get; private set; }
        public int OpenObligationResults { get; private set; }

        public void Record(string tool, ResultFacts result, bool isError)
        {
            if (result.TryReadNumber("openObligations", out var open))
            {
                OpenObligationsLastObserved = open;
                OpenObligationResults++;
            }

            if (tool is not ("WaitAgent" or "WaitForAgents"))
            {
                return;
            }

            var outcome =
                isError ? result.ErrorCode ?? UnclassifiedErrorCode
                : result.TryReadString("status", out var status) ? status
                : "ok";
            _waitOutcomes[outcome] = _waitOutcomes.TryGetValue(outcome, out var seen) ? seen + 1 : 1;
        }
    }

    private sealed class ToolStatsBuilder
    {
        public int Calls;
        public int Errors;
        public readonly Dictionary<string, int> ErrorCodes = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// Everything one paired <c>ToolCallResultMessage</c> contributes: the result text, the persisted
/// <c>is_error</c> flag, and the stable error code the score object classifies failures by.
/// </summary>
/// <remarks>
/// Error code per metrics-spec.md, in order: the result message's own <c>error_code</c>
/// (what <c>ToolHandlerResult.FromError(text, code)</c> persists), else a <c>code</c> property when
/// the result itself parses to a JSON object, else none - which the caller reports as
/// <c>unclassified</c> rather than omitting, so a reader who skips the definitions still sees that
/// the failure happened and was simply not classifiable.
/// </remarks>
internal readonly record struct ResultFacts(string Text, bool IsErrorFlag, string? ErrorCode)
{
    public static readonly ResultFacts None = new("", false, null);

    public static ResultFacts Read(JsonElement resultMessage)
    {
        var text = ResultText(resultMessage);
        var isErrorFlag =
            resultMessage.TryGetProperty("is_error", out var flag) && flag.ValueKind == JsonValueKind.True;

        string? code = null;
        if (
            resultMessage.TryGetProperty("error_code", out var errorCode)
            && errorCode.ValueKind == JsonValueKind.String
        )
        {
            code = errorCode.GetString();
        }

        return new ResultFacts(text, isErrorFlag, code ?? ReadResultProperty(text, "code"));
    }

    /// <summary>Reads a string property out of a result whose text is a JSON object.</summary>
    public bool TryReadString(string propertyName, out string value)
    {
        value = ReadResultProperty(Text, propertyName) ?? "";
        return value.Length > 0;
    }

    /// <summary>Reads an integer property out of a result whose text is a JSON object.</summary>
    public bool TryReadNumber(string propertyName, out int value)
    {
        value = 0;
        if (!LooksLikeJsonObject(Text))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(Text);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out var found)
                && found.ValueKind == JsonValueKind.Number
                && found.TryGetInt32(out value);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The result's text: the string value itself, or compact JSON for a non-string value.</summary>
    private static string ResultText(JsonElement resultMessage)
    {
        if (!resultMessage.TryGetProperty("result", out var result))
        {
            return "";
        }

        return result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : result.GetRawText();
    }

    private static string? ReadResultProperty(string text, string propertyName)
    {
        if (!LooksLikeJsonObject(text))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return
                doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out var found)
                && found.ValueKind == JsonValueKind.String
                ? found.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Cheap gate before parsing. Most results are prose, and handing every one of them to
    /// JsonDocument.Parse just to catch the exception is the hot path of a whole-sweep extraction.
    /// </summary>
    private static bool LooksLikeJsonObject(string text) => text.TrimStart().StartsWith('{');
}
