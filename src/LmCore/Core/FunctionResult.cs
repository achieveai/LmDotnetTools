namespace AchieveAi.LmDotnetTools.LmCore.Core;

/// <summary>
///     What a <see cref="FunctionAttribute" /> method returns when it needs to tell the caller
///     that the operation failed, rather than that it succeeded and produced the word "Error".
/// </summary>
/// <remarks>
///     <para>
///         A reflective handler cannot tell a domain failure from a successful answer: both are
///         just a returned string, so both reach the model with <c>IsError = false</c> and only
///         an unhandled .NET exception is ever marked as an error. A model has no reliable way
///         to distinguish "the task was deleted" from "no such task", and a host cannot count
///         tool failures at all.
///     </para>
///     <para>
///         This type is opt-in and additive. A method that returns <see cref="string" /> keeps
///         today's behaviour exactly; changing its return type to <see cref="FunctionResult" />
///         is the opt-in. Because <see cref="string" /> converts implicitly, the success paths
///         of a converted method need no edit — only the failure paths change, to name a code.
///     </para>
///     <para>
///         The wire shape does not change either way: only <see cref="Text" /> is serialized, so
///         a tool that starts reporting errors still returns the same JSON string it always did.
///         What changes is the <c>IsError</c> flag and <c>ErrorCode</c> on the tool result.
///     </para>
/// </remarks>
public readonly record struct FunctionResult
{
    private FunctionResult(string text, string? errorCode)
    {
        Text = text;
        ErrorCode = errorCode;
    }

    /// <summary>The value delivered to the model. This is what gets serialized.</summary>
    public string Text { get; }

    /// <summary>
    ///     <see langword="null" /> on success. Non-null marks the call as failed and names a
    ///     stable, machine-readable reason (for example <c>TASK_NOT_FOUND</c>) that a host can
    ///     count or branch on without parsing the message.
    /// </summary>
    public string? ErrorCode { get; }

    public bool IsError => ErrorCode != null;

    public static FunctionResult Ok(string text)
    {
        return new FunctionResult(text, null);
    }

    /// <param name="errorCode">
    ///     Stable identifier for the failure. Prefer SCREAMING_SNAKE_CASE and treat it as part
    ///     of the tool's contract — hosts may branch on it.
    /// </param>
    /// <param name="text">Human- and model-readable explanation.</param>
    public static FunctionResult Error(string errorCode, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new FunctionResult(text, errorCode);
    }

    public static implicit operator FunctionResult(string text)
    {
        return Ok(text);
    }

    public override string ToString()
    {
        return Text;
    }
}
