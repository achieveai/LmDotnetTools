using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

[JsonConverter(typeof(ToolsCallMessageJsonConverter))]
public record ToolsCallMessage : IMessage, ICanGetToolCalls
{
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

    [JsonPropertyName("tool_calls")]
    public ImmutableList<ToolCall> ToolCalls { get; init; } = [];

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    public IEnumerable<ToolCall>? GetToolCalls() => ToolCalls.Count > 0 ? ToolCalls : null;

    public static string? GetText() => null;

    public static BinaryData? GetBinary() => null;

    public static IEnumerable<IMessage>? GetMessages() => null;
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

    [JsonPropertyName("tool_call_updates")]
    public ImmutableList<ToolCallUpdate> ToolCallUpdates { get; init; } = [];

    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }
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
    public string? FromAgent { get; init; } = null;

    public Role Role { get; init; } = Role.Assistant;

    public ImmutableDictionary<string, object>? Metadata { get; private set; } = null;

    public string? GenerationId { get; set; } = null;

    public Action<ToolCall> OnToolCall { get; init; } = _ => { };

    IMessage IMessageBuilder.Build()
    {
        return this.Build();
    }

    public string? CurrentFunctionName { get; private set; } = null;

    public string AccumulatedArgs { get; private set; } = "";

    public string? CurrentToolCallId { get; private set; } = null;

    public int? CurrentIndex { get; private set; } = null;

    public ImmutableList<ToolCall> CompletedToolCalls { get; private set; } = [];

    public string? ThreadId { get; set; }

    public string? RunId { get; set; }

    public string? ParentRunId { get; set; }

    public void Add(ToolsCallUpdateMessage streamingMessageUpdate)
    {
        // Capture GenerationId from the message update if not already set
        if (GenerationId == null && streamingMessageUpdate.GenerationId != null)
        {
            GenerationId = streamingMessageUpdate.GenerationId;
        }

        // Process each update
        foreach (var update in streamingMessageUpdate.ToolCallUpdates)
        {
            // Check if this update completes a current tool call based on Id or Index
            bool isNewToolCall = false;

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
        };
    }
}
