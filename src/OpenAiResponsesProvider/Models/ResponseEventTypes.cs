namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

/// <summary>
///     Canonical event-type strings for the OpenAI Responses API event stream.
///     These constants match the wire-format <c>type</c> field exactly and are shared
///     between provider, mock handler, and WebSocket emitter to avoid string drift.
/// </summary>
public static class ResponseEventTypes
{
    public const string ResponseCreated = "response.created";
    public const string ResponseInProgress = "response.in_progress";
    public const string ResponseCompleted = "response.completed";
    public const string ResponseFailed = "response.failed";

    public const string OutputItemAdded = "response.output_item.added";
    public const string OutputItemDone = "response.output_item.done";

    public const string ContentPartAdded = "response.content_part.added";
    public const string ContentPartDone = "response.content_part.done";

    public const string OutputTextDelta = "response.output_text.delta";
    public const string OutputTextDone = "response.output_text.done";

    public const string FunctionCallArgumentsDelta = "response.function_call_arguments.delta";
    public const string FunctionCallArgumentsDone = "response.function_call_arguments.done";

    // Reasoning-summary streaming. Reasoning-capable models (e.g. GPT-5.5) deliver their
    // human-readable reasoning summary via these dedicated events — NOT inside the reasoning
    // output_item's "summary" array, which is empty while streaming. Without handling these,
    // requested reasoning summaries are silently dropped (no thinking blocks).
    public const string ReasoningSummaryTextDelta = "response.reasoning_summary_text.delta";
    public const string ReasoningSummaryTextDone = "response.reasoning_summary_text.done";

    /// <summary>
    ///     Client→server frame requesting a response generation. Currently the only
    ///     client-originated frame the mock host accepts.
    /// </summary>
    public const string ClientResponseCreate = "response.create";
}
