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
///         What changes is the <c>IsError</c> flag and <c>ErrorCode</c> on the tool result. The
///         contract a provider advertises therefore names <see cref="string" /> as well — the
///         model never sees this wrapper.
///     </para>
///     <para>
///         Being a struct, <c>default(FunctionResult)</c> is reachable without going through
///         either factory — an uninitialized field, an array slot, a <c>default</c> switch arm.
///         That value is deliberately <em>not</em> a success: it reports
///         <see cref="UninitializedErrorCode" /> so a bug that never assigned a result surfaces
///         as a failed tool call instead of reaching the model as an empty success.
///     </para>
/// </remarks>
public readonly record struct FunctionResult
{
    /// <summary>
    ///     Error code reported by <c>default(FunctionResult)</c>. Follows the repository's
    ///     lower_snake_case convention for tool error codes.
    /// </summary>
    public const string UninitializedErrorCode = "uninitialized_function_result";

    private const string UninitializedText =
        "Error: the tool produced an uninitialized FunctionResult. "
        + "Return FunctionResult.Ok, FunctionResult.Error, or a string.";

    private readonly string? _errorCode;

    private readonly string? _text;

    private FunctionResult(string text, string? errorCode)
    {
        _text = text;
        _errorCode = errorCode;
    }

    /// <summary>The value delivered to the model. This is what gets serialized.</summary>
    public string Text => _text ?? UninitializedText;

    /// <summary>
    ///     <see langword="null" /> on success. Non-null marks the call as failed and names a
    ///     stable, machine-readable reason (for example <c>task_not_found</c>) that a host can
    ///     count or branch on without parsing the message.
    /// </summary>
    /// <remarks>
    ///     A <c>default</c> instance never reached a factory, so it has no text to deliver; it
    ///     reports <see cref="UninitializedErrorCode" /> rather than passing for a success.
    /// </remarks>
    public string? ErrorCode => _errorCode ?? (_text == null ? UninitializedErrorCode : null);

    public bool IsError => ErrorCode != null;

    public static FunctionResult Ok(string text)
    {
        // Never store null: a null text is what distinguishes an uninitialized struct from a
        // deliberate empty answer, and Ok(null) is the latter.
        return new FunctionResult(text ?? string.Empty, null);
    }

    /// <param name="errorCode">
    ///     Stable identifier for the failure. Use lower_snake_case — that is what every error
    ///     code already in this repository uses, and consumers such as
    ///     <c>MultiTurnAgentLoop.ClassifyToolOutcome</c> match codes exactly, so a
    ///     differently-cased code silently falls through to the default arm. Treat it as part
    ///     of the tool's contract; hosts may branch on it.
    /// </param>
    /// <param name="text">Human- and model-readable explanation.</param>
    public static FunctionResult Error(string errorCode, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        return new FunctionResult(text ?? string.Empty, errorCode);
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
