using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

/// <summary>
///     Marker message indicating a turn is complete (ResultEvent received from CLI).
///     Used by SubscribeToMessagesAsync to signal end of a response cycle.
/// </summary>
/// <remarks>
///     The SDK distinguishes <see cref="Subtype"/>: <c>success</c> vs.
///     <c>error_during_execution</c>, <c>error_max_turns</c>, <c>error_max_budget_usd</c>,
///     <c>error_max_structured_output_retries</c>. The CLI's <c>is_error</c> flag is
///     <strong>not</strong> a reliable signal — it remains <c>false</c> on
///     <c>error_during_execution</c> even though the run failed (e.g., when an internal
///     CLI exception is swallowed). Always inspect <see cref="Subtype"/> to detect failure;
///     <see cref="IsError"/> is true when <see cref="Subtype"/> is anything other than
///     <c>success</c>.
/// </remarks>
public record ResultEventMessage : IMessage
{
    /// <summary>
    ///     Result subtype as reported by the SDK (<c>success</c> on a clean turn, otherwise
    ///     one of the documented error variants such as <c>error_during_execution</c>).
    /// </summary>
    public string? Subtype { get; init; }

    /// <summary>
    ///     True if <see cref="Subtype"/> is anything other than <c>success</c>. Use this to
    ///     decide whether the turn produced usable model output. The CLI's raw <c>is_error</c>
    ///     flag is unreliable and should not be used directly.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>
    ///     The result text on success (<see cref="Subtype"/> == <c>success</c>); null on errors.
    /// </summary>
    public string? Result { get; init; }

    /// <summary>
    ///     Error messages reported by the SDK on non-success subtypes (typically captured
    ///     internal exceptions, e.g. a stray TypeError in the CLI's request-prep pipeline).
    ///     Empty on success.
    /// </summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>
    ///     Number of turns the SDK reports for this run. NumTurns of 2 with empty
    ///     <see cref="Result"/> on a failed subtype means the model was never invoked.
    /// </summary>
    public int? NumTurns { get; init; }

    /// <summary>
    ///     Wall-clock duration of the turn in milliseconds.
    /// </summary>
    public int? DurationMs { get; init; }

    /// <summary>
    ///     Time spent in the model API in milliseconds. <c>0</c> with non-success
    ///     <see cref="Subtype"/> indicates the CLI never issued the API request.
    /// </summary>
    public int? DurationApiMs { get; init; }

    // IMessage implementation
    public Role Role => Role.Assistant;
    public string? FromAgent => "claude-agent-sdk";
    public string? GenerationId => null;
    public ImmutableDictionary<string, object>? Metadata => null;
}
