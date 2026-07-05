using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// The 5 top-level states a headless REST caller can observe for a run, distinct from the
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
    /// source of truth (see plan decisions.md, "FinalResponse derivation"). Tool-only-run convention:
    /// prefers the last assistant <see cref="TextMessage"/> produced by the run; if the run produced
    /// no text (a pure tool-call run), falls back to the last message of any type so a Completed/Errored
    /// run never resolves to a null response as long as it produced at least one message.
    /// </summary>
    private async Task<object?> LoadFinalResponseAsync(string threadId, string runId, CancellationToken ct)
    {
        var messages = await conversationStore.LoadMessagesAsync(threadId, ct);
        var runMessages = messages.Where(m => m.RunId == runId).ToList();
        if (runMessages.Count == 0)
        {
            return null;
        }

        var textMessage = runMessages.LastOrDefault(m => m.MessageType == nameof(TextMessage));
        return DeserializeMessage(textMessage ?? runMessages[^1]);
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
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown RunStatus value."),
    };
}
