namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Internal representation of a queued input item.
/// Contains the original UserInput plus queue metadata.
/// </summary>
/// <param name="Input">The original user input</param>
/// <param name="ReceiptId">Unique ID for this queued item (matches SendReceipt.ReceiptId)</param>
/// <param name="QueuedAt">Timestamp when the input was queued</param>
/// <param name="Resume">When non-null, this entry is an internal resume sentinel posted by the
/// loop after the last deferred tool call from a previous turn was resolved. The loop drains
/// it and starts a new run with no fresh user messages — the resolved tool_results already in
/// history feed the LLM. Real user inputs always have <c>Resume == null</c>.</param>
/// <param name="Trigger">Non-null when this entry was injected by a notify-mode trigger fire
/// (telemetry only; content is in Input.Messages, Resume is null so it drives a real run).</param>
public record QueuedInput(
    UserInput Input,
    string ReceiptId,
    DateTimeOffset QueuedAt,
    ResumeSentinel? Resume = null,
    TriggerEnvelope? Trigger = null);
