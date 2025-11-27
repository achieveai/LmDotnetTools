namespace AchieveAi.LmDotnetTools.AgUi.DataObjects;

/// <summary>
///     Constants for AG-UI protocol event types.
///     Uses SCREAMING_SNAKE_CASE per AG-UI protocol specification.
/// </summary>
public static class AgUiEventTypes
{
    /// <summary>
    ///     Signals the start of a new WebSocket session
    /// </summary>
    public const string SESSION_STARTED = "SESSION_STARTED";

    /// <summary>
    ///     Signals the beginning of agent processing
    /// </summary>
    public const string RUN_STARTED = "RUN_STARTED";

    /// <summary>
    ///     Signals completion of agent processing
    /// </summary>
    public const string RUN_FINISHED = "RUN_FINISHED";

    /// <summary>
    ///     Reports errors during processing
    /// </summary>
    public const string RUN_ERROR = "RUN_ERROR";

    /// <summary>
    ///     Signals the beginning of a new text message
    /// </summary>
    public const string TEXT_MESSAGE_START = "TEXT_MESSAGE_START";

    /// <summary>
    ///     Streams chunks of text content
    /// </summary>
    public const string TEXT_MESSAGE_CONTENT = "TEXT_MESSAGE_CONTENT";

    /// <summary>
    ///     Signals completion of a text message
    /// </summary>
    public const string TEXT_MESSAGE_END = "TEXT_MESSAGE_END";

    /// <summary>
    ///     Initiates a tool/function call
    /// </summary>
    public const string TOOL_CALL_START = "TOOL_CALL_START";

    /// <summary>
    ///     Streams tool call arguments (supports incremental JSON)
    /// </summary>
    public const string TOOL_CALL_ARGS = "TOOL_CALL_ARGS";

    /// <summary>
    ///     Signals tool call completion
    /// </summary>
    public const string TOOL_CALL_END = "TOOL_CALL_END";

    /// <summary>
    ///     Provides complete state representation at a point in time
    /// </summary>
    public const string STATE_SNAPSHOT = "STATE_SNAPSHOT";

    /// <summary>
    ///     Provides incremental state updates (only changed values)
    /// </summary>
    public const string STATE_DELTA = "STATE_DELTA";

    /// <summary>
    ///     Delivers the result of a tool/function call execution
    /// </summary>
    public const string TOOL_CALL_RESULT = "TOOL_CALL_RESULT";

    /// <summary>
    ///     Signals the start of a sub-agent step or task
    /// </summary>
    public const string STEP_STARTED = "STEP_STARTED";

    /// <summary>
    ///     Signals the completion of a sub-agent step or task
    /// </summary>
    public const string STEP_FINISHED = "STEP_FINISHED";

    /// <summary>
    ///     Signals the start of reasoning (chain-of-thought) with optional encrypted content
    /// </summary>
    public const string REASONING_START = "REASONING_START";

    /// <summary>
    ///     Signals the beginning of a streaming reasoning message
    /// </summary>
    public const string REASONING_MESSAGE_START = "REASONING_MESSAGE_START";

    /// <summary>
    ///     Streams reasoning content chunks
    /// </summary>
    public const string REASONING_MESSAGE_CONTENT = "REASONING_MESSAGE_CONTENT";

    /// <summary>
    ///     Signals completion of a reasoning message
    /// </summary>
    public const string REASONING_MESSAGE_END = "REASONING_MESSAGE_END";

    /// <summary>
    ///     Convenience event combining start/end logic for reasoning chunks
    /// </summary>
    public const string REASONING_MESSAGE_CHUNK = "REASONING_MESSAGE_CHUNK";

    /// <summary>
    ///     Signals the end of the reasoning phase
    /// </summary>
    public const string REASONING_END = "REASONING_END";
}
