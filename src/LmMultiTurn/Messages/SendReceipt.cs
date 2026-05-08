namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Receipt returned immediately when input is accepted into the queue.
/// Does NOT guarantee run assignment - that comes later via RunAssignmentMessage on the output stream.
/// </summary>
/// <param name="ReceiptId">Unique ID for this submission (used for correlation)</param>
/// <param name="InputId">Echoed back if client provided</param>
/// <param name="QueuedAt">Timestamp when the input was queued</param>
public record SendReceipt(
    string ReceiptId,
    string? InputId = null,
    DateTimeOffset QueuedAt = default);
