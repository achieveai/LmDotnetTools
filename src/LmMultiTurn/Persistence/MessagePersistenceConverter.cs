using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Converts between IMessage and PersistedMessage for storage and retrieval.
/// </summary>
public static class MessagePersistenceConverter
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new IMessageJsonConverter() },
    };

    /// <summary>
    /// Converts an IMessage to a PersistedMessage for storage.
    /// </summary>
    /// <param name="message">The message to convert.</param>
    /// <param name="threadId">The thread ID (uses message.ThreadId if available, otherwise this value).</param>
    /// <param name="runId">The run ID (uses message.RunId if available, otherwise this value).</param>
    /// <param name="jsonOptions">Optional JSON serializer options. Defaults to snake_case with IMessageJsonConverter.</param>
    /// <returns>A PersistedMessage ready for storage.</returns>
    public static PersistedMessage ToPersistedMessage(
        IMessage message,
        string threadId,
        string runId,
        JsonSerializerOptions? jsonOptions = null
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = jsonOptions ?? DefaultOptions;
        var messageJson = JsonSerializer.Serialize(message, message.GetType(), options);
        var effectiveThreadId = message.ThreadId ?? threadId;

        return new PersistedMessage
        {
            // Deterministic Id for ToolCallResultMessage with a non-empty ToolCallId so that
            // ReplaceMessageAsync can address the row without an in-memory index. Other message
            // types keep a random Id since they're append-only.
            Id = BuildPersistedId(message, effectiveThreadId),
            ThreadId = effectiveThreadId,
            RunId = message.RunId ?? runId,
            ParentRunId = message.ParentRunId,
            GenerationId = message.GenerationId,
            MessageOrderIdx = message.MessageOrderIdx,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MessageType = message.GetType().Name,
            Role = message.Role.ToString(),
            FromAgent = message.FromAgent,
            MessageJson = messageJson,
        };
    }

    /// <summary>
    /// Constructs the deterministic persisted-Id for a <see cref="ToolCallResultMessage"/> with
    /// a non-empty ToolCallId. Used by <c>MultiTurnAgentBase</c> when calling
    /// <see cref="IConversationStore.ReplaceMessageAsync"/> on deferred-tool resolution.
    /// </summary>
    public static string BuildToolResultPersistedId(string threadId, string toolCallId)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        return $"tcr:{threadId}:{toolCallId}";
    }

    private static string BuildPersistedId(IMessage message, string threadId)
    {
        return message is ToolCallResultMessage tcr && !string.IsNullOrEmpty(tcr.ToolCallId)
            ? BuildToolResultPersistedId(threadId, tcr.ToolCallId)
            : Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Converts a PersistedMessage back to an IMessage.
    /// </summary>
    /// <param name="persisted">The persisted message to convert.</param>
    /// <param name="jsonOptions">Optional JSON serializer options. Defaults to snake_case with IMessageJsonConverter.</param>
    /// <returns>The deserialized IMessage.</returns>
    /// <exception cref="JsonException">Thrown if deserialization fails.</exception>
    public static IMessage FromPersistedMessage(PersistedMessage persisted, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        var options = jsonOptions ?? DefaultOptions;

        var message = JsonSerializer.Deserialize<IMessage>(persisted.MessageJson, options);

        return message ?? throw new JsonException($"Failed to deserialize message {persisted.Id}");
    }

    /// <summary>
    /// Converts multiple IMessages to PersistedMessages.
    /// </summary>
    public static IReadOnlyList<PersistedMessage> ToPersistedMessages(
        IEnumerable<IMessage> messages,
        string threadId,
        string runId,
        JsonSerializerOptions? jsonOptions = null
    )
    {
        return [.. messages.Select(m => ToPersistedMessage(m, threadId, runId, jsonOptions))];
    }

    /// <summary>
    /// Converts multiple PersistedMessages back to IMessages.
    /// </summary>
    /// <remarks>
    /// ALL-OR-NOTHING: one undeserializable row aborts the whole batch and no partial result is
    /// returned. That is correct only where a caller genuinely cannot proceed on partial history.
    /// Every reader that restores a conversation for USE wants
    /// <see cref="FromPersistedMessagesResilient"/> instead — see its remarks for why.
    /// </remarks>
    public static IReadOnlyList<IMessage> FromPersistedMessages(
        IEnumerable<PersistedMessage> persistedMessages,
        JsonSerializerOptions? jsonOptions = null
    )
    {
        return [.. persistedMessages.Select(p => FromPersistedMessage(p, jsonOptions))];
    }

    /// <summary>
    /// Converts persisted rows back to IMessages degrading PER-RECORD, then removes any tool
    /// call/result left without its partner, so a history assembled from a partially-readable store is
    /// still safe to hand to a provider.
    /// </summary>
    /// <param name="persistedMessages">The rows to restore, in load order. Order is preserved.</param>
    /// <param name="onSkipped">
    /// Invoked once per row that could not be converted, with the row and the failure. The caller owns
    /// the reporting because the two failure categories mean different things to an operator: an
    /// <see cref="UnknownMessageTypeDiscriminatorException"/> is well-formed data written by a NEWER
    /// binary (a rollback window, not corruption), anything else is a damaged row. Passing
    /// <c>null</c> drops rows silently and should be reserved for callers that have no logger at all.
    /// </param>
    /// <param name="jsonOptions">Optional JSON serializer options.</param>
    /// <param name="cancellationToken">
    /// Cancellation is never swallowed as corruption. INERT today — <see cref="FromPersistedMessage"/>
    /// is fully synchronous and <c>IMessageJsonConverter</c> has no cancellable surface — but the
    /// convention stays true if one ever gains that surface, rather than being rediscovered as a
    /// silent-drop bug then.
    /// </param>
    /// <remarks>
    /// <para>
    /// WHY BOTH HALVES ARE ONE METHOD (#489, #495, #498). The per-record skip and the pairing sweep are
    /// not independently useful, because the skip is what CREATES the shape the sweep repairs: a tool
    /// call and its result are two separate rows (<see cref="ToPersistedMessage"/> is strictly 1:1), so
    /// dropping either one orphans the other. Nothing downstream repairs that —
    /// <c>MessageTransformationMiddleware.TryCreateToolCallAggregate</c> returns null when only one half
    /// is present and its caller passes the unpaired message through verbatim — and every provider
    /// rejects the resulting shape with a 400. Exposing the skip without the sweep would hand callers a
    /// fix that trades a total failure for a subtler one, so they are deliberately not separable.
    /// </para>
    /// <para>
    /// The sweep runs UNCONDITIONALLY, not only when a row was skipped, because a second and older
    /// route produces the same orphan with no corruption involved: <c>MultiTurnAgentBase</c>
    /// .<c>PersistMessageAsync</c> appends one row at a time and swallows an append failure, leaving a
    /// permanently half-written exchange in the store. The sweep is blind to WHICH route it was — it
    /// asks only whether a partner is present — so one mechanism closes both.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<IMessage> FromPersistedMessagesResilient(
        IReadOnlyList<PersistedMessage> persistedMessages,
        Action<PersistedMessage, Exception>? onSkipped = null,
        JsonSerializerOptions? jsonOptions = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(persistedMessages);

        var converted = new List<IMessage>(persistedMessages.Count);
        foreach (var persisted in persistedMessages)
        {
            IMessage message;
            try
            {
                message = FromPersistedMessage(persisted, jsonOptions);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                onSkipped?.Invoke(persisted, ex);
                continue;
            }

            converted.Add(message);
        }

        return DropUnpairedToolMessages(converted);
    }

    /// <summary>
    /// Returns <paramref name="messages"/> with every tool-call/tool-result message that has lost its
    /// partner removed, preserving the relative order of everything kept.
    /// </summary>
    /// <remarks>
    /// One message may carry SEVERAL tool call ids, so dropping one result can invalidate a message
    /// that also holds calls whose results ARE present — which in turn orphans those results. The
    /// sweep therefore iterates to a fixed point rather than making a single pass. Ids are the only
    /// key used: a message with no id on either side takes no part in pairing and is always kept,
    /// because it cannot be matched either way and inventing a verdict would delete history on a
    /// guess. Duplicate results for one call are fine — pairing asks whether a partner EXISTS, not
    /// how many.
    /// <para>
    /// DEPENDENCY, stated because it lives in another assembly and nothing else links the two:
    /// <c>ToolsCallAggregateMessage</c> is deliberately NOT handled below, and that is only safe
    /// because <c>MessageTransformationMiddleware</c> (LmCore, MessageTransformationMiddleware.cs:411)
    /// throws <see cref="NotSupportedException"/> rather than let an aggregate through message-order
    /// assignment, so one can never reach persistence. An aggregate carries its call and its result by
    /// COMPOSITION: it matches neither arm of <see cref="ToolCallIdsOf"/> nor
    /// <see cref="ToolResultIdsOf"/>, would contribute zero ids, and would therefore look like a
    /// message that takes no part in pairing while the separate row answering it got dropped as
    /// unpaired. If that guard is ever relaxed, this sweep needs an aggregate arm on BOTH extractors
    /// before the aggregate can reach a store.
    /// </para>
    /// </remarks>
    private static List<IMessage> DropUnpairedToolMessages(IReadOnlyList<IMessage> messages)
    {
        var keep = new bool[messages.Count];
        Array.Fill(keep, true);

        bool changed;
        do
        {
            changed = false;

            var resultIds = new HashSet<string>(StringComparer.Ordinal);
            var callIds = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < messages.Count; i++)
            {
                if (keep[i])
                {
                    resultIds.UnionWith(ToolResultIdsOf(messages[i]));
                    callIds.UnionWith(ToolCallIdsOf(messages[i]));
                }
            }

            for (var i = 0; i < messages.Count; i++)
            {
                if (!keep[i])
                {
                    continue;
                }

                var isUnpaired =
                    ToolResultIdsOf(messages[i]).Any(id => !callIds.Contains(id))
                    || ToolCallIdsOf(messages[i]).Any(id => !resultIds.Contains(id));

                if (isUnpaired)
                {
                    keep[i] = false;
                    changed = true;
                }
            }
        } while (changed);

        var kept = new List<IMessage>(messages.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            if (keep[i])
            {
                kept.Add(messages[i]);
            }
        }

        return kept;
    }

    /// <summary>
    /// The tool call ids a restored message REQUESTS. Both shapes are handled deliberately: the
    /// singular <see cref="ToolCallMessage"/> is what production persists (
    /// <c>MessageTransformationMiddleware</c> splits a plural message into one per call upstream of
    /// the loop's turn body), while a plural <see cref="ICanGetToolCalls"/> can still reach the store
    /// through the middleware's unsplit passthrough and through the sibling Claude/Codex/Copilot
    /// loops, which persist their translated streams directly.
    /// </summary>
    private static IEnumerable<string> ToolCallIdsOf(IMessage message)
    {
        return message switch
        {
            // Checked before ICanGetToolCalls-style plural shapes: ToolCallMessage IS-A ToolCall and
            // carries its id directly rather than through a collection.
            ToolCallMessage single => WithId(single.ToolCallId),
            ICanGetToolCalls many => Usable((many.GetToolCalls() ?? []).Select(tc => tc.ToolCallId)),
            _ => [],
        };
    }

    /// <summary>The tool call ids a restored message ANSWERS.</summary>
    private static IEnumerable<string> ToolResultIdsOf(IMessage message)
    {
        return message switch
        {
            ToolCallResultMessage single => WithId(single.ToolCallId),
            ToolsCallResultMessage many => Usable(many.ToolCallResults.Select(r => r.ToolCallId)),
            _ => [],
        };
    }

    /// <summary>
    /// A single id, or nothing when there is no USABLE id to pair on.
    /// </summary>
    /// <remarks>
    /// <c>IsNullOrWhiteSpace</c>, not <c>IsNullOrEmpty</c>, and the difference is load-bearing:
    /// <c>AnthropicRequest.IsExpectedMissingToolCallId</c> (AnthropicRequest.cs:593) uses
    /// <c>string.IsNullOrWhiteSpace(toolCallId)</c> to decide that a provider-server tool call is
    /// LEGITIMATELY id-less and must not be treated as an error. If this sweep called a
    /// whitespace-only id "present" while Anthropic calls it "absent", the sweep would look for a
    /// partner that by Anthropic's own rules is never written, and delete a message the provider was
    /// perfectly happy to receive. The two predicates have to agree, and they now do.
    /// Keeping is always the safe direction here: an unpairable message is passed through untouched
    /// rather than deleted on a guess.
    /// </remarks>
    private static IEnumerable<string> WithId(string? id) => string.IsNullOrWhiteSpace(id) ? [] : [id];

    /// <summary>The usable ids among <paramref name="ids"/>. See <see cref="WithId"/> on the predicate.</summary>
    private static IEnumerable<string> Usable(IEnumerable<string?> ids) =>
        ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!);
}
