namespace AchieveAi.LmDotnetTools.AgUi.DataObjects;

/// <summary>
/// Constants for AG-UI protocol event types.
/// Uses SCREAMING_SNAKE_CASE per AG-UI protocol specification.
/// </summary>
public static class AgUiEventTypes
{
    /// <summary>
    /// Signals the beginning of agent processing
    /// </summary>
    public const string RUN_STARTED = "RUN_STARTED";

    /// <summary>
    /// Signals completion of agent processing
    /// </summary>
    public const string RUN_FINISHED = "RUN_FINISHED";

    /// <summary>
    /// Reports errors during processing
    /// </summary>
    public const string RUN_ERROR = "RUN_ERROR";

    /// <summary>
    /// Signals the beginning of a new text message
    /// </summary>
    public const string TEXT_MESSAGE_START = "TEXT_MESSAGE_START";

    /// <summary>
    /// Streams chunks of text content
    /// </summary>
    public const string TEXT_MESSAGE_CONTENT = "TEXT_MESSAGE_CONTENT";

    /// <summary>
    /// Signals completion of a text message
    /// </summary>
    public const string TEXT_MESSAGE_END = "TEXT_MESSAGE_END";

    /// <summary>
    /// Initiates a tool/function call
    /// </summary>
    public const string TOOL_CALL_START = "TOOL_CALL_START";

    /// <summary>
    /// Streams tool call arguments (supports incremental JSON)
    /// </summary>
    public const string TOOL_CALL_ARGS = "TOOL_CALL_ARGS";

    /// <summary>
    /// Signals tool call completion
    /// </summary>
    public const string TOOL_CALL_END = "TOOL_CALL_END";

    /// <summary>
    /// Provides complete state representation at a point in time
    /// </summary>
    public const string STATE_SNAPSHOT = "STATE_SNAPSHOT";

    /// <summary>
    /// Provides incremental state updates (only changed values)
    /// </summary>
    public const string STATE_DELTA = "STATE_DELTA";
}
