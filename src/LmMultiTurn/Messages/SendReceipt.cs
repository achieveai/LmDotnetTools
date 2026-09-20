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
/// <param name="ActionToolsSuppressed">True only when the accepted input is guaranteed tool-free.</param>
[method: System.Text.Json.Serialization.JsonConstructor]
public record SendReceipt(
    string ReceiptId,
    string? InputId = null,
    DateTimeOffset QueuedAt = default,
    bool SpawningSuppressed = false,
    bool ActionToolsSuppressed = false
)
{
    /// <summary>Preserves the constructor used by previously compiled consumers.</summary>
    public SendReceipt(string ReceiptId, string? InputId, DateTimeOffset QueuedAt, bool SpawningSuppressed)
        : this(ReceiptId, InputId, QueuedAt, SpawningSuppressed, false) { }

    /// <summary>Preserves positional deconstruction for previously compiled consumers.</summary>
    public void Deconstruct(
        out string ReceiptId,
        out string? InputId,
        out DateTimeOffset QueuedAt,
        out bool SpawningSuppressed
    )
    {
        ReceiptId = this.ReceiptId;
        InputId = this.InputId;
        QueuedAt = this.QueuedAt;
        SpawningSuppressed = this.SpawningSuppressed;
    }
}
