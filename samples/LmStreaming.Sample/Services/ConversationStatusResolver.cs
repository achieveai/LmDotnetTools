using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The 6 top-level states a headless REST caller can observe for a run, distinct from the
/// internal <see cref="RunStatus"/> ledger enum: <see cref="RunStatus.Queued"/> is surfaced as
/// <see cref="NotStarted"/> (an external caller has no use for the internal mint-vs-start
/// distinction, and "queued" also covers an accepted input that has no ledger row yet).
/// </summary>
public enum ConversationRunStatus
{
    NotStarted,
    InProgress,
    Completed,
    Errored,
    Interrupted,
    Cancelled,
}

/// <summary>
/// A resolved run status, ready to shape into <see cref="LmStreaming.Sample.Models.ConversationStatusResponse"/>.
/// </summary>
public sealed record ConversationStatusResult(
    string ThreadId,
    string? RunId,
    ConversationRunStatus Status,
    object? Response);

/// <summary>
/// Resolves the polled status of a conversation run from persisted state (<see cref="IRunLedgerStore"/>
/// + <see cref="IConversationStore"/>) — no in-memory/live-agent dependency, so a poll after process
/// restart sees exactly what a live poll would see.
/// </summary>
public sealed class ConversationStatusResolver(IConversationStore conversationStore, IRunLedgerStore runLedgerStore)
{
    private static readonly JsonSerializerOptions ResponseMessageOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new IMessageJsonConverter() },
    };

    /// <summary>
    /// Resolves status by run id. Returns null if no ledger row exists for that run id under the
    /// given thread — callers map that to a 404.
    /// </summary>
    public async Task<ConversationStatusResult?> ResolveByRunIdAsync(
        string threadId,
        string runId,
        CancellationToken ct = default)
    {
        var entry = await runLedgerStore.LoadRunLedgerAsync(runId, ct);
        if (entry == null || entry.ThreadId != threadId)
        {
            return null;
        }

        return await BuildResultAsync(entry, ct);
    }

    /// <summary>
    /// Resolves status by input id. Three outcomes: (1) a ledger row already folds this input id in
    /// — resolve its status normally; (2) no ledger row yet, but the input id was durably accepted
    /// onto this thread — resolves <see cref="ConversationRunStatus.NotStarted"/> (queued, not yet
    /// drained into a run); (3) the input id is neither accepted nor in any ledger row — returns null,
    /// which callers map to a 404 distinct from an unknown threadId.
    /// </summary>
    public async Task<ConversationStatusResult?> ResolveByInputIdAsync(
        string threadId,
        string inputId,
        CancellationToken ct = default)
    {
        var entries = await runLedgerStore.ListRunLedgerAsync(threadId, ct);
        var match = entries.FirstOrDefault(e => e.InputIds.Contains(inputId));
        if (match != null)
        {
            return await BuildResultAsync(match, ct);
        }

        var acceptedInputIds = await runLedgerStore.ListAcceptedInputIdsAsync(threadId, ct);
        if (acceptedInputIds.Contains(inputId))
        {
            return new ConversationStatusResult(threadId, null, ConversationRunStatus.NotStarted, null);
        }

        return null;
    }

    private async Task<ConversationStatusResult> BuildResultAsync(RunLedgerEntry entry, CancellationToken ct)
    {
        var status = ToConversationRunStatus(entry.Status);
        object? response = null;
        if (status is ConversationRunStatus.Completed or ConversationRunStatus.Errored)
        {
            response = await LoadFinalResponseAsync(entry.ThreadId, entry.RunId, ct);
        }

        return new ConversationStatusResult(entry.ThreadId, entry.RunId, status, response);
    }

    /// <summary>
    /// Derives the run's final response from persisted messages tagged with its run id — the single
    /// source of truth (see plan decisions.md, "FinalResponse derivation"). Prefers the last
    /// assistant, non-thinking <see cref="TextMessage"/> the run produced: a run's own user-input
    /// echo and any provider thinking/reasoning trace are ALSO persisted as <see cref="TextMessage"/>
    /// under the same run id, so a bare <c>MessageType</c> match would wrongly surface the prompt or
    /// an internal reasoning trace as the answer. If the run produced no genuine assistant answer (a
    /// pure tool-call run), falls back to the last non-user message so a Completed/Errored run still
    /// surfaces its tool activity — but never falls back to the user's own input (e.g. an errored run
    /// that never got past persisting the prompt resolves to a null response, not an echo of the ask).
    /// </summary>
    private async Task<object?> LoadFinalResponseAsync(string threadId, string runId, CancellationToken ct)
    {
        var messages = await conversationStore.LoadMessagesAsync(threadId, ct);
        var runMessages = messages.Where(m => m.RunId == runId).ToList();
        if (runMessages.Count == 0)
        {
            return null;
        }

        for (var i = runMessages.Count - 1; i >= 0; i--)
        {
            var candidate = runMessages[i];
            if (candidate.MessageType != nameof(TextMessage) || candidate.Role != nameof(Role.Assistant))
            {
                continue;
            }

            if (TryDeserializeAssistantAnswer(candidate) is { } answer)
            {
                return answer;
            }
        }

        var fallback = runMessages.LastOrDefault(m => m.Role != nameof(Role.User));
        return fallback == null ? null : DeserializeMessage(fallback);
    }

    /// <summary>
    /// Deserializes a candidate assistant <see cref="TextMessage"/> and returns its serialized form
    /// only when it's a genuine answer, not a thinking/reasoning trace. <see cref="TextMessage.IsThinking"/>
    /// isn't flattened onto <see cref="PersistedMessage"/> (unlike <see cref="PersistedMessage.Role"/>),
    /// so ruling it out requires deserializing the candidate.
    /// </summary>
    private static object? TryDeserializeAssistantAnswer(PersistedMessage message)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<IMessage>(message.MessageJson, ResponseMessageOptions);
            return msg is not TextMessage { IsThinking: false }
                ? null
                : JsonSerializer.SerializeToElement(msg, msg.GetType(), ResponseMessageOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? DeserializeMessage(PersistedMessage message)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<IMessage>(message.MessageJson, ResponseMessageOptions);
            return msg == null ? null : JsonSerializer.SerializeToElement(msg, msg.GetType(), ResponseMessageOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ConversationRunStatus ToConversationRunStatus(RunStatus status) => status switch
    {
        RunStatus.Queued => ConversationRunStatus.NotStarted,
        RunStatus.InProgress => ConversationRunStatus.InProgress,
        RunStatus.Completed => ConversationRunStatus.Completed,
        RunStatus.Errored => ConversationRunStatus.Errored,
        RunStatus.Interrupted => ConversationRunStatus.Interrupted,
        RunStatus.Cancelled => ConversationRunStatus.Cancelled,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown RunStatus value."),
    };
}
