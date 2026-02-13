using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

public record ToolCall
{
    [JsonPropertyName("function_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionName { get; init; }

    [JsonPropertyName("function_args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionArgs { get; init; }

    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Index { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    /// <summary>
    ///     Order index of this tool call within its containing ToolCallMessage.
    ///     Enables deterministic reconstruction of tool call order for KV cache optimization.
    /// </summary>
    [JsonPropertyName("toolCallIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ToolCallIdx { get; init; }

    /// <summary>
    ///     Distinguishes local function tools from provider-executed server tools.
    /// </summary>
    [JsonPropertyName("execution_target")]
    public ExecutionTarget ExecutionTarget { get; init; } = ExecutionTarget.LocalFunction;
}

/// <summary>
/// Represents the result of a tool call execution.
/// Supports both text-only and multi-modal (text + images) results.
/// </summary>
public readonly record struct ToolCallResult
{
    /// <summary>
    /// Creates a text-only tool call result.
    /// </summary>
    public ToolCallResult(
        string? toolCallId,
        string result,
        ExecutionTarget executionTarget = ExecutionTarget.LocalFunction)
    {
        ToolCallId = toolCallId;
        Result = result;
        ContentBlocks = null;
        ToolName = null;
        IsError = false;
        ErrorCode = null;
        ExecutionTarget = executionTarget;
    }

    /// <summary>
    /// Creates a multi-modal tool call result with text and content blocks.
    /// </summary>
    public ToolCallResult(
        string? toolCallId,
        string result,
        IList<ToolResultContentBlock>? contentBlocks,
        ExecutionTarget executionTarget = ExecutionTarget.LocalFunction)
    {
        ToolCallId = toolCallId;
        Result = result;
        ContentBlocks = contentBlocks;
        ToolName = null;
        IsError = false;
        ErrorCode = null;
        ExecutionTarget = executionTarget;
    }

    /// <summary>
    /// The unique identifier for this tool call.
    /// </summary>
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    /// <summary>
    /// The text result of the tool call.
    /// </summary>
    [JsonPropertyName("result")]
    public string Result { get; init; }

    /// <summary>
    /// Optional multi-modal content blocks (text, images) from MCP tool results.
    /// When present, provides richer content than the text-only Result.
    /// </summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<ToolResultContentBlock>? ContentBlocks { get; init; }

    /// <summary>
    /// The tool name that produced this result.
    /// </summary>
    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    /// <summary>
    /// Whether this result indicates an error.
    /// </summary>
    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    /// <summary>
    /// Optional provider/tool-specific error code.
    /// </summary>
    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    /// <summary>
    /// Distinguishes local function tools from provider-executed server tools.
    /// </summary>
    [JsonPropertyName("execution_target")]
    public ExecutionTarget ExecutionTarget { get; init; } = ExecutionTarget.LocalFunction;
}

/// <summary>
/// Represents a single tool call result as a message.
/// This is the singular version of ToolsCallResultMessage, containing a single result.
/// </summary>
[JsonConverter(typeof(ToolCallResultMessageJsonConverter))]
public record ToolCallResultMessage : IMessage
{
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("result")]
    public required string Result { get; init; }

    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    [JsonPropertyName("is_error")]
    public bool IsError { get; init; }

    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("execution_target")]
    public ExecutionTarget ExecutionTarget { get; init; } = ExecutionTarget.LocalFunction;

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.User;

    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    [JsonIgnore]
    public System.Collections.Immutable.ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }

    /// <summary>
    /// Multi-modal content blocks from MCP tool results.
    /// Contains text and/or images returned by the tool.
    /// Optional for backwards compatibility - if null, use Result string.
    /// </summary>
    [JsonPropertyName("content_blocks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<ToolResultContentBlock>? ContentBlocks { get; init; }

    /// <summary>
    /// Converts this message to a ToolCallResult struct.
    /// </summary>
    public ToolCallResult ToToolCallResult()
    {
        return new ToolCallResult(ToolCallId, Result, ContentBlocks)
        {
            ToolName = ToolName,
            IsError = IsError,
            ErrorCode = ErrorCode,
            ExecutionTarget = ExecutionTarget,
        };
    }

    /// <summary>
    /// Creates a ToolCallResultMessage from a ToolCallResult struct.
    /// Preserves ContentBlocks for multi-modal tool results.
    /// </summary>
    public static ToolCallResultMessage FromToolCallResult(
        ToolCallResult result,
        Role role = Role.User,
        string? fromAgent = null,
        string? generationId = null,
        System.Collections.Immutable.ImmutableDictionary<string, object>? metadata = null,
        string? threadId = null,
        string? runId = null,
        string? parentRunId = null,
        int? messageOrderIdx = null)
    {
        return new ToolCallResultMessage
        {
            ToolCallId = result.ToolCallId,
            Result = result.Result,
            ContentBlocks = result.ContentBlocks,
            ToolName = result.ToolName,
            IsError = result.IsError,
            ErrorCode = result.ErrorCode,
            ExecutionTarget = result.ExecutionTarget,
            Role = role,
            FromAgent = fromAgent,
            GenerationId = generationId,
            Metadata = metadata,
            ThreadId = threadId,
            RunId = runId,
            ParentRunId = parentRunId,
            MessageOrderIdx = messageOrderIdx,
        };
    }
}

/// <summary>
/// JSON converter for ToolCallResultMessage that supports the shadow properties pattern.
/// </summary>
public class ToolCallResultMessageJsonConverter : ShadowPropertiesJsonConverter<ToolCallResultMessage>
{
    protected override ToolCallResultMessage CreateInstance()
    {
        return new ToolCallResultMessage { Result = string.Empty };
    }
}

public record ToolCallUpdate
{
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int? Index { get; init; }

    [JsonPropertyName("function_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionName { get; init; }

    [JsonPropertyName("function_args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FunctionArgs { get; init; }

    /// <summary>
    ///     Distinguishes local function tools from provider-executed server tools.
    /// </summary>
    [JsonPropertyName("execution_target")]
    public ExecutionTarget ExecutionTarget { get; init; } = ExecutionTarget.LocalFunction;

    /// <summary>
    ///     Structured JSON fragment updates generated from the function arguments
    /// </summary>
    [JsonPropertyName("json_update_fragments")]
    public IList<JsonFragmentUpdate>? JsonFragmentUpdates { get; init; }
}

/// <summary>
/// Represents a streaming tool call update from a language model.
/// Contains the current accumulated tool call state at a point in time during streaming.
/// </summary>
[JsonConverter(typeof(ToolCallUpdateMessageJsonConverter))]
public record ToolCallUpdateMessage : ToolCallUpdate, IMessage
{
    /// <summary>
    /// The role of the message sender (typically Assistant for LM responses).
    /// </summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>
    /// The name or identifier of the agent that generated this message.
    /// </summary>
    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <summary>
    /// A unique identifier for the generation this update is part of.
    /// </summary>
    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <summary>
    /// Additional metadata associated with the message.
    /// </summary>
    [JsonIgnore]
    public System.Collections.Immutable.ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>
    /// Thread identifier for conversation continuity (used with AG-UI protocol).
    /// </summary>
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <summary>
    /// Run identifier for this specific execution (used with AG-UI protocol).
    /// </summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>
    /// Parent Run identifier for branching/time travel (creates git-like lineage).
    /// </summary>
    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    /// <summary>
    /// Order index of this message within its generation (same GenerationId).
    /// </summary>
    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }

    /// <summary>
    /// Chunk index within the same messageOrderIdx for streaming updates.
    /// Multiple chunks can belong to the same message during streaming.
    /// Note: A chunk represents partial updates to a single tool call, not multiple tool calls.
    /// </summary>
    [JsonPropertyName("chunkIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ChunkIdx { get; init; }

    /// <summary>
    /// Indicates this is a streaming update rather than a complete message.
    /// </summary>
    [JsonPropertyName("isUpdate")]
    public bool IsUpdate { get; init; } = true;

    /// <summary>
    /// Converts this update to a complete ToolCallMessage.
    /// </summary>
    /// <returns>A ToolCallMessage with the same content and properties.</returns>
    public ToolCallMessage ToToolCallMessage()
    {
        return new ToolCallMessage
        {
            ToolCallId = ToolCallId,
            Index = Index,
            FunctionName = FunctionName,
            FunctionArgs = FunctionArgs,
            ExecutionTarget = ExecutionTarget,
            Role = Role,
            FromAgent = FromAgent,
            GenerationId = GenerationId,
            Metadata = Metadata,
            ThreadId = ThreadId,
            RunId = RunId,
            ParentRunId = ParentRunId,
            MessageOrderIdx = MessageOrderIdx,
        };
    }
}

/// <summary>
/// JSON converter for ToolCallUpdateMessage that supports the shadow properties pattern.
/// </summary>
public class ToolCallUpdateMessageJsonConverter : ShadowPropertiesJsonConverter<ToolCallUpdateMessage>
{
    /// <summary>
    /// Creates a new instance of ToolCallUpdateMessage during deserialization.
    /// </summary>
    /// <returns>A minimal ToolCallUpdateMessage instance.</returns>
    protected override ToolCallUpdateMessage CreateInstance()
    {
        return new ToolCallUpdateMessage();
    }
}

/// <summary>
/// Base class for content blocks in MCP tool results.
/// Supports polymorphic serialization for text and image content.
/// The "type" discriminator is handled automatically by JsonPolymorphic.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TextToolResultBlock), "text")]
[JsonDerivedType(typeof(ImageToolResultBlock), "image")]
public abstract record ToolResultContentBlock;

/// <summary>
/// Text content block for tool results.
/// </summary>
public record TextToolResultBlock : ToolResultContentBlock
{
    /// <summary>
    /// The text content.
    /// </summary>
    [JsonPropertyName("text")]
    public required string Text { get; init; }
}

/// <summary>
/// Image content block for tool results.
/// Contains base64-encoded image data with MIME type.
/// </summary>
public record ImageToolResultBlock : ToolResultContentBlock
{
    /// <summary>
    /// Base64-encoded image data.
    /// </summary>
    [JsonPropertyName("data")]
    public required string Data { get; init; }

    /// <summary>
    /// MIME type of the image (e.g., "image/png", "image/jpeg").
    /// Detected from actual bytes, not the data URL.
    /// </summary>
    [JsonPropertyName("mimeType")]
    public required string MimeType { get; init; }
}
