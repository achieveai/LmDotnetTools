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
    TriggerEnvelope? Trigger = null)
{
    /// <summary>
    /// Binary-compatibility overload for the pre-<see cref="Trigger"/> 4-arg positional shape.
    /// Delegates to the primary constructor with <see cref="Trigger"/> left <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <paramref name="resume"/> intentionally has no default value here: giving it one would make
    /// this constructor ambiguous with the primary constructor for a 3-arg call (both would be
    /// applicable via default-substitution, and neither is preferred by the language's tie-break
    /// rules). Omitting the default keeps this overload applicable only to genuine 4-arg calls
    /// (where it is unambiguously preferred, since it substitutes no optional parameters) while
    /// 3-arg calls still resolve — unambiguously — to the primary constructor.
    /// </remarks>
    public QueuedInput(
        UserInput input,
        string receiptId,
        DateTimeOffset queuedAt,
        ResumeSentinel? resume)
        : this(input, receiptId, queuedAt, resume, Trigger: null)
    {
    }

    /// <summary>
    /// Binary-compatibility 4-value deconstruction matching the pre-<see cref="Trigger"/> shape.
    /// </summary>
    public void Deconstruct(
        out UserInput input,
        out string receiptId,
        out DateTimeOffset queuedAt,
        out ResumeSentinel? resume)
    {
        input = Input;
        receiptId = ReceiptId;
        queuedAt = QueuedAt;
        resume = Resume;
    }
}
