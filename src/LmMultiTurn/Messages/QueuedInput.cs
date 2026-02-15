namespace AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

/// <summary>
/// Internal representation of a queued input item.
/// Contains the original UserInput plus queue metadata.
/// </summary>
/// <param name="Input">The original user input</param>
/// <param name="ReceiptId">Unique ID for this queued item (matches SendReceipt.ReceiptId)</param>
/// <param name="QueuedAt">Timestamp when the input was queued</param>
public record QueuedInput(
    UserInput Input,
    string ReceiptId,
    DateTimeOffset QueuedAt);
