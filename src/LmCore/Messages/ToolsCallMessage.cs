using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(ToolsCallMessageJsonConverter))]
public record ToolsCallMessage : IMessage, ICanGetToolCalls
{
    [JsonPropertyName("tool_calls")]
    public ImmutableList<ToolCall> ToolCalls { get; init; } = [];

    public IEnumerable<ToolCall>? GetToolCalls()
    {
        return ToolCalls.Count > 0 ? ToolCalls : null;
    }

    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

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

    public static string? GetText()
    {
        return null;
    }

    public static BinaryData? GetBinary()
    {
        return null;
    }

    public static IEnumerable<IMessage>? GetMessages()
    {
        return null;
    }
}

public class ToolsCallMessageJsonConverter : ShadowPropertiesJsonConverter<ToolsCallMessage>
{
    protected override ToolsCallMessage CreateInstance()
    {
        return new ToolsCallMessage();
    }
}

[JsonConverter(typeof(ToolsCallMessageJsonConverter))]
public record ToolsCallUpdateMessage : IMessage
{
    [JsonPropertyName("tool_call_updates")]
    public ImmutableList<ToolCallUpdate> ToolCallUpdates { get; init; } = [];

    [JsonPropertyName("from_agent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; } = null;

    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; } = null;

    [JsonPropertyName("generation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; } = null;

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
    /// Chunk index within the same messageOrderIdx for streaming updates.
    /// Multiple chunks can belong to the same message during streaming.
    /// Note: A chunk represents partial updates to tool calls, not multiple distinct tool calls.
    /// </summary>
    [JsonPropertyName("chunkIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ChunkIdx { get; init; }
}

public class ToolsCallUpdateMessageJsonConverter : ShadowPropertiesJsonConverter<ToolsCallUpdateMessage>
{
    protected override ToolsCallUpdateMessage CreateInstance()
    {
        return new ToolsCallUpdateMessage();
    }
}

public class ToolsCallMessageBuilder : IMessageBuilder<ToolsCallMessage, ToolsCallUpdateMessage>
{
    public ImmutableDictionary<string, object>? Metadata { get; private set; }

    public string? GenerationId { get; set; }

    public Action<ToolCall> OnToolCall { get; init; } = _ => { };

    public string? CurrentFunctionName { get; private set; }

    public string AccumulatedArgs { get; private set; } = "";

    public string? CurrentToolCallId { get; private set; }

    public int? CurrentIndex { get; private set; }

    public ExecutionTarget CurrentExecutionTarget { get; private set; } = ExecutionTarget.LocalFunction;

    public ImmutableList<ToolCall> CompletedToolCalls { get; private set; } = [];

    public string? ThreadId { get; set; }

    public string? RunId { get; set; }

    public string? ParentRunId { get; set; }

    public int? MessageOrderIdx { get; set; }
    public string? FromAgent { get; init; } = null;

    public Role Role { get; init; } = Role.Assistant;

    IMessage IMessageBuilder.Build()
    {
        return Build();
    }

    public void Add(ToolsCallUpdateMessage streamingMessageUpdate)
    {
        ArgumentNullException.ThrowIfNull(streamingMessageUpdate);
        // Capture GenerationId from the message update if not already set
        if (GenerationId == null && streamingMessageUpdate.GenerationId != null)
        {
            GenerationId = streamingMessageUpdate.GenerationId;
        }

        // Process each update
        foreach (var update in streamingMessageUpdate.ToolCallUpdates)
        {
            // Check if this update completes a current tool call based on Id or Index
            var isNewToolCall = false;

            // Rule 0: If we have both IDs (non-null) and they're different, it's a new tool call
            if (CurrentToolCallId != null && update.ToolCallId != null && CurrentToolCallId != update.ToolCallId)
            {
                CompleteCurrentToolCall();
                isNewToolCall = true;
            }
            // Rule 1: If we have both Indexes (non-null) and they're different, it's a new tool call
            else if (CurrentIndex != null && update.Index != null && CurrentIndex != update.Index)
            {
                CompleteCurrentToolCall();
                isNewToolCall = true;
            }

            // If update contains a function name, it's the start of a new tool call
            if (isNewToolCall || (update.FunctionName != null && CurrentFunctionName == null))
            {
                // Start a new tool call
                CurrentFunctionName = update.FunctionName;
                AccumulatedArgs = update.FunctionArgs ?? "";
                CurrentToolCallId = update.ToolCallId;
                CurrentIndex = update.Index;
                CurrentExecutionTarget = update.ExecutionTarget;
            }
            // Otherwise, it's an update to the current partial tool call
            else if (CurrentFunctionName != null && update.FunctionArgs != null)
            {
                AccumulatedArgs += update.FunctionArgs;

                // Update tool call ID if it's now provided
                if (CurrentToolCallId == null && update.ToolCallId != null)
                {
                    CurrentToolCallId = update.ToolCallId;
                }

                // Update index if it's now provided
                if (CurrentIndex == null && update.Index != null)
                {
                    CurrentIndex = update.Index;
                }
            }
            else if (CurrentFunctionName != null)
            {
                // Preserve explicit execution target updates even when args are not present.
                CurrentExecutionTarget = update.ExecutionTarget;
            }
        }

        // Merge metadata from the update
        if (streamingMessageUpdate.Metadata != null)
        {
            if (Metadata == null)
            {
                Metadata = streamingMessageUpdate.Metadata;
            }
            else
            {
                // Merge metadata, with update's metadata taking precedence
                foreach (var prop in streamingMessageUpdate.Metadata)
                {
                    Metadata = Metadata.Add(prop.Key, prop.Value);
                }
            }
        }
    }

    public ToolsCallMessage Build()
    {
        // Rule 2: When build is called, complete any final partial update
        CompleteCurrentToolCall();
        var toolCalls = CompletedToolCalls;
        CompletedToolCalls = [];

        return new ToolsCallMessage
        {
            FromAgent = FromAgent,
            Role = Role,
            Metadata = Metadata,
            GenerationId = GenerationId,
            ToolCalls = toolCalls,
            ThreadId = ThreadId,
            RunId = RunId,
            ParentRunId = ParentRunId,
            MessageOrderIdx = MessageOrderIdx,
        };
    }

    private void CompleteCurrentToolCall()
    {
        if (CurrentFunctionName != null)
        {
            var toolCall = new ToolCall
            {
                FunctionName = CurrentFunctionName,
                FunctionArgs = AccumulatedArgs,
                ToolCallId = CurrentToolCallId,
                Index = CurrentIndex,
                ToolCallIdx = CompletedToolCalls.Count, // Assign sequential index (0, 1, 2...)
                ExecutionTarget = CurrentExecutionTarget,
            };

            // Add to completed tool calls
            CompletedToolCalls = CompletedToolCalls.Add(toolCall);

            // Invoke callback for completed tool call
            OnToolCall(toolCall);

            // Reset the current tool call state
            CurrentFunctionName = null;
            AccumulatedArgs = "";
            CurrentToolCallId = null;
            CurrentIndex = null;
            CurrentExecutionTarget = ExecutionTarget.LocalFunction;
        }
    }
}
