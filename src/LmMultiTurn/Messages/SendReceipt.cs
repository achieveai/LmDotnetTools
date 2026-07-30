namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Receipt returned immediately when input is accepted into the queue.
/// Does NOT guarantee run assignment - that comes later via RunAssignmentMessage on the output stream.
/// </summary>
/// <param name="ReceiptId">Unique ID for this submission (used for correlation)</param>
/// <param name="InputId">Echoed back if client provided</param>
/// <param name="QueuedAt">Timestamp when the input was queued</param>
/// <param name="SpawningSuppressed">
/// <c>true</c> only when the accepting agent will ENFORCE <see cref="UserInput.SuppressSubAgentSpawning"/> on
/// the run that consumes this input. It is an enforcement statement, not an echo of what was asked for: an
/// agent that ignores the flag leaves this <c>false</c>, so a host relaying it can never advertise a guarantee
/// nothing is keeping. Callers that need the guarantee must fail closed when this is <c>false</c>.
/// </param>
public record SendReceipt(
    string ReceiptId,
    string? InputId = null,
    DateTimeOffset QueuedAt = default,
    bool SpawningSuppressed = false);
